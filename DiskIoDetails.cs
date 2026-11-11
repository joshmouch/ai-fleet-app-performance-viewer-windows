using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

internal sealed record DiskVolume(int DiskNumber, string[] Paths, string VolumePath, string DevicePath)
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int capacity);

    public string DisplayRoot => Paths.Length == 0 ? VolumePath : Paths[0];

    public static DiskVolume Create(int diskNumber, string[] paths, string[] accessPaths)
    {
        string volume = accessPaths.FirstOrDefault(path =>
            path != null && path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
        if (volume != null)
            volume = volume.TrimEnd('\\') + "\\";
        string name = volume != null ? volume.Substring(4).TrimEnd('\\') :
            paths.FirstOrDefault(path => path.Length == 3)?.TrimEnd('\\');
        var result = new StringBuilder(4096);
        string device = name == null || QueryDosDevice(name, result, result.Capacity) == 0 ? null : result.ToString();
        return new DiskVolume(diskNumber, paths, volume, device);
    }
}

internal sealed class VolumePathIndex
{
    private readonly Dictionary<string, DiskVolume> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DiskVolume> mounts = new(StringComparer.OrdinalIgnoreCase);

    public VolumePathIndex(IEnumerable<DiskVolume> volumes)
    {
        foreach (var volume in volumes)
        {
            if (!string.IsNullOrEmpty(volume.DevicePath))
                devices[volume.DevicePath] = volume;
            if (volume.VolumePath != null)
                mounts[volume.VolumePath[4..].TrimEnd('\\')] = volume;
            foreach (var path in volume.Paths)
                mounts[path.TrimEnd('\\')] = volume;
        }
    }

    public string Resolve(ReadOnlySpan<char> path, int? diskNumber)
    {
        if (path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            int separator = path[8..].IndexOf('\\');
            int end = separator < 0 ? path.Length : separator + 8;
            var deviceLookup = devices.GetAlternateLookup<ReadOnlySpan<char>>();
            return deviceLookup.TryGetValue(path[..end], out var volume) &&
                (!diskNumber.HasValue || volume.DiskNumber == diskNumber) && volume.DisplayRoot != null
                ? volume.DisplayRoot + path[end..].TrimStart('\\').ToString() : null;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            path = path[4..];
        if (!path.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) &&
            (path.Length < 3 || path[1] != ':' || path[2] != '\\'))
            return null;

        // Match the most specific mount before a drive root, including mounts on other disks.
        var mountLookup = mounts.GetAlternateLookup<ReadOnlySpan<char>>();
        for (int end = path.Length; end > 1; end = path[..end].LastIndexOf('\\'))
        {
            if (mountLookup.TryGetValue(path[..end], out var volume))
                return !diskNumber.HasValue || volume.DiskNumber == diskNumber
                    ? volume.DisplayRoot + path[end..].TrimStart('\\').ToString() : null;
        }
        return null;
    }
}

internal readonly record struct FileIoKey(int Pid, string Process, string Path);
internal sealed record VirtualDiskIo(int DiskNumber, string Path, FileIoTotals Totals);

internal struct FileIoTotals
{
    public long ReadBytes, WriteBytes, Reads, Writes;
    public long TotalBytes => ReadBytes + WriteBytes;
    public long Operations => Reads + Writes;

    public void Add(FileIoTotals value)
    {
        ReadBytes += value.ReadBytes;
        WriteBytes += value.WriteBytes;
        Reads += value.Reads;
        Writes += value.Writes;
    }

    public static void AddTo<TKey>(Dictionary<TKey, FileIoTotals> target, TKey key, FileIoTotals value)
    {
        var total = target.GetValueOrDefault(key);
        total.Add(value);
        target[key] = total;
    }
}

internal sealed class VolumeFileIo : IDisposable
{
    private const string SessionPrefix = "CpuTimelines-FileIo-";
    private static readonly Guid FileInfoProvider = new("a319d300-015c-48be-acdb-47746e154751");
    // FileInfo minifilter: file name create (1), rundown (2), delete (3).
    private const ulong FileNameEvents = 0x00010000 | 0x00020000 | 0x00040000;
    private const int BucketSeconds = 5;
    internal const int WindowSeconds = 60;
    private const int MaxPathsPerBucket = 4096;
    private const int MaxOpenFiles = 65536;
    private static readonly System.Threading.SemaphoreSlim RundownGate = new(1);
    private readonly object lifecycle = new();
    private readonly object samples = new();
    private readonly Queue<Bucket> buckets = new();
    private Bucket currentBucket;
    private readonly Dictionary<int, string> processes = new();
    private readonly Dictionary<ulong, OpenFile> openFiles = new();
    private volatile TraceScope scope;
    private long detailStarted;
    private long virtualStarted;
    private volatile bool disposed;
    private TraceEventSession session;
    private volatile string status = "Starting Windows file I/O trace...";
    private volatile string rundownStatus;
    private volatile int eventsLost;
    private long started;

    private sealed record TraceScope(int? DetailDisk, int? SelectedVirtualDisk, int[] VirtualDisks, VolumePathIndex Paths)
    {
        public bool CapturesVirtualDisks => SelectedVirtualDisk.HasValue || VirtualDisks.Length > 0;
    }

    private sealed class OpenFile(string path)
    {
        // The rundown can name an already open file after its first captured I/O.
        public volatile string Path = path;
    }

