using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace PerformanceViewer.Native;

internal sealed class LocalViewerServer : IDisposable
{
    private readonly HttpClient http;
    private readonly Process? process;
    private readonly string? identity;
    private readonly FileStream? lease;
    private readonly bool watchingSource;
    private int disposed;
    private string? lastOutput;
    public Uri Address { get; }

    private LocalViewerServer(Uri address, Process? process, string? identity, FileStream? lease = null, bool watchingSource = false)
    {
        Address = address;
        this.process = process;
        this.identity = identity;
        this.lease = lease;
        this.watchingSource = watchingSource;
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(1) };
    }

    public static LocalViewerServer Start(string[] args)
    {
        var address = new UriBuilder("http", "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("ViewerPort"), out int port) ? port : 5217).Uri;
        var basePath = AppContext.BaseDirectory;
        var leaseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFleetPerformanceViewerShell");
        var (source, package) = args switch
        {
            [] => ((string?)null, basePath),
            ["--source", var path] => (Path.GetFullPath(path), basePath),
            ["--package", var path] => ((string?)null, Path.GetFullPath(path)),
            _ => throw new ArgumentException("The viewer shortcut must point to the package or an existing source checkout.")
        };
        string serverFolder = Path.Combine(package, "Server");
        if (args.Length == 0 && !Directory.Exists(serverFolder))
            return new(address, null, null); // Optional macOS or standalone loopback host.

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (source == null)
        {
            start.Environment.Remove("ViewerSourceCheckout");
            start.Environment.Remove("ViewerSourceRuntime");
            var assembly = Path.Combine(serverFolder, "PerformanceViewer.Web.dll");
            if (!File.Exists(assembly)) throw new FileNotFoundException("The package's viewer server is missing.", assembly);
            start.WorkingDirectory = serverFolder;
            start.ArgumentList.Add(assembly);
            var collector = Path.Combine(package, "Collector", "AI Fleet Cluster Performance Viewer.exe");
            if (OperatingSystem.IsWindows() && !File.Exists(collector))
                throw new FileNotFoundException("The package's Windows collector is missing.", collector);
            if (File.Exists(collector)) start.Environment["ViewerCollector"] = collector;
        }
        else
        {
            var project = Path.Combine(source, "Web", "PerformanceViewer.Web.csproj");
            if (!File.Exists(project)) throw new FileNotFoundException("The source viewer project was moved.", project);
            start.WorkingDirectory = source;
            foreach (string argument in new[] { "watch", "--project", project, "--non-interactive", "--no-hot-reload" })
                start.ArgumentList.Add(argument);
            if (OperatingSystem.IsWindows())
            {
                var checkoutIdentity = Path.TrimEndingDirectorySeparator(source).ToUpperInvariant();
                var checkoutKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkoutIdentity)))[..16];
                // Both executables need to inherit the installed viewer's runtime location. Windows
                // can lower the integrity of executables built inside a shared checkout.
                var runtime = Path.Combine(leaseDirectory, "CheckoutRuntime", checkoutKey, address.Port.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(runtime);
                start.ArgumentList.Add("--artifacts-path");
                start.ArgumentList.Add(Path.Combine(runtime, "Web"));
                start.Environment["ViewerSourceRuntime"] = runtime;
            }
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("--no-launch-profile");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "1";
            start.Environment.Remove("ViewerCollector");
            start.Environment["ViewerSourceCheckout"] = source;
        }
        start.Environment["ViewerPort"] = address.Port.ToString(CultureInfo.InvariantCulture);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        start.Environment["ViewerInstance"] = token;
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint =>
            endpoint.Port == address.Port && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any))))
            throw new InvalidOperationException($"Port {address.Port} already has a server. Close the other viewer before switching modes.");
#if WINDOWS
        ViewerJob.JoinBeforeLaunchingChildren();
#endif
        Directory.CreateDirectory(leaseDirectory);
        FileStream lease;
        try
        {
            lease = File.Open(Path.Combine(leaseDirectory, $"viewer-{address.Port}.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("Another viewer window is already starting. Use that window or close it to switch modes.", error);
        }
        var child = new Process { StartInfo = start };
        var server = new LocalViewerServer(address, child, token, lease, source != null);
        child.OutputDataReceived += (_, e) => server.Remember(e.Data);
        child.ErrorDataReceived += (_, e) => server.Remember(e.Data);
        try
        {
            if (!child.Start()) throw new IOException("Couldn't start the local viewer server.");
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public async Task Watch(Action<Uri?, string> show, CancellationToken stop)
    {
        int? generation = null;
        var started = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            stop.ThrowIfCancellationRequested();
            if (process is { HasExited: true })
            {
                show(null, Volatile.Read(ref lastOutput) ?? "The local viewer server stopped.");
                return;
            }
            if (!generation.HasValue && Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(watchingSource ? 90 : 30))
            {
                show(null, $"The viewer server didn't start on port {Address.Port}. " + Volatile.Read(ref lastOutput));
                return;
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, identity == null ? "_viewer/info" : "_viewer/ready");
                if (identity != null) request.Headers.Add("X-Viewer-Instance", identity);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
                if (identity != null && response.IsSuccessStatusCode &&
                    response.Headers.TryGetValues(ViewerStartupProtocol.Header, out var versions) &&
                    !versions.SequenceEqual(new[] { ViewerStartupProtocol.Version }))
                {
                    show(null, $"This shell supports viewer startup protocol {ViewerStartupProtocol.Version}. Update the shell or switch to a compatible checkout.");
                    return;
                }
                var reply = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(stop) : null;
                var next = identity == null ? reply == "performance-viewer" ? 0 : (int?)null
                    : int.TryParse(reply, out int pid) ? pid : (int?)null;
                if (next.HasValue && next != generation)
                {
                    generation = next;
                    show(watchingSource ? new Uri(Address, $"?source={next.Value}") : Address, "");
                }
                else if (!generation.HasValue && identity != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    show(null, $"The server on port {Address.Port} belongs to a different viewer window.");
                    return;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    private void Remember(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) Volatile.Write(ref lastOutput, line);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "_viewer/exit");
                    request.Headers.Add("X-Viewer-Instance", identity);
                    try { using var response = http.Send(request); }
                    catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
                    if (!process.WaitForExit(2500)) process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        http.Dispose();
        lease?.Dispose();
    }
}
