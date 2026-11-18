using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

if (args is ["--check"]) { CollectorChecks.Run(); return 0; }
if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("macOS required"); return 2; }
if (args.Length != 2 || !int.TryParse(args[0], out var pid) || pid <= 0 ||
    !int.TryParse(args[1], out var seconds) || seconds is < 1 or > 30)
{ Console.Error.WriteLine("MacCollector PID SECONDS (1..30); JSON performance window on stdout"); return 2; }
try
{
    using var target = Process.GetProcessById(pid);
    var started = target.StartTime.ToUniversalTime().Ticks;
    var name = target.ProcessName;
    var threads = Native.Threads(pid);
    var window = new PhysicalWindow(pid, name);
    var begin = Stopwatch.StartNew();
    using var trace = new Process { StartInfo = new("/usr/bin/sudo") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
    foreach (var arg in new[] { "-n", "/usr/bin/fs_usage", "-w", "-f", "diskio", "-t", seconds.ToString(), pid.ToString() })
        trace.StartInfo.ArgumentList.Add(arg);
    trace.Start();
    var errors = trace.StandardError.ReadToEndAsync();
    try
    {
    while (await trace.StandardOutput.ReadLineAsync() is { } line)
    {
        // The suffix is a kernel thread id, never a pid. Keep observed threads for short-lived workers.
        target.Refresh();
        if (target.HasExited || target.StartTime.ToUniversalTime().Ticks != started)
        { window.IdentityLost = true; continue; }
        var suffix = Regex.Match(line, @"\.(?<tid>[0-9]+)\s*$");
        if (ulong.TryParse(suffix.Groups["tid"].Value, out var currentThread) && !threads.Contains(currentThread))
            threads.UnionWith(Native.Threads(pid));
        window.Accept(line, threads);
    }
    await trace.WaitForExitAsync();
    }
    finally
    {
        if (!trace.HasExited)
        {
            trace.Kill(entireProcessTree: true);
            await trace.WaitForExitAsync();
        }
    }
    var error = await errors;
    if (trace.ExitCode != 0 || !string.IsNullOrWhiteSpace(error)) window.Error = true;
    target.Refresh();
    if (target.HasExited || target.StartTime.ToUniversalTime().Ticks != started) window.IdentityLost = true;
    var frame = window.Frame(begin.Elapsed.TotalSeconds, started);
    Console.WriteLine(frame.Encode());
    return window.Error || window.IdentityLost ? 1 : 0;
}
catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
{ Console.Error.WriteLine(e.GetType().Name + ": collector unavailable"); return 1; }

static class Native
{
    [DllImport("/usr/lib/libproc.dylib")] static extern int proc_pidinfo(int pid, int flavor, ulong arg, [Out] ulong[] buffer, int size);
    internal static HashSet<ulong> Threads(int pid)
    {
        var buffer = new ulong[16384];
        // XNU proc_info_private.h: PROC_PIDLISTTHREADIDS=28 returns unique kernel ids.
        // PROC_PIDLISTTHREADS=6 returns user thread handles, not fs_usage suffixes.
        var bytes = proc_pidinfo(pid, 28, 0, buffer, buffer.Length * sizeof(ulong));
        if (bytes <= 0 || bytes >= buffer.Length * sizeof(ulong)) throw new InvalidOperationException("Thread census unavailable");
        return buffer.Take(bytes / sizeof(ulong)).ToHashSet();
    }
}

sealed class PhysicalWindow(int pid, string process)
{
    // Fixed columns through device, then the optional path, duration, optional wait marker and command.thread.
    static readonly Regex Row = new(@"^\s*\S+\s+(?<op>RdData|WrData|RdMeta|WrMeta)\[[^\]]+\]\s+D=0x[0-9a-fA-F]+\s+B=0x(?<bytes>[0-9a-fA-F]+)\s+(?<device>/dev/\S+)\s+(?:(?<path>.*?)\s+)?[0-9]+\.[0-9]+\s+(?:W\s+)?(?<command>.+)\.(?<tid>[0-9]+)\s*$", RegexOptions.CultureInvariant);
    readonly Dictionary<FileIoKey, FileIoTotals> files = new();
    long captured, named, keyless, unresolved, unnamed;
    FileIoTotals keylessTotals;
    int lost;
    public bool Error, IdentityLost;
    public void Accept(string line, HashSet<ulong> threads)
    {
        var m = Row.Match(line);
        if (!m.Success)
        {
            if (Regex.IsMatch(line, @"\b(?:lost|dropped|overflow)\b", RegexOptions.IgnoreCase))
            { lost++; return; } // Count notices, never claim a numeric lost-event count.
            if (line.Contains("RdData") || line.Contains("WrData") || line.Contains("RdMeta") || line.Contains("WrMeta")) Error = true;
            return;
        }
        if (!long.TryParse(m.Groups["bytes"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bytes) || bytes < 0)
        { Error = true; return; }
        captured += bytes;
        var read = m.Groups["op"].Value.StartsWith("Rd");
        var delta = new FileIoTotals { ReadBytes = read ? bytes : 0, WriteBytes = read ? 0 : bytes,
            Reads = read ? 1 : 0, Writes = read ? 0 : 1 };
        if (!ulong.TryParse(m.Groups["tid"].Value, out var tid) || !threads.Contains(tid))
        { keyless += bytes; unresolved += bytes; keylessTotals.Add(delta); return; }
        var path = m.Groups["path"].Value.Trim();
        // Device-only/metadata records are not file attribution. Do not fabricate a path.
        if (!m.Groups["op"].Value.EndsWith("Data") || path.Length == 0 || path.StartsWith("/dev/"))
        { keyless += bytes; unnamed += bytes; keylessTotals.Add(delta); return; }
        named += bytes;
        FileIoTotals.AddTo(files, new FileIoKey(pid, process, path), delta);
    }
    public PerformanceWindow Frame(double seconds, long started)
    {
        var rows = IdentityLost ? new List<MeasuredFile>() : files.Select(x => new MeasuredFile(x.Key, x.Value)).ToList();
        var unknown = keylessTotals;
        if (IdentityLost) unknown.Add(DiskIoMerge.Sum(files.Values));
        if (unknown.Operations > 0) rows.Add(new(new FileIoKey(0, "unattributed (includes metadata)", ""), unknown));
        return new(PerformanceWindow.CurrentSchema,
        Environment.MachineName, null, Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
        new CpuProcessSample(Environment.ProcessorCount, seconds, [new(pid, process, 0, Started: started)]),
        new DiskPathWindow(DateTimeOffset.UtcNow, seconds, null,
            $"mac fs_usage diskio; CPU unmeasured; physical data only; cache hits excluded; unresolvedBytes={unresolved}; unnamedBytes={unnamed}; device/metadata/unresolved bytes keyless; loss notices={lost}, lost event count unknown; error={Error}; identityLost={IdentityLost}; coverage partial",
            lost, captured, IdentityLost ? 0 : named, IdentityLost ? captured : keyless, rows.ToArray()));
    }
}

static class CollectorChecks
{
    public static void Run()
    {
        var w = new PhysicalWindow(123, "worker");
        HashSet<ulong> threads = [900001];
        w.Accept("12:00:00.000001 RdData[S] D=0x123 B=0x1000 /dev/disk3 /tmp/public fixture 0.000010 W worker.900001", threads);
        w.Accept("12:00:00.000002 WrData[S] D=0x124 B=0x2000 /dev/disk3 /tmp/public fixture 0.000010 worker.900001", threads);
        w.Accept("12:00:00.000003 RdMeta[S] D=0x125 B=0x1000 /dev/disk3 0.000010 worker.900001", threads);
        w.Accept("12:00:00.000004 WrData[S] D=0x126 B=0x1000 /dev/disk3 /tmp/public fixture 0.000010 worker.123", threads);
        var f = PerformanceWindow.Decode(w.Frame(1, 42).Encode());
        var t = DiskIoMerge.Sum(f.DiskPaths.Files.Where(x => x.File.Path != "").Select(x => x.Totals));
        _ = FileWindowExport.Encode(f);
        if (t.ReadBytes != 4096 || t.WriteBytes != 8192 || t.Reads != 1 || t.Writes != 1 ||
            f.DiskPaths.CapturedBytes != 20480 || f.DiskPaths.KeylessBytes != 8192 || f.Cpu.Rows[0].Started != 42)
            throw new Exception("counters/identity/coverage failed");
        w.Accept("lost events", threads);
        w.Accept("12:00:00 WrData malformed", threads);
        if (w.Frame(1, 42).DiskPaths.EventsLost != 1 || !w.Error) throw new Exception("loss/error failed");
        _ = FileWindowExport.Encode(w.Frame(1, 42));
        w.IdentityLost = true;
        if (w.Frame(1, 42).DiskPaths.Files.Any(x => x.File.Pid != 0)) throw new Exception("identity fence failed");
        _ = FileWindowExport.Encode(w.Frame(1, 42));
        if (new PhysicalWindow(123, "worker").Frame(1, 42).DiskPaths.Totals().Operations != 0) throw new Exception("empty cache window failed");
        var names = new PhysicalWindow(123, "worker");
        names.Accept("12:00:00.000001 RdData[S] D=0x123 B=0x1000 /dev/disk3 lost-dropped.bin 0.000010 W worker.900001", threads);
        if (names.Frame(1, 42).DiskPaths.Totals().Reads != 1 || names.Frame(1, 42).DiskPaths.EventsLost != 0)
            throw new Exception("diagnostic words in filename failed");
        Console.WriteLine("PASS: four counters, thread != pid, identity fence, spaces, metadata, keyless, loss, malformed input, empty window, contract roundtrip");
    }
}