    private readonly record struct DiskPathKey(FileIoKey Process, OpenFile File);

    private sealed class Bucket(long second)
    {
        public readonly long Second = second;
        public readonly Dictionary<DiskPathKey, FileIoTotals> Paths = new();
        public readonly Dictionary<FileIoKey, FileIoTotals> Processes = new();
        public readonly Dictionary<(int Disk, FileIoKey File), FileIoTotals> VirtualDisks = new();
        public long DiskBytes;
        public int Omitted, VirtualOmitted;
    }

    public sealed record Snapshot(Dictionary<FileIoKey, FileIoTotals> Entries,
        long DiskBytes, long NamedBytes, double Seconds, string Status, string RundownStatus, int EventsLost, int Omitted);
    public sealed record VirtualSnapshot(VirtualDiskIo[] Entries, double Seconds, string Status, int EventsLost);

    public VolumeFileIo(int? detailDisk, int? selectedVirtualDisk, int[] virtualDisks, VolumePathIndex index)
    {
        scope = new TraceScope(detailDisk, selectedVirtualDisk, virtualDisks, index);
        detailStarted = virtualStarted = Stopwatch.GetTimestamp();
        Task.Run(Run);
    }

    public void SetScope(int? detailDisk, int? selectedVirtualDisk, int[] virtualDisks, VolumePathIndex index)
    {
        lock (samples)
        {
            if (scope.DetailDisk == detailDisk && scope.SelectedVirtualDisk == selectedVirtualDisk &&
                scope.VirtualDisks.SequenceEqual(virtualDisks) && scope.Paths == index)
                return;
            if (scope.DetailDisk != detailDisk)
            {
                detailStarted = Stopwatch.GetTimestamp();
                foreach (var bucket in buckets)
                {
                    bucket.Paths.Clear();
                    bucket.Processes.Clear();
                    bucket.DiskBytes = 0;
                    bucket.Omitted = 0;
                }
            }
            var next = new TraceScope(detailDisk, selectedVirtualDisk, virtualDisks, index);
            if (next.CapturesVirtualDisks != scope.CapturesVirtualDisks)
            {
                virtualStarted = Stopwatch.GetTimestamp();
                foreach (var bucket in buckets)
                {
                    bucket.VirtualDisks.Clear();
                    bucket.VirtualOmitted = 0;
                }
            }
            scope = next;
        }
    }

