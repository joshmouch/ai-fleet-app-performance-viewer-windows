using System.Diagnostics;
using System.Runtime.InteropServices;

if (args is ["--check"]) { CollectorChecks.Run(); return 0; }
if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("macOS required"); return 2; }
int? pid = null;
double interval = 2;
int duration = 0;
bool singleWindow = args.Length == 2 && int.TryParse(args[0], out _) && int.TryParse(args[1], out _);
if (singleWindow)
{
    if (!int.TryParse(args[1], out var seconds) || seconds is < 1 or > 30) return 2;
    args = ["--pid", args[0], "--duration", args[1]];
}
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--host") continue;
    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var value) || value < 1)
    { Console.Error.WriteLine("MacCollector --host [--interval 1..30] [--duration SECONDS] | --pid PID [--duration SECONDS]"); return 2; }
    switch (args[i++])
    {
        case "--pid": pid = value; break;
        case "--interval" when value <= 30: interval = value; break;
        case "--duration": duration = value; break;
        default: return 2;
    }
}
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
return await Capture.Run(pid, interval, duration, singleWindow, stop.Token);

static class Capture
{
    internal static async Task<int> Run(int? pid, double interval, int duration, bool singleWindow, CancellationToken stop)
    {
        var ownership = new ThreadOwners(pid);
        ownership.Refresh();
        var scope = pid is int p ? "process:" + p : "host";
        var gate = new object();
        var window = new PhysicalWindow(scope);
        var capture = Guid.NewGuid();
        long sequence = 0;
        var elapsed = Stopwatch.StartNew();
        double previous = 0;
        using var trace = new Process { StartInfo = new("/usr/bin/sudo") {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in new[] { "-n", "/usr/bin/fs_usage", "-w", "-f", "diskio" }) trace.StartInfo.ArgumentList.Add(arg);
        if (duration > 0) { trace.StartInfo.ArgumentList.Add("-t"); trace.StartInfo.ArgumentList.Add(duration.ToString()); }
        if (pid is int selected) trace.StartInfo.ArgumentList.Add(selected.ToString());
        // Exactly one native capture for the entire stream, never one per process or window.
        trace.Start();
        var stderr = trace.StandardError.ReadToEndAsync();
        using var finished = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var census = Task.Run(async () => {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            try { while (await timer.WaitForNextTickAsync(finished.Token)) ownership.Refresh(); }
            catch (OperationCanceledException) when (finished.IsCancellationRequested) { }
        });
        void Emit(bool final = false)
        {
            lock (gate)
            {
                var now = elapsed.Elapsed.TotalSeconds;
                // Round only a sub-millisecond tail up to storage precision; never discard its completions.
                var frame = window.Frame(Math.Max(.001, now - previous), capture, ++sequence, ownership.Failures, final);
                window = new PhysicalWindow(scope);
                previous = now;
                Console.WriteLine(frame.Encode());
            }
        }
        var emit = Task.Run(async () => {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
            try { while (await timer.WaitForNextTickAsync(finished.Token)) { if (!singleWindow) Emit(); } }
            catch (OperationCanceledException) when (finished.IsCancellationRequested) { }
        });
        var failed = false;
        var read = Task.Run(async () => {
            while (await trace.StandardOutput.ReadLineAsync() is { } line)
                lock (gate) window.Accept(line, ownership.Find);
            await trace.WaitForExitAsync();
        });
        try
        {
            var terminal = await Task.WhenAny(read, census, emit);
            if (terminal.IsFaulted) failed = true;
            if (terminal == read) await read;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { failed = true; }
        finally
        {
            if (!trace.HasExited)
            {
                try { CollectorProcess.RequestStop(trace); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { failed = true; }
            }
            // The single reader drains EOF after stop; no cancellation can lose a partial line.
            try { await read; }
            catch (Exception) { failed = true; }
            finished.Cancel();
            try { await Task.WhenAll(census, emit); }
            catch (Exception) { failed = true; }
            var error = await stderr;
            failed |= (!stop.IsCancellationRequested && trace.ExitCode != 0) || !string.IsNullOrWhiteSpace(error);
            lock (gate) window.Error |= failed;
            Emit(final: true);
        }
        return failed ? 1 : 0;
    }
}
