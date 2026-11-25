using System.ComponentModel;
using System.Diagnostics;
using PerformanceViewer.Components;

namespace PerformanceViewer.Web;

internal sealed class SourceConfig
{
    public string Name { get; set; } = "";
    public string? GuestOf { get; set; }
    public string? Command { get; set; }
    public string[] Arguments { get; set; } = [];
    public string? ReplayFile { get; set; }
    public int StaleAfterSeconds { get; set; } = 30;
}

internal sealed class WindowSources : BackgroundService
{
    private sealed class Source(SourceConfig config, string? checkout = null)
    {
        public SourceConfig Config { get; } = config;
        public string? Checkout { get; } = checkout;
        public PerformanceWindow? Frame;
        public GuestDiskWindow? Guest;
        public DateTimeOffset? GuestEnd;
        public string? Status = "Waiting for first sample";
        public bool Loading = true;
    }

    private readonly Source[] sources;
    private readonly object gate = new();
    private readonly IHostApplicationLifetime lifetime;
    private readonly string? sourceRuntime;

    public WindowSources(IConfiguration configuration, IHostApplicationLifetime lifetime)
    {
        this.lifetime = lifetime;
        sourceRuntime = configuration["ViewerSourceRuntime"];
        var configured = configuration.GetSection("Sources").Get<SourceConfig[]>();
        if (configured == null && OperatingSystem.IsWindows() && configuration["ViewerSourceCheckout"] is { Length: > 0 } checkout)
        {
            sources = [new(new SourceConfig { Name = "Windows checkout", Arguments = ["--collect", "--guests", "--resources", "--interval-windows"] }, checkout)];
            return;
        }
        if (configured == null && OperatingSystem.IsWindows())
            configured = [new() { Name = "Local Windows", Command = configuration["ViewerCollector"] ?? "aifleet-performance-viewer.exe",
                Arguments = configuration["ViewerCollector"] == null ? ["--collect", "--guests"] : ["--collect", "--guests", "--resources", "--interval-windows"] }];
        sources = (configured ?? []).Select(c =>
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.StaleAfterSeconds < 1 ||
                (string.IsNullOrWhiteSpace(c.Command) == string.IsNullOrWhiteSpace(c.ReplayFile)))
                throw new InvalidDataException("Each named source needs one command or replay file and positive freshness.");
            return new Source(c);
        }).ToArray();
        if (sources.Select(s => s.Config.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new InvalidDataException("Source names must be unique.");
    }

    public sealed record Snapshot(SourceWindow[] Windows, string Tag);
    private Snapshot? last;

    public Snapshot Read()
    {
        lock (gate)
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            var windows = sources.Select(s =>
            {
                bool replay = s.Config.ReplayFile != null;
                var end = s.Frame?.CpuEnd ?? s.GuestEnd;
                bool stale = !replay && end.HasValue && (s.Status != null ||
                    now - end.Value > TimeSpan.FromSeconds(s.Config.StaleAfterSeconds));
                bool scoped = s.Frame != null && s.Frame.DiskPaths.Scope != "host";
                bool selected = !scoped && !stale && (s.Guest != null || (s.Frame != null && claimed.Add(s.Frame.Host)));
                return new SourceWindow(s.Config.Name, s.Frame, scoped ? "Process-scoped capture; excluded from host aggregate" : s.Status,
                    replay, stale, selected, s.Config.GuestOf, s.Guest, s.GuestEnd, s.Loading);
            }).ToArray();
            if (last == null || !last.Windows.SequenceEqual(windows))
                last = new(windows, $"\"{Guid.NewGuid():N}\"");
            return last;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Only the host that successfully binds the viewer port owns its collectors.
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (lifetime.ApplicationStarted.Register(() => listening.TrySetResult()))
            await listening.Task.WaitAsync(stop);
        await Task.WhenAll(sources.Select(source => Run(source, stop)));
    }

    private async Task Run(Source source, CancellationToken stop)
    {
        var command = source.Config.Command;
        if (source.Checkout is string checkout)
        {
            lock (gate) source.Status = "Building the Windows collector from the checkout…";
            try
            {
                if (sourceRuntime == null || !Path.IsPathFullyQualified(sourceRuntime))
                    throw new InvalidDataException("Update the installed Source shell to build and run the checkout collector.");
                command = await WindowsCheckoutCollector.Build(checkout, sourceRuntime, stop);
                lock (gate) source.Status = "Waiting for the first checkout sample";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
            {
                lock (gate) { source.Status = error.Message; source.Loading = false; }
                return; // The source watcher will restart the server on the next source edit.
            }
        }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (source.Config.ReplayFile is string path)
                {
                    using var reader = File.OpenText(path);
                    await ReadFrames(source, reader, stop);
                    lock (gate)
                    {
                        if (source.Loading) source.Status = "No samples in this replay.";
                        else if (source.Status == null) source.Status = "Replay complete";
                        source.Loading = false;
                    }
                    return;
                }

                var start = new ProcessStartInfo(command!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardInput = source.Config.GuestOf != null
                };
                foreach (var arg in source.Config.Arguments) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Collector could not start.");
                try
                {
                    if (start.RedirectStandardInput)
                    {
                        using var sampler = typeof(WindowSources).Assembly.GetManifestResourceStream("GuestDiskIo.py")!;
                        await sampler.CopyToAsync(process.StandardInput.BaseStream, stop);
                        process.StandardInput.Close();
                    }
                    await ReadFrames(source, process.StandardOutput, stop);
                    await process.WaitForExitAsync(stop);
                    lock (gate) { source.Status = $"Collector exited ({process.ExitCode})"; source.Loading = false; }
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        CollectorProcess.RequestStop(process);
                        // A collector emits its last measured window during termination.
                        // Keep reading so a full stdout pipe cannot hold the child open.
                        var drain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (TimeoutException)
                        {
                            if (!process.HasExited) process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync();
                        }
                        await drain;
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or
                System.Text.Json.JsonException or InvalidDataException)
            {
                lock (gate) { source.Status = ex.Message; source.Loading = false; }
                if (source.Config.ReplayFile != null) return;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stop);
        }
    }

    private async Task ReadFrames(Source source, StreamReader reader, CancellationToken stop)
    {
        while (await reader.ReadLineAsync(stop) is string line)
        {
            if (source.Config.GuestOf != null)
            {
                var sample = System.Text.Json.JsonSerializer.Deserialize<GuestDiskLine>(line);
                if (sample?.Samples == null || sample.Errors == null)
                    throw new InvalidDataException("Expected a guest disk sample.");
                if (sample.Errors.TryGetValue("root", out var error))
                {
                    lock (gate) { source.Guest = null; source.GuestEnd = null; source.Status = error; source.Loading = false; }
                    continue;
                }
                if (!sample.Samples.TryGetValue("root", out var root) || root?.Rows == null || root.Seconds <= 0)
                    throw new InvalidDataException("Missing root filesystem sample or duration.");
                var files = new Dictionary<FileIoKey, FileIoTotals>();
                foreach (var row in root.Rows)
                {
                    if (row == null || row.Process == null || row.Path == null || row.ReadBytes < 0 ||
                        row.WriteBytes < 0 || row.Reads < 0 || row.Writes < 0)
                        throw new InvalidDataException("Invalid guest file counters.");
                    row.AddTo(files);
                }
                var guest = new GuestDiskWindow(source.Config.Name, source.Config.Name, root.Device, [], root.Seconds,
                    files.Select(f => new MeasuredFile(f.Key, f.Value)).ToArray(), root.Limited, null);
                lock (gate)
                {
                    source.Guest = guest;
                    source.GuestEnd = sample.End ?? DateTimeOffset.UtcNow;
                    source.Status = null;
                    source.Loading = false;
                }
                continue;
            }
            var frame = PerformanceWindow.Decode(line);
            lock (gate)
            {
                var previous = source.Frame;
                if (previous?.Capture == frame.Capture && previous.Sequence >= frame.Sequence) continue;
                source.Frame = frame;
                source.Status = null;
                source.Loading = false;
            }
        }
    }
}