    private void Run()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            string owner = SessionPrefix + self.Id + "-" + self.StartTime.ToUniversalTime().Ticks + "-";
            bool fileInfoAvailable = false;
            lock (lifecycle)
            {
                if (disposed)
                    return;
                ReapAbandonedSessions();
                session = OpenSession(owner);
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO |
                    KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);
                try
                {
                    session.EnableProvider(FileInfoProvider, TraceEventLevel.Verbose, FileNameEvents);
                    fileInfoAvailable = true;
                }
                catch (UnauthorizedAccessException) { }
                catch (Win32Exception) { }
            }

            var source = session.Source;
            foreach (var process in Process.GetProcesses())
                using (process)
                {
                    try { processes[process.Id] = process.ProcessName; }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            source.Kernel.ProcessStart += e => processes[e.ProcessID] = e.ImageFileName;
            source.Kernel.ProcessStop += e => processes.Remove(e.ProcessID);
            if (fileInfoAvailable)
                source.Dynamic.All += RecordFileName;
            source.Kernel.DiskIORead += e => RecordDisk(e, false, source);
            source.Kernel.DiskIOWrite += e => RecordDisk(e, true, source);
            rundownStatus = fileInfoAvailable ? "Resolving names of already open files..." : "Windows filename rundown unavailable";
            System.Threading.Volatile.Write(ref started, Stopwatch.GetTimestamp());
            status = null;
            if (!disposed)
            {
                if (fileInfoAvailable)
                    _ = Task.Run(() => RequestFileNames(owner));
                source.Process();
            }
            if (!disposed)
                status = "Windows ended the file I/O trace. Close and reopen details to restart it.";
        }
        catch (UnauthorizedAccessException)
        {
            status = "Windows denied access to file I/O tracing. Open the viewer as administrator.";
        }
        catch (Exception e)
        {
            if (!disposed)
                status = "Windows file I/O trace stopped. " + e.Message;
        }
        finally
        {
            lock (lifecycle)
            {
                session?.Dispose();
                session = null;
            }
        }
    }

    private static TraceEventSession OpenSession(string owner) =>
        new(owner + Guid.NewGuid().ToString("N"), TraceEventSessionOptions.NoRestartOnCreate)
        { StopOnDispose = true, BufferSizeMB = 32 };

    private async Task RequestFileNames(string owner)
    {
        await RundownGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            // Stopping a kernel disk-file session causes Windows to broadcast open-file
            // names to the live FileInfo subscriber. Keep disk completions flowing there.
            using var rundown = OpenSession(owner);
            rundown.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskFileIO);
            rundown.Stop();
            rundownStatus = null;
        }
        catch (Exception)
        {
            rundownStatus = "Windows filename rundown unavailable";
        }
        finally
        {
            RundownGate.Release();
        }
    }

    private static void ReapAbandonedSessions()
    {
        foreach (string name in TraceEventSession.GetActiveSessionNames())
        {
            if (!name.StartsWith(SessionPrefix, StringComparison.Ordinal))
                continue;
            string[] owner = name.Substring(SessionPrefix.Length).Split('-');
            if (owner.Length != 3 || !int.TryParse(owner[0], out int pid) || !long.TryParse(owner[1], out long started))
                continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.StartTime.ToUniversalTime().Ticks == started)
                    continue;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { continue; }
            using var stale = TraceEventSession.GetActiveSession(name);
            stale?.Stop();
        }
    }

    private unsafe void RecordFileName(TraceEvent e)
    {
        if (e.ProviderGuid != FileInfoProvider)
            return;
        int kind = (int)e.ID;
        int pathOffset = e.PointerSize + sizeof(ushort);
        if (kind is < 1 or > 3 || e.EventDataLength < pathOffset)
            return;

        // The installed provider manifest describes a pointer, a UTF-16 character count,
        // and the path. Filter in place so a system-wide rundown does not allocate per event.
        ulong key = e.PointerSize == 8 ? unchecked((ulong)Marshal.ReadInt64(e.DataStart))
            : unchecked((uint)Marshal.ReadInt32(e.DataStart));
        int length = unchecked((ushort)Marshal.ReadInt16(e.DataStart, e.PointerSize));
        if (key == 0 || length > (e.EventDataLength - pathOffset) / sizeof(char))
            return;
        var selection = scope;
        var rawPath = new ReadOnlySpan<char>((byte*)e.DataStart + pathOffset, length).TrimEnd('\0');
        openFiles.TryGetValue(key, out var file);
        if (kind == 1 && file != null)
        {
            // A newly created file object can reuse a pointer; do not relabel old I/O.
            openFiles.Remove(key);
            file = null;
        }
        if (kind == 3)
        {
            if (file != null)
            {
                if (!rawPath.IsEmpty)
                    file.Path ??= selection.Paths.Resolve(rawPath, null) ?? rawPath.ToString();
                openFiles.Remove(key);
            }
            return;
        }
        if (rawPath.IsEmpty)
            return;
        string path = selection.DetailDisk.HasValue ? selection.Paths.Resolve(rawPath, selection.DetailDisk) : null;
        if (path == null && IsVirtualDisk(rawPath))
            path = selection.Paths.Resolve(rawPath, null) ?? rawPath.ToString();
        if (file != null)
            file.Path ??= path ?? rawPath.ToString();
        else if (path != null && openFiles.Count < MaxOpenFiles)
            openFiles[key] = new OpenFile(path);
    }

    private static bool IsVirtualDisk(ReadOnlySpan<char> path) =>
        path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase);

    private void RecordDisk(DiskIOTraceData e, bool write, ETWTraceEventSource source)
    {
        var selection = scope;
        bool detail = e.DiskNumber == selection.DetailDisk;
        bool virtualDisk = e.DiskNumber == selection.SelectedVirtualDisk ||
            Array.IndexOf(selection.VirtualDisks, e.DiskNumber) >= 0;
        if ((!detail && !virtualDisk) || e.TransferSize <= 0)
            return;

        OpenFile file = null;
        if (e.FileKey != 0)
            openFiles.TryGetValue(e.FileKey, out file);
        if (file?.Path == null)
        {
            string name = e.FileName;
            string path = string.IsNullOrEmpty(name) ? null : selection.Paths.Resolve(name, e.DiskNumber) ??
                (IsVirtualDisk(name) ? selection.Paths.Resolve(name, null) : null) ?? name;
            if (!detail && (path == null || !IsVirtualDisk(path)))
                return;
            if (file != null)
                file.Path = path;
            else if (e.FileKey != 0 && openFiles.Count < MaxOpenFiles)
                openFiles[e.FileKey] = file = new OpenFile(path);
            else if (path != null)
                file = new OpenFile(path);
        }
        string virtualPath = virtualDisk && file?.Path is { } named && IsVirtualDisk(named) ? named : null;
        if (!detail && virtualPath == null)
            return;
        int pid = e.ProcessID;
        if (!processes.TryGetValue(pid, out string process) || string.IsNullOrWhiteSpace(process))
            process = string.IsNullOrWhiteSpace(e.ProcessName) ? (pid == 4 ? "System" : "Unknown") : e.ProcessName;
        var count = write ? new FileIoTotals { WriteBytes = e.TransferSize, Writes = 1 }
            : new FileIoTotals { ReadBytes = e.TransferSize, Reads = 1 };
        long now = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        long second = now / BucketSeconds * BucketSeconds;
        lock (samples)
        {
            if (selection != scope)
                return;
            Trim(now);
            if (currentBucket == null || currentBucket.Second != second)
            {
                eventsLost = source.EventsLost;
                currentBucket = new Bucket(second);
                buckets.Enqueue(currentBucket);
            }
            var bucket = currentBucket;
            if (virtualPath != null)
            {
                var virtualKey = (e.DiskNumber, new FileIoKey(pid, process, virtualPath));
                if (bucket.VirtualDisks.Count < MaxPathsPerBucket || bucket.VirtualDisks.ContainsKey(virtualKey))
                    FileIoTotals.AddTo(bucket.VirtualDisks, virtualKey, count);
                else
                    bucket.VirtualOmitted++;
            }
            if (!detail)
                return;
            var key = new FileIoKey(pid, process, "");
            bucket.DiskBytes += count.TotalBytes;
            FileIoTotals.AddTo(bucket.Processes, key, count);
            var pathKey = new DiskPathKey(key, file);
            if (file == null)
                return;
            if (bucket.Paths.Count < MaxPathsPerBucket || bucket.Paths.ContainsKey(pathKey))
                FileIoTotals.AddTo(bucket.Paths, pathKey, count);
            else
                bucket.Omitted++;
        }
    }

    private void Trim(long now)
    {
        while (buckets.TryPeek(out var oldest) && oldest.Second + BucketSeconds <= now - WindowSeconds)
            buckets.Dequeue();
    }

    public Snapshot Read(bool byPath)
    {
        var entries = new Dictionary<FileIoKey, FileIoTotals>();
        long diskBytes = 0, namedBytes = 0;
        int omitted = 0;
        double seconds;
        lock (samples)
        {
            long timestamp = Stopwatch.GetTimestamp();
            long now = timestamp / Stopwatch.Frequency;
            long traceStart = System.Threading.Volatile.Read(ref started);
            long start = traceStart == 0 ? 0 : Math.Max(traceStart, detailStarted);
            long bucketEdge = (now - WindowSeconds) / BucketSeconds * BucketSeconds;
            long floor = Math.Max(start, bucketEdge * Stopwatch.Frequency);
            seconds = start == 0 ? 0 : Math.Max(0, Stopwatch.GetElapsedTime(floor, timestamp).TotalSeconds);
            Trim(now);
            foreach (var bucket in buckets)
            {
                diskBytes += bucket.DiskBytes;
                if (byPath)
                {
                    foreach (var entry in bucket.Paths)
                    {
                        string path = entry.Key.File.Path;
                        if (string.IsNullOrEmpty(path))
                            continue;
                        namedBytes += entry.Value.TotalBytes;
                        FileIoTotals.AddTo(entries, entry.Key.Process with { Path = path }, entry.Value);
                    }
                }
                else
                {
                    foreach (var entry in bucket.Processes)
                        FileIoTotals.AddTo(entries, entry.Key, entry.Value);
                }
                omitted += bucket.Omitted;
            }
        }
        return new Snapshot(entries, diskBytes, namedBytes, seconds, status, rundownStatus, eventsLost, omitted);
    }

    private string VirtualPath(string path) => VmDiskIo.Normalize(scope.Paths.Resolve(path, null) ?? path);

    private double TrimVirtualWindow()
    {
        long timestamp = Stopwatch.GetTimestamp();
        long now = timestamp / Stopwatch.Frequency;
        Trim(now);
        long traceStart = System.Threading.Volatile.Read(ref started);
        long bucketEdge = (now - WindowSeconds) / BucketSeconds * BucketSeconds;
        long floor = Math.Max(Math.Max(traceStart, virtualStarted), bucketEdge * Stopwatch.Frequency);
        return traceStart == 0 ? 0 : Math.Max(0, Stopwatch.GetElapsedTime(floor, timestamp).TotalSeconds);
    }

    public VirtualSnapshot ReadVirtualDisks()
    {
        var entries = new Dictionary<(int Disk, string Path), FileIoTotals>();
        double seconds;
        lock (samples)
        {
            seconds = TrimVirtualWindow();
            foreach (var bucket in buckets)
                foreach (var entry in bucket.VirtualDisks)
                    if (Array.IndexOf(scope.VirtualDisks, entry.Key.Disk) >= 0)
                    {
                        string path = VirtualPath(entry.Key.File.Path);
                        FileIoTotals.AddTo(entries, (entry.Key.Disk, path), entry.Value);
                    }
        }
        return new VirtualSnapshot(entries.Select(entry => new VirtualDiskIo(entry.Key.Disk, entry.Key.Path, entry.Value)).ToArray(),
            seconds, status ?? rundownStatus, eventsLost);
    }

    public Snapshot ReadVirtualDisk(string path, bool byPath)
    {
        var entries = new Dictionary<FileIoKey, FileIoTotals>();
        long bytes = 0;
        int omitted = 0;
        double seconds;
        lock (samples)
        {
            seconds = TrimVirtualWindow();
            string wanted = VirtualPath(path);
            foreach (var bucket in buckets)
            {
                foreach (var entry in bucket.VirtualDisks)
                {
                    if (!string.Equals(VirtualPath(entry.Key.File.Path), wanted, StringComparison.OrdinalIgnoreCase))
                        continue;
                    bytes += entry.Value.TotalBytes;
                    FileIoTotals.AddTo(entries, entry.Key.File with { Path = byPath ? wanted : "" }, entry.Value);
                }
                omitted += bucket.VirtualOmitted;
            }
        }
        return new Snapshot(entries, bytes, bytes, seconds, status, rundownStatus, eventsLost, omitted);
    }

    public void Dispose()
    {
        lock (lifecycle)
        {
            disposed = true;
            session?.Stop();
        }
    }
}

