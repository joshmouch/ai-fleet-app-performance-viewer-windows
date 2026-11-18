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
    private sealed class Source(SourceConfig config)
    {
        public SourceConfig Config { get; } = config;
        public PerformanceWindow? Frame;
        public GuestDiskWindow? Guest;
        public DateTimeOffset? GuestEnd;
        public string? Status = "Waiting for first sample";
    }

    private readonly Source[] sources;
    private readonly object gate = new();

    public WindowSources(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Sources").Get<SourceConfig[]>();
        if (configured == null && OperatingSystem.IsWindows())
            configured = [new() { Name = "Local Windows", Command = "aifleet-performance-viewer.exe", Arguments = ["--collect", "--guests"] }];
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
                bool selected = !stale && (s.Guest != null || (s.Frame != null && claimed.Add(s.Frame.Host)));
                return new SourceWindow(s.Config.Name, s.Frame, s.Status, replay, stale, selected, s.Config.GuestOf, s.Guest, s.GuestEnd);
            }).ToArray();
            if (last == null || !last.Windows.SequenceEqual(windows))
                last = new(windows, $"\"{Guid.NewGuid():N}\"");
            return last;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stop) =>
        Task.WhenAll(sources.Select(source => Run(source, stop)));

    private async Task Run(Source source, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (source.Config.ReplayFile is string path)
                {
                    using var reader = File.OpenText(path);
                    await ReadFrames(source, reader, stop);
                    lock (gate) source.Status = source.Frame == null && source.Guest == null ? source.Status ?? "Empty replay" : "Replay complete";
                    return;
                }

                var start = new ProcessStartInfo(source.Config.Command!)
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
                    lock (gate) source.Status = $"Collector exited ({process.ExitCode})";
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or
                System.Text.Json.JsonException or InvalidDataException)
            {
                lock (gate) source.Status = ex.Message;
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
                    lock (gate) { source.Guest = null; source.GuestEnd = null; source.Status = error; }
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
            }
        }
    }
}