internal sealed class GuestIoWatch : IDisposable
{
    private readonly System.Threading.CancellationTokenSource stop = new();
    private readonly Queue<(long Completed, GuestIoSample Sample)> frames = new();
    private volatile Snapshot current = new(new(), new(), null, 0, 0, 0, 0, false, 0, null);

    public sealed record Snapshot(Dictionary<FileIoKey, FileIoTotals> Paths, Dictionary<FileIoKey, FileIoTotals> Processes,
        string Device, int Seconds, double DiskSeconds, long DiskReadBytes, long DiskWriteBytes,
        bool Limited, long Latest, string Error);

    public GuestIoWatch(VmDisk disk)
    {
        var cancellation = stop.Token;
        _ = Task.Run(() => Run(disk, cancellation));
    }

    public Snapshot Read()
    {
        var snapshot = current;
        if (snapshot.Latest != 0 && Stopwatch.GetElapsedTime(snapshot.Latest).TotalSeconds >= VolumeFileIo.WindowSeconds)
            return snapshot with { Paths = new(), Processes = new(), Device = null, Seconds = 0, DiskSeconds = 0,
                DiskReadBytes = 0, DiskWriteBytes = 0, Limited = false, Latest = 0 };
        return snapshot;
    }

    private async Task Run(VmDisk disk, System.Threading.CancellationToken cancellation)
    {
        int backoff = 4;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var sample = await VmDiskIo.Sample(disk, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    frames.Enqueue((Stopwatch.GetTimestamp(), sample));
                    Publish(null);
                    backoff = 4;
                }
                catch (Exception e) when (!cancellation.IsCancellationRequested)
                {
                    Publish(e.Message + " Retrying automatically.");
                    await Task.Delay(TimeSpan.FromSeconds(backoff), cancellation).ConfigureAwait(false);
                    backoff = Math.Min(30, backoff * 2);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
    }

    private void Publish(string error)
    {
        long now = Stopwatch.GetTimestamp();
        while (frames.TryPeek(out var oldest) && Stopwatch.GetElapsedTime(oldest.Completed, now).TotalSeconds >= VolumeFileIo.WindowSeconds)
            frames.Dequeue();

        var paths = new Dictionary<FileIoKey, FileIoTotals>();
        var processes = new Dictionary<FileIoKey, FileIoTotals>();
        long latest = 0, diskRead = 0, diskWrite = 0;
        int seconds = 0;
        double diskSeconds = 0;
        string device = null;
        bool limited = false;
        foreach (var frame in frames)
        {
            var sample = frame.Sample;
            latest = frame.Completed;
            device = sample.Device;
            seconds += sample.Seconds;
            diskSeconds += sample.DiskSeconds;
            diskRead += sample.DiskReadBytes;
            diskWrite += sample.DiskWriteBytes;
            limited |= sample.Limited;
            foreach (var row in sample.Rows)
            {
                var key = new FileIoKey(row.Pid, row.Process, row.Path);
                FileIoTotals.AddTo(paths, key, new FileIoTotals { ReadBytes = row.ReadBytes, WriteBytes = row.WriteBytes,
                    Reads = row.Reads, Writes = row.Writes });
            }
        }
        foreach (var entry in paths)
            FileIoTotals.AddTo(processes, entry.Key with { Path = "" }, entry.Value);
        current = new Snapshot(paths, processes, device, seconds, diskSeconds, diskRead, diskWrite, limited, latest, error);
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }
}

internal sealed class DiskIoDetails : Panel
{
    private const double BytesPerMiB = 1024 * 1024;
    private const int TopRows = 50;
    private static readonly Dictionary<FileIoKey, FileIoTotals> EmptyRows = new();
    private readonly Timer refreshTimer = new() { Interval = 2000 };
    private readonly Label title = new() { AutoEllipsis = true };
    private readonly Label status = new() { AutoEllipsis = true };
    private readonly Label totals = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight,
        AccessibleName = "Table I/O totals", Visible = false };
    private readonly ToolTip totalsHelp = new();
    private readonly Button collapse = new() { Text = "Collapse" };
    private readonly Button close = new() { Text = "Close" };
    private readonly TabControl tabs = new();
    private readonly TabPage processTab = new("Processes");
    private readonly TabPage pathTab = new("Paths");
    private readonly TabPage guestProcessTab = new("Guest Processes");
    private readonly TabPage guestPathTab = new("Guest Paths");
    private readonly ListView paths = NewTable(true, true);
    private readonly ListView processes = NewTable(false);
    private readonly ListView guestPaths = NewTable(true);
    private readonly ListView guestProcesses = NewTable(false);
    private readonly Dictionary<ListView, int> sortColumns = new();
    private VolumeFileIo capture;
    private System.Threading.CancellationTokenSource inventoryCancellation;
    private Task<Dictionary<string, VmDiskOwners>> inventory;
    private GuestIoWatch guestWatch;
    private VmDisk guestDisk;
    private string diskName;
    private string totalsFull, totalsCompact;
    private int? selectedDisk;
    private string selectedPath;
    private bool openVirtualGuest;
    private int[] catalogDisks = Array.Empty<int>();
    private VolumePathIndex captureIndex;
    private bool expanded = true;

    private sealed record RowSelection(FileIoKey Key, VmDisk Attachment);
    private sealed record TableRender(Dictionary<FileIoKey, FileIoTotals> Entries, int Sort,
        Dictionary<string, VmDiskOwners> Owners, FileIoTotals Totals);

    public bool HasSelection => selectedDisk.HasValue;
    public Dictionary<string, VmDiskOwners> VirtualDiskOwners =>
        inventory is { IsCompletedSuccessfully: true } ? inventory.Result : null;
    public event EventHandler LayoutChanged;

    public DiskIoDetails()
    {
        Size = new Size(600, 300);
        title.SetBounds(8, 6, Width - 166, 23);
        title.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        status.SetBounds(8, 32, Width - 16, 21);
        status.Anchor = title.Anchor;
        collapse.SetBounds(Width - 154, 2, 76, 26);
        close.SetBounds(Width - 72, 2, 64, 26);
        collapse.Anchor = close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        collapse.Click += (_, _) => SetExpanded(!expanded);
        close.Click += (_, _) => CloseDetails();
        tabs.SetBounds(8, 55, Width - 16, Height - 55);
        tabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        processTab.Controls.Add(processes);
        pathTab.Controls.Add(paths);
        guestProcessTab.Controls.Add(guestProcesses);
        guestPathTab.Controls.Add(guestPaths);
        tabs.TabPages.AddRange(new[] { processTab, pathTab });
        tabs.SelectedIndexChanged += (_, _) => RefreshRows();
        tabs.SizeChanged += (_, _) => LayoutTotals();
        refreshTimer.Tick += (_, _) => RefreshRows();
        foreach (var table in new[] { paths, processes, guestPaths, guestProcesses })
            table.ColumnClick += (_, e) => { sortColumns[table] = e.Column; RefreshRows(); };
        paths.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenGuest(paths.GetItemAt(e.X, e.Y));
        };
        paths.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && paths.SelectedItems.Count > 0)
            {
                e.Handled = true;
                OpenGuest(paths.SelectedItems[0]);
            }
        };
        Controls.AddRange(new Control[] { title, status, collapse, close, tabs, totals });
        totals.BringToFront();
        Visible = false;
    }

    private static ListView NewTable(bool includePath, bool includeVm = false)
    {
        var table = new ListView
        {
            View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true, ShowItemToolTips = true,
            HeaderStyle = ColumnHeaderStyle.Clickable
        };
        table.Columns.Add("Process", 135);
        table.Columns.Add("PID", 62, HorizontalAlignment.Right);
        table.Columns.Add("Read MiB", 84, HorizontalAlignment.Right);
        table.Columns.Add("Write MiB", 84, HorizontalAlignment.Right);
        table.Columns.Add("Total MiB", 84, HorizontalAlignment.Right);
        table.Columns.Add("Reads", 66, HorizontalAlignment.Right);
        table.Columns.Add("Writes", 66, HorizontalAlignment.Right);
        table.Columns.Add("Total ops", 76, HorizontalAlignment.Right);
        if (includeVm)
            table.Columns.Add("VM", 118);
        if (includePath)
        {
            var path = table.Columns.Add("Path", 240);
            table.SizeChanged += (_, _) =>
                path.Width = Math.Max(170, table.ClientSize.Width - 20 -
                    table.Columns.Cast<ColumnHeader>().Where(column => column != path).Sum(column => column.Width));
        }
        return table;
    }

    public int HeightFor(int available) => expanded ? Math.Min(345, Math.Max(180, available * 3 / 5)) : 34;

    public void ApplyColors(Color background, Color foreground, Color card)
    {
        BackColor = card;
        ForeColor = foreground;
        foreach (var table in new[] { paths, processes, guestPaths, guestProcesses })
        {
            table.BackColor = background;
            table.ForeColor = foreground;
        }
        collapse.BackColor = close.BackColor = totals.BackColor = card;
    }

    public void SelectDisk(int diskNumber, string name, VolumePathIndex index) =>
        Select(diskNumber, name, null, index);

    public void SelectVirtualDisk(int diskNumber, string path, VolumePathIndex index) =>
        Select(diskNumber, System.IO.Path.GetFileName(path), path, index);

    private void Select(int diskNumber, string name, string path, VolumePathIndex index)
    {
        bool newSelection = selectedDisk != diskNumber || !string.Equals(selectedPath, path, StringComparison.OrdinalIgnoreCase);
        if (newSelection)
        {
            openVirtualGuest = false;
            ClearGuest();
        }
        selectedDisk = diskNumber;
        selectedPath = path;
        diskName = name;
        captureIndex = index;
        UpdateCapture();
        if (newSelection)
        {
            openVirtualGuest = path != null;
            tabs.SelectedTab = path == null ? processTab : pathTab;
        }
        SetExpanded(true);
    }

    public void TrackVirtualDisks(int[] disks, VolumePathIndex index)
    {
        catalogDisks = disks;
        captureIndex = index;
        UpdateCapture();
    }

    public VolumeFileIo.VirtualSnapshot ReadVirtualDisks() => capture?.ReadVirtualDisks();

    private void UpdateCapture()
    {
        if (!HasSelection && catalogDisks.Length == 0)
        {
            capture?.Dispose();
            capture = null;
            inventoryCancellation?.Cancel();
            inventoryCancellation?.Dispose();
            inventoryCancellation = null;
            inventory = null;
            return;
        }
        int? detailDisk = selectedPath == null ? selectedDisk : null;
        int? virtualDisk = selectedPath == null ? null : selectedDisk;
        if (capture == null)
            capture = new VolumeFileIo(detailDisk, virtualDisk, catalogDisks, captureIndex);
        else
            capture.SetScope(detailDisk, virtualDisk, catalogDisks, captureIndex);
        if (inventory == null)
        {
            inventoryCancellation = new();
            inventory = VmDiskIo.ReadInventory(inventoryCancellation.Token);
        }
    }

    private void SetExpanded(bool value)
    {
        expanded = value;
        collapse.Text = expanded ? "Collapse" : "Expand";
        tabs.Visible = status.Visible = expanded;
        if (!expanded)
            totals.Visible = false;
        refreshTimer.Enabled = expanded;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        RefreshRows();
    }

    public void CloseDetails()
    {
        if (!HasSelection)
            return;
        refreshTimer.Stop();
        selectedDisk = null;
        selectedPath = null;
        openVirtualGuest = false;
        ClearGuest();
        UpdateCapture();
        paths.Items.Clear();
        processes.Items.Clear();
        paths.Tag = processes.Tag = null;
        totals.Visible = false;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            guestWatch?.Dispose();
            inventoryCancellation?.Cancel();
            inventoryCancellation?.Dispose();
            refreshTimer.Dispose();
            totalsHelp.Dispose();
            capture?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void RefreshRows()
    {
        if (!HasSelection || capture == null || !expanded)
            return;
        if (openVirtualGuest && inventory is { IsCompleted: true })
        {
            openVirtualGuest = false;
            var owner = VirtualDiskOwners?.GetValueOrDefault(VmDiskIo.Normalize(selectedPath));
            if (owner?.Attachment != null)
            {
                OpenGuest(owner.Attachment);
                return;
            }
        }
        bool guestByPath = tabs.SelectedTab == guestPathTab;
        bool inGuest = guestDisk != null && (guestByPath || tabs.SelectedTab == guestProcessTab);
        title.Text = inGuest ? guestDisk.VMName + "  |  " + System.IO.Path.GetFileName(guestDisk.Path) : "File I/O  |  " + diskName;
        if (inGuest)
        {
            RefreshGuest(guestByPath);
            return;
        }

        bool byPath = tabs.SelectedTab == pathTab;
        var snapshot = selectedPath == null ? capture.Read(byPath) : capture.ReadVirtualDisk(selectedPath, byPath);
        var entries = snapshot.Entries;
        string window = $"Completed disk I/O  |  {snapshot.Seconds:0} s window";
        if (snapshot.DiskBytes == 0)
            window = "No completed disk I/O in the current window.";
        else if (byPath && selectedPath == null)
            window += $"  |  {(double)snapshot.NamedBytes / snapshot.DiskBytes:P0} of bytes matched to paths";
        status.Text = snapshot.Status ?? window;
        if (byPath && snapshot.Status == null && snapshot.RundownStatus != null)
            status.Text += "  |  " + snapshot.RundownStatus;
        if (snapshot.EventsLost > 0)
            status.Text += "  |  Windows dropped " + snapshot.EventsLost + " trace events";
        if (byPath && snapshot.Omitted > 0)
            status.Text += "  |  Path limit reached";

        var owners = byPath && inventory is { IsCompletedSuccessfully: true } ? inventory.Result : null;
        if (byPath && inventory is { IsFaulted: true })
            status.Text += "  |  " + inventory.Exception.GetBaseException().Message;
        if (byPath && owners != null && entries.Keys.Any(key => owners.TryGetValue(VmDiskIo.Normalize(key.Path), out var match) && match.Attachment != null))
            status.Text += "  |  Click a VM disk for live guest I/O";
        var totals = FillTable(byPath ? paths : processes, entries, byPath, byPath, owners);
        ShowTotals(totals, snapshot.Seconds, selectedPath != null ? "Completed Windows I/O for this virtual disk file." :
            byPath ? "Completed Windows disk I/O matched to paths." : "All completed Windows disk I/O for this disk.");
    }

    private void OpenGuest(ListViewItem item)
    {
        if (item?.Tag is not RowSelection { Attachment: { } attachment })
        {
            if (item != null && guestDisk != null)
                ClearGuest();
            return;
        }
        OpenGuest(attachment);
    }

    private void OpenGuest(VmDisk attachment)
    {
        if (guestDisk != attachment)
        {
            guestWatch?.Dispose();
            guestDisk = attachment;
            guestWatch = new GuestIoWatch(attachment);
        }
        if (!tabs.TabPages.Contains(guestPathTab))
            tabs.TabPages.AddRange(new[] { guestProcessTab, guestPathTab });
        tabs.SelectedTab = guestPathTab;
        RefreshRows();
    }

    private void ClearGuest()
    {
        guestWatch?.Dispose();
        guestWatch = null;
        tabs.TabPages.Remove(guestPathTab);
        tabs.TabPages.Remove(guestProcessTab);
        guestDisk = null;
        guestPaths.Items.Clear();
        guestProcesses.Items.Clear();
        guestPaths.Tag = guestProcesses.Tag = null;
    }

    private void RefreshGuest(bool byPath)
    {
        var table = byPath ? guestPaths : guestProcesses;
        var sample = guestWatch?.Read();
        var entries = (byPath ? sample?.Paths : sample?.Processes) ?? EmptyRows;
        string diskRate = sample is { DiskSeconds: > 0 }
            ? $"Disk {(sample.DiskReadBytes + sample.DiskWriteBytes) / BytesPerMiB / sample.DiskSeconds:0.##} MiB/s"
            : "Disk rate unavailable";
        status.Text = sample?.Error ?? (sample is { Seconds: > 0 }
            ? $"Live guest ext4 I/O on {sample.Device}  |  {sample.Seconds} s sampled in ~{VolumeFileIo.WindowSeconds} s  |  {diskRate}  |  File totals include cache"
            : "Collecting the first guest I/O sample inside " + guestDisk.VMName + "...");
        if (sample is { Error: not null, Seconds: > 0 })
            status.Text += " Showing recent samples.";
        if (sample?.Limited == true)
            status.Text += "  |  Some file paths unresolved";
        var totals = FillTable(table, entries, byPath, false, null);
        string comparison = sample is { DiskSeconds: > 0 }
            ? $"Guest disk counters during the sample runs: read {sample.DiskReadBytes / BytesPerMiB / sample.DiskSeconds:0.##} MiB/s, write {sample.DiskWriteBytes / BytesPerMiB / sample.DiskSeconds:0.##} MiB/s."
            : "Guest disk counters are unavailable.";
        ShowTotals(totals, sample?.Seconds ?? 0, "Observed guest ext4 file I/O, including cached I/O. File rate uses sampled time. " + comparison);
    }

    private void ShowTotals(FileIoTotals values, double seconds, string description)
    {
        double read = values.ReadBytes / BytesPerMiB, write = values.WriteBytes / BytesPerMiB;
        double total = values.TotalBytes / BytesPerMiB;
        totalsFull = seconds <= 0 ? "Waiting for I/O totals..." :
            $"Read {read:N2} MiB / {values.Reads:N0} ops   |   Write {write:N2} MiB / {values.Writes:N0} ops   |   Total {total:N2} MiB / {values.Operations:N0} ops ({total / seconds:N2} MiB/s)";
        totalsCompact = seconds <= 0 ? totalsFull :
            $"R {read:N2}  |  W {write:N2}  |  Total {total:N2} MiB ({total / seconds:N2} MiB/s)";
        string help = $"{totalsFull}\n{description}\nIncludes every captured row in this tab, even when only the top {TopRows} are listed.";
        if (totalsHelp.GetToolTip(totals) != help)
            totalsHelp.SetToolTip(totals, help);
        LayoutTotals();
    }

    private void LayoutTotals()
    {
        if (!expanded || capture == null || !tabs.IsHandleCreated || tabs.TabCount == 0 || totalsFull == null)
            return;
        var first = tabs.GetTabRect(0);
        int left = tabs.Left + tabs.GetTabRect(tabs.TabCount - 1).Right + 12;
        int width = Math.Max(0, tabs.Right - left - 8);
        totals.SetBounds(left, tabs.Top + first.Top, width, first.Height);
        totals.Text = TextRenderer.MeasureText(totalsFull, totals.Font).Width <= width ? totalsFull : totalsCompact;
        totals.AccessibleDescription = totalsFull;
        totals.Visible = width > 0;
    }

    private FileIoTotals FillTable(ListView table, Dictionary<FileIoKey, FileIoTotals> entries, bool byPath, bool includeVm,
        Dictionary<string, VmDiskOwners> owners)
    {
        int sort = sortColumns.GetValueOrDefault(table);
        if (table.Tag is TableRender rendered && rendered.Entries == entries && rendered.Sort == sort && rendered.Owners == owners)
            return rendered.Totals;
        FileIoTotals totals = default;
        foreach (var value in entries.Values)
            totals.Add(value);
        var previous = table.SelectedItems.Count > 0 ? table.SelectedItems[0].Tag as RowSelection : null;
        var items = entries.OrderByDescending(entry => SortValue(entry.Value, sort))
            .ThenByDescending(entry => entry.Value.TotalBytes).Take(TopRows).Select(entry =>
            {
                var item = new ListViewItem(new[] { entry.Key.Process, entry.Key.Pid > 0 ? entry.Key.Pid.ToString() : "-",
                    (entry.Value.ReadBytes / BytesPerMiB).ToString("0.###"), (entry.Value.WriteBytes / BytesPerMiB).ToString("0.###"),
                    (entry.Value.TotalBytes / BytesPerMiB).ToString("0.###"),
                    entry.Value.Reads.ToString("N0"), entry.Value.Writes.ToString("N0"), entry.Value.Operations.ToString("N0") });
                VmDiskOwners vm = null;
                if (includeVm)
                {
                    vm = owners?.GetValueOrDefault(VmDiskIo.Normalize(entry.Key.Path));
                    item.SubItems.Add(vm?.Label ?? "");
                }
                item.Tag = new RowSelection(entry.Key, vm?.Attachment);
                if (byPath)
                {
                    item.SubItems.Add(entry.Key.Path);
                    item.ToolTipText = entry.Key.Path + (vm?.Attachment != null ? "\nClick for live guest I/O in " + vm.Label : "");
                }
                return item;
            }).ToArray();
        int top = table.TopItem?.Index ?? 0;
        table.BeginUpdate();
        try
        {
            table.Items.Clear();
            table.Items.AddRange(items);
            if (previous != null)
            {
                var item = items.FirstOrDefault(item => ((RowSelection)item.Tag).Key == previous.Key);
                if (item != null)
                    item.Selected = true;
            }
            if (items.Length > 0 && top > 0)
                table.TopItem = items[Math.Min(top, items.Length - 1)];
        }
        finally { table.EndUpdate(); }
        table.Tag = new TableRender(entries, sort, owners, totals);
        return totals;
    }

    private static long SortValue(FileIoTotals value, int column)
    {
        return column switch
        {
            2 => value.ReadBytes,
            3 => value.WriteBytes,
            5 => value.Reads,
            6 => value.Writes,
            7 => value.Operations,
            _ => value.TotalBytes
        };
    }
}
