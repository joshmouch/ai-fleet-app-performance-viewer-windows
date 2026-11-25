using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
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

internal sealed record VirtualDiskIo(int DiskNumber, string Path, FileIoTotals Totals);

internal sealed class VolumeFileIo : IDisposable
{
    private const string SessionPrefix = "CpuTimelines-FileIo-";
    private static readonly Guid FileInfoProvider = new("a319d300-015c-48be-acdb-47746e154751");
    private static readonly Guid DiskProvider = new("c7bde69a-e1e0-4177-b6ef-283ad1525271");
    private static readonly Guid FileProvider = new("edd08927-9cc4-4e65-b970-c2560fb5c289");
    private static readonly Guid ProcessProvider = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    // FileInfo minifilter: file name create (1), rundown (2), delete (3).
    private const ulong FileNameEvents = 0x00010000 | 0x00020000 | 0x00040000;
    private const int BucketSeconds = 5;
    internal const int WindowSeconds = 60;
    private const int MaxOpenFiles = 65536;
    private const int MaxVirtualFiles = 1024;
    private const int MaxRequests = 65536;
    private static readonly System.Threading.SemaphoreSlim RundownGate = new(1);
    private readonly object lifecycle = new();
    private readonly object samples = new();
    private readonly Queue<Bucket> buckets = new();
    private readonly bool frameIntervals;
    private Bucket currentBucket;
    private long previousFrame;
    private readonly Dictionary<int, string> processes = new();
    // Kernel-File creates name an individual open. A later filesystem event gives that
    // open's stream key, which Kernel-Disk reports in its FileObject field.
    private readonly WindowsDiskFileNames fileNameQueries = new();
    private volatile bool queryOpenFiles;
    private readonly FileNames manifestOpens = new(MaxOpenFiles);
    private readonly FileNames observedFiles = new(MaxOpenFiles);
    private readonly FileNames recentNames = new(MaxOpenFiles);
    private readonly FileNames virtualFiles = new(MaxVirtualFiles);
    private readonly Dictionary<ulong, DiskRequest> requests = new();
    private double nextRequestTrim;
    private double nextNameRundown = BucketSeconds * 1000;
    private bool firstNameRundown = true;
    private bool nameRundownNeeded = true;
    private int nameRundownRunning;
    private volatile TraceScope scope;
    private long detailStarted;
    private long virtualStarted;
    private volatile bool disposed;
    private TraceEventSession session;
    private volatile string status = "Starting Windows file I/O trace...";
    private volatile int eventsLost;
    private long started;

    private sealed record TraceScope(bool Details, int[] VirtualDisks, VolumePathIndex Paths)
    {
        public bool CapturesVirtualDisks => VirtualDisks.Length > 0;
    }

    private sealed class OpenFile(string path)
    {
        // The rundown can name an already open file after its first captured I/O.
        public volatile string Path = path;
        public bool RequestedName;
    }

    private sealed class FileNames(int capacity)
    {
        private readonly Dictionary<ulong, OpenFile> files = new();
        private readonly Queue<(ulong Key, OpenFile File)> order = new();

        public OpenFile Find(ulong key) => files.GetValueOrDefault(key);
        public void Remove(ulong key) => files.Remove(key);

        public void Remember(ulong key, OpenFile file)
        {
            if (order.Count == capacity)
            {
                var oldest = order.Dequeue();
                if (ReferenceEquals(Find(oldest.Key), oldest.File))
                    files.Remove(oldest.Key);
            }
            files[key] = file;
            order.Enqueue((key, file));
        }

        public bool MarkUnnamedForRundown()
        {
            bool found = false;
            foreach (var file in files.Values)
            {
                if (file.Path != null || file.RequestedName)
                    continue;
                file.RequestedName = true;
                found = true;
            }
            return found;
        }
    }

    private readonly record struct DiskRequest(int Pid, int Thread, double Time, string Process);
    private readonly record struct DiskPathKey(int Disk, FileIoKey File, OpenFile PendingName);

    private sealed class Bucket(long second)
    {
        public readonly long Second = second;
        public Dictionary<DiskPathKey, FileIoTotals> Paths = new();
        public readonly Dictionary<int, long> KeylessBytes = new();
        public readonly Dictionary<(int Disk, FileIoKey Process), FileIoTotals> Processes = new();
        public readonly Dictionary<(int Disk, string Path), FileIoTotals> VirtualDisks = new();
    }

    public sealed record Snapshot(Dictionary<FileIoKey, FileIoTotals> Entries,
        long DiskBytes, long NamedBytes, double Seconds, string Status, int EventsLost,
        long KeylessBytes, long OutsideScopeBytes,
        DateTimeOffset End, Dictionary<FileIoKey, FileIoTotals> VirtualPaths = null, DiskFileMeasurement[] DiskFiles = null);
    public sealed record VirtualSnapshot(VirtualDiskIo[] Entries, double Seconds, string Status, int EventsLost);

    public VolumeFileIo(bool details, int[] virtualDisks, VolumePathIndex index, bool frameIntervals = false)
    {
        this.frameIntervals = frameIntervals;
        scope = new TraceScope(details, virtualDisks, index);
        detailStarted = virtualStarted = Stopwatch.GetTimestamp();
        // Run blocks for the whole trace, so it gets its own thread rather than pinning a thread-pool thread.
        Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);
    }

    public void SetScope(bool details, int[] virtualDisks, VolumePathIndex index)
    {
        lock (samples)
        {
            if (scope.Details == details &&
                scope.VirtualDisks.SequenceEqual(virtualDisks) && scope.Paths == index)
                return;
            if (scope.Details != details)
            {
                detailStarted = Stopwatch.GetTimestamp();
                foreach (var bucket in buckets)
                {
                    bucket.Paths = new();
                    bucket.Processes.Clear();
                    bucket.KeylessBytes.Clear();
                }
            }
            var next = new TraceScope(details, virtualDisks, index);
            if (next.CapturesVirtualDisks != scope.CapturesVirtualDisks)
            {
                virtualStarted = Stopwatch.GetTimestamp();
                foreach (var bucket in buckets)
                    bucket.VirtualDisks.Clear();
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
            if (disposed)
                return;
            // ETW startup is slow, so it runs outside `lifecycle`: Dispose must never wait behind it. The session is
            // published only after it is enabled; a Dispose that won the race is handled by the check under the lock.
            ReapAbandonedSessions();
            var opened = OpenSession(owner);
            bool manifestDisk = false;
            try
            {
                try
                {
                    opened.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO |
                        KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.DiskFileIO |
                        KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x800705AA) ||
                    ex.HResult == unchecked((int)0xC000009A) || ex is Win32Exception { NativeErrorCode: 1450 })
                {
                    // Windows allows eight system loggers. These manifest providers also
                    // report disk completions and can run together in an ordinary session.
                    opened.Dispose();
                    opened = OpenSession(owner);
                    opened.EnableProvider(DiskProvider);
                    opened.EnableProvider(FileProvider, TraceEventLevel.Verbose, 0x13B0,
                        new TraceEventProviderOptions { EventIDsToEnable = new[] { 10, 11, 12, 13, 14, 15, 16, 22, 30 } });
                    opened.EnableProvider(ProcessProvider, TraceEventLevel.Informational, 0x10);
                    manifestDisk = true;
                }
                try
                {
                    opened.EnableProvider(FileInfoProvider, TraceEventLevel.Verbose, FileNameEvents);
                    fileInfoAvailable = true;
                }
                catch (UnauthorizedAccessException) { }
                catch (Win32Exception) { }
                lock (lifecycle)
                {
                    if (disposed)
                    {
                        opened.Dispose();
                        return;
                    }
                    session = opened;
                }
            }
            catch
            {
                opened.Dispose();
                throw;
            }

            var source = opened.Source;
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
            source.Kernel.FileIOName += e => RememberFile(e.FileKey, e.FileName, 0);
            source.Kernel.FileIOFileCreate += e => RememberFile(e.FileKey, e.FileName, 1);
            source.Kernel.FileIOFileRundown += e => RememberFile(e.FileKey, e.FileName, 2);
            source.Kernel.FileIOFileDelete += e => RememberFile(e.FileKey, e.FileName, 3);
            source.Kernel.DiskIOReadInit += RecordRequest;
            source.Kernel.DiskIOWriteInit += RecordRequest;
            string rundownOwner = fileInfoAvailable && !manifestDisk ? owner : null;
            queryOpenFiles = manifestDisk;
            if (manifestDisk)
                source.Dynamic.All += e => RecordManifestEvent(e, source);
            else
            {
                source.Kernel.DiskIORead += e => RecordKernelDisk(e, false, source, rundownOwner);
                source.Kernel.DiskIOWrite += e => RecordKernelDisk(e, true, source, rundownOwner);
            }
            System.Threading.Volatile.Write(ref started, Stopwatch.GetTimestamp());
            status = null;
            if (!disposed)
                source.Process();
            if (!disposed)
                status = "Windows ended the file I/O trace. Close and reopen details to restart it.";
        }
        catch (UnauthorizedAccessException)
        {
            using var identity = WindowsIdentity.GetCurrent();
            status = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                ? "Windows denied the file I/O trace even though the collector is elevated."
                : "Windows denied access to file I/O tracing. Open the viewer as administrator.";
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

    private void QueueFileNameRundown(string owner, double time, OpenFile file)
    {
        if (owner == null)
            return;
        if (file is { Path: null, RequestedName: false })
            nameRundownNeeded = true;
        if (!nameRundownNeeded || time < nextNameRundown ||
            System.Threading.Volatile.Read(ref nameRundownRunning) != 0)
            return;

        // Give completions time to identify the files worth retaining. A busy box
        // can emit millions of names per rundown, so coalesce new keys across samples.
        nameRundownNeeded = false;
        bool outstanding = observedFiles.MarkUnnamedForRundown();
        if (!firstNameRundown && !outstanding)
            return;
        firstNameRundown = false;
        nextNameRundown = time + WindowSeconds * 500;
        System.Threading.Volatile.Write(ref nameRundownRunning, 1);
        _ = Task.Run(() => RequestFileNames(owner));
    }

    private async Task RequestFileNames(string owner)
    {
        await RundownGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            // Stopping a kernel disk-file session broadcasts names of already open files.
            // The long-running trace keeps receiving completions during that rundown.
            using var rundown = OpenSession(owner);
            rundown.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskFileIO);
            rundown.Stop();
        }
        catch (Exception) { }
        finally
        {
            RundownGate.Release();
            System.Threading.Volatile.Write(ref nameRundownRunning, 0);
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
        var path = new ReadOnlySpan<char>((byte*)e.DataStart + pathOffset, length).TrimEnd('\0');
        RememberFile(key, path, kind);
    }

    private void RememberFile(ulong key, ReadOnlySpan<char> path, int kind)
    {
        if (disposed || key == 0)
            return;
        var file = FindOpenFile(key);
        if (kind == 3)
        {
            if (file != null)
            {
                if (!path.IsEmpty)
                    file.Path ??= path.ToString();
                ForgetFile(key);
            }
            return;
        }
        if (path.IsEmpty)
            return;
        bool isVirtual = FileIoKey.IsVirtualDisk(path);
        // A full rundown contains millions of entries. Keep names for observed file keys
        // and VHDX attachments, which can stay open for the lifetime of a VM.
        if (file == null && !isVirtual && (!scope.Details || kind == 2))
            return;
        string name = path.ToString();
        if (file == null || (kind == 1 && file.Path != null &&
            !string.Equals(file.Path, name, StringComparison.OrdinalIgnoreCase)))
        {
            ForgetFile(key);
            (isVirtual ? virtualFiles : recentNames).Remember(key, new OpenFile(name));
        }
        else
        {
            file.Path ??= name;
            if (isVirtual && virtualFiles.Find(key) == null)
                RememberVirtualFile(key, file);
        }
    }

    private OpenFile FindOpenFile(ulong key) =>
        virtualFiles.Find(key) ?? observedFiles.Find(key) ?? recentNames.Find(key);

    private void ForgetFile(ulong key)
    {
        virtualFiles.Remove(key);
        observedFiles.Remove(key);
        recentNames.Remove(key);
    }

    private void RememberVirtualFile(ulong key, OpenFile file)
    {
        observedFiles.Remove(key);
        recentNames.Remove(key);
        virtualFiles.Remember(key, file);
    }

    private void RecordRequest(DiskIOInitTraceData e)
    {
        RecordRequest(e, e.Irp);
    }

    private void RecordRequest(TraceEvent e, ulong irp)
    {
        if (disposed || irp == 0 || e.ProcessID <= 0)
            return;
        double time = e.TimeStampRelativeMSec;
        if (requests.Count >= MaxRequests && time >= nextRequestTrim)
        {
            nextRequestTrim = time + 1000;
            foreach (var key in requests.Where(pair => time - pair.Value.Time > WindowSeconds * 1000)
                .Select(pair => pair.Key).ToArray())
                requests.Remove(key);
        }
        if (requests.Count < MaxRequests || requests.ContainsKey(irp))
            requests[irp] = new DiskRequest(e.ProcessID, e.ThreadID, time, processes.GetValueOrDefault(e.ProcessID));
    }

    private void RecordManifestEvent(TraceEvent e, ETWTraceEventSource source)
    {
        int id = (int)e.ID;
        if (e.ProviderGuid == DiskProvider && id is 10 or 11)
        {
            long bytes = Convert.ToUInt32(e.PayloadByName("TransferSize"));
            int disk = Convert.ToInt32(e.PayloadByName("DiskNumber"));
            double elapsed = Convert.ToUInt64(e.PayloadByName("HighResResponseTime")) * 1000.0 / Stopwatch.Frequency;
            RecordDisk(e, id == 11, source, null, disk, bytes,
                Convert.ToUInt64(e.PayloadByName("FileObject")), Convert.ToUInt64(e.PayloadByName("IORequestPacket")), elapsed);
        }
        else if (e.ProviderGuid == FileProvider)
        {
            if (id is 10 or 11)
            {
                var key = Convert.ToUInt64(e.PayloadByName("FileKey"));
                RememberFile(key, (e.PayloadByName("FileName") as string).AsSpan(), id == 11 ? 3 : 1);
                return;
            }
            var fileObject = Convert.ToUInt64(e.PayloadByName("FileObject"));
            if (id is 12 or 30)
            {
                if (fileObject == 0) return;
                manifestOpens.Remove(fileObject);
                if (e.PayloadByName("FileName") is string { Length: > 0 } name && (scope.Details || FileIoKey.IsVirtualDisk(name)))
                    manifestOpens.Remember(fileObject, new OpenFile(name));
                return;
            }
            if (id is 15 or 16)
                RecordRequest(e, Convert.ToUInt64(e.PayloadByName("Irp")));
            var opened = manifestOpens.Find(fileObject);
            if (opened == null) return;
            ulong streamKey = Convert.ToUInt64(e.PayloadByName("FileKey"));
            if (streamKey != 0)
            {
                // An alias can name bytes already captured on another open of the same stream.
                // A name observed directly for the stream takes precedence.
                if (FindOpenFile(streamKey)?.Path == null)
                    RememberFile(streamKey, opened.Path, 0);
                manifestOpens.Remove(fileObject);
            }
            else if (id == 14)
                manifestOpens.Remove(fileObject);
        }
        else if (e.ProviderGuid == ProcessProvider && id is 1 or 2)
        {
            int pid = Convert.ToInt32(e.PayloadByName("ProcessID"));
            if (id == 2)
                processes.Remove(pid);
            else if (e.PayloadByName("ImageName") is string image)
                processes[pid] = System.IO.Path.GetFileName(image);
        }
    }

    private void RecordKernelDisk(DiskIOTraceData e, bool write, ETWTraceEventSource source, string rundownOwner) =>
        RecordDisk(e, write, source, rundownOwner, e.DiskNumber, e.TransferSize, e.FileKey, e.Irp, e.ElapsedTimeMSec);

    private void RecordDisk(TraceEvent e, bool write, ETWTraceEventSource source, string rundownOwner,
        int disk, long bytes, ulong fileKey, ulong irp, double elapsed)
    {
        if (disposed)
            return;
        bool hasRequest = requests.Remove(irp, out var request);
        var selection = scope;
        bool detail = selection.Details;
        bool virtualDisk = Array.IndexOf(selection.VirtualDisks, disk) >= 0;
        if ((!detail && !virtualDisk) || bytes <= 0)
            return;

        OpenFile attachment = fileKey != 0 ? virtualFiles.Find(fileKey) : null;
        OpenFile observed = fileKey != 0 && attachment == null ? observedFiles.Find(fileKey) : null;
        OpenFile file = attachment ?? observed ?? (fileKey != 0 ? recentNames.Find(fileKey) : null);
        if (file?.Path == null)
        {
            string path = (e as DiskIOTraceData)?.FileName;
            if (string.IsNullOrEmpty(path))
                path = null;
            if ((detail && fileKey != 0) || path != null)
            {
                file ??= new OpenFile(null);
                file.Path = path;
            }
        }

        string name = file?.Path;
        bool isVirtual = attachment != null || name != null && FileIoKey.IsVirtualDisk(name);
        if (fileKey != 0 && file != null && attachment == null)
        {
            if (isVirtual)
                RememberVirtualFile(fileKey, file);
            else if (detail && observed == null)
            {
                // File opens with no completed reads or writes cannot evict these identities.
                recentNames.Remove(fileKey);
                observedFiles.Remember(fileKey, file);
            }
        }
        QueueFileNameRundown(rundownOwner, e.TimeStampRelativeMSec, file);
        string virtualPath = virtualDisk && isVirtual ? name : null;
        if (!detail && virtualPath == null)
            return;
        int pid = e.ProcessID;
        string process = processes.GetValueOrDefault(pid) ?? e.ProcessName;
        if (pid <= 0 && hasRequest && (e.ThreadID <= 0 || request.Thread <= 0 || e.ThreadID == request.Thread) &&
            Math.Abs(e.TimeStampRelativeMSec - elapsed - request.Time) < 2)
        {
            pid = request.Pid;
            process = request.Process ?? processes.GetValueOrDefault(pid);
        }
        if (string.IsNullOrWhiteSpace(process))
            process = pid == 4 ? "System" : "Unknown";
        var count = write ? new FileIoTotals { WriteBytes = bytes, Writes = 1 }
            : new FileIoTotals { ReadBytes = bytes, Reads = 1 };
        long now = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        long second = now / BucketSeconds * BucketSeconds;
        lock (samples)
        {
            if (selection != scope)
                return;
            if (!frameIntervals) Trim(now);
            if (currentBucket == null || !frameIntervals && currentBucket.Second != second)
            {
                eventsLost = source.EventsLost;
                currentBucket = new Bucket(second);
                buckets.Enqueue(currentBucket);
            }
            var bucket = currentBucket;
            if (virtualPath != null)
                FileIoTotals.AddTo(bucket.VirtualDisks, (disk, virtualPath), count);
            if (!detail)
                return;
            var key = new FileIoKey(pid, process, "");
            FileIoTotals.AddTo(bucket.Processes, (disk, key), count);
            if (file == null)
            {
                bucket.KeylessBytes[disk] = bucket.KeylessBytes.GetValueOrDefault(disk) + count.TotalBytes;
                return;
            }
            // Aggregate known paths across opens, while keeping the identity of files
            // that can still receive a name from a later trace event.
            string path = file.Path;
            var pathKey = path == null ? new DiskPathKey(disk, key, file)
                : new DiskPathKey(disk, key with { Path = path }, null);
            FileIoTotals.AddTo(bucket.Paths, pathKey, count);
        }
    }

    private void Trim(long now)
    {
        while (buckets.TryPeek(out var oldest) && oldest.Second + BucketSeconds <= now - WindowSeconds)
            buckets.Dequeue();
    }

    public Snapshot Read(bool byPath, int? disk, string virtualDisk, bool includeVirtualPaths, HashSet<int> diskScope = null,
        bool includeDiskFiles = false)
    {
        var entries = new Dictionary<FileIoKey, FileIoTotals>();
        var virtualPaths = includeVirtualPaths ? new Dictionary<FileIoKey, FileIoTotals>() : null;
        var processTotals = new Dictionary<FileIoKey, FileIoTotals>();
        var namedTotals = new Dictionary<FileIoKey, FileIoTotals>();
        var diskFiles = includeDiskFiles ? new Dictionary<(int Disk, FileIoKey File), FileIoTotals>() : null;
        var pathBuckets = new List<Dictionary<DiskPathKey, FileIoTotals>>();
        var readPaths = byPath || virtualPaths != null || virtualDisk != null;
        long diskBytes = 0, namedBytes = 0;
        long keyless = 0, outsideScope = 0;
        bool Includes(int number) => disk.HasValue ? number == disk.Value : diskScope == null || diskScope.Contains(number);
        double seconds;
        DateTimeOffset end;
        VolumePathIndex pathIndex;
        lock (samples)
        {
            pathIndex = scope.Paths;
            long timestamp = Stopwatch.GetTimestamp();
            end = DateTimeOffset.UtcNow;
            long now = timestamp / Stopwatch.Frequency;
            long traceStart = System.Threading.Volatile.Read(ref started);
            long start = traceStart == 0 ? 0 : Math.Max(traceStart, detailStarted);
            long bucketEdge = (now - WindowSeconds) / BucketSeconds * BucketSeconds;
            long floor = Math.Max(start, frameIntervals ? previousFrame : bucketEdge * Stopwatch.Frequency);
            seconds = start == 0 ? 0 : Math.Max(0, Stopwatch.GetElapsedTime(floor, timestamp).TotalSeconds);
            if (!frameIntervals) Trim(now);
            foreach (var bucket in buckets)
            {
                if (virtualDisk == null)
                {
                    foreach (var entry in bucket.Processes)
                        if (Includes(entry.Key.Disk))
                        {
                            FileIoTotals.AddTo(processTotals, entry.Key.Process, entry.Value);
                            if (diskFiles != null) FileIoTotals.AddTo(diskFiles, entry.Key, entry.Value);
                        }
                        else if (!disk.HasValue)
                            outsideScope += entry.Value.TotalBytes;
                }
                // Closed buckets are no longer written. Copy only the live one so
                // path resolution and aggregation don't block the trace consumer.
                if (readPaths && bucket.Paths.Count > 0)
                    pathBuckets.Add(!frameIntervals && bucket == currentBucket ? new(bucket.Paths) : bucket.Paths);
                if (byPath && virtualDisk == null)
                    keyless += bucket.KeylessBytes.Where(entry => Includes(entry.Key)).Sum(entry => entry.Value);
            }
            if (frameIntervals)
            {
                // The next completion opens a new interval. File names and request
                // identities stay alive so an open file can span multiple frames.
                previousFrame = timestamp;
                currentBucket = null;
                buckets.Clear();
            }
        }
        var pending = queryOpenFiles && readPaths ? new Dictionary<int, long>() : null;
        foreach (var bucket in pathBuckets)
            foreach (var entry in bucket)
            {
                if (!Includes(entry.Key.Disk))
                    continue;
                string path = entry.Key.PendingName?.Path ?? entry.Key.File.Path;
                if (pending != null && entry.Key.PendingName != null && string.IsNullOrEmpty(path))
                {
                    int pid = entry.Key.File.Pid;
                    pending[pid] = pending.GetValueOrDefault(pid) + entry.Value.TotalBytes;
                }
                if (string.IsNullOrEmpty(path) || ((!byPath || virtualDisk != null) && !FileIoKey.IsVirtualDisk(path)))
                    continue;
                path = pathIndex.Resolve(path, entry.Key.Disk) ?? pathIndex.Resolve(path, null) ?? path;
                var process = entry.Key.File.ProcessKey;
                if (virtualDisk != null)
                {
                    if (!string.Equals(virtualDisk, VmDiskIo.Normalize(path), StringComparison.OrdinalIgnoreCase))
                        continue;
                    FileIoTotals.AddTo(processTotals, process, entry.Value);
                }
                var file = process with { Path = path };
                if (byPath)
                {
                    FileIoTotals.AddTo(entries, file, entry.Value);
                    FileIoTotals.AddTo(namedTotals, process, entry.Value);
                    if (diskFiles != null)
                    {
                        var key = (entry.Key.Disk, process);
                        diskFiles[key] = diskFiles.GetValueOrDefault(key).Minus(entry.Value);
                        FileIoTotals.AddTo(diskFiles, (entry.Key.Disk, file), entry.Value);
                    }
                }
                if (virtualPaths != null && FileIoKey.IsVirtualDisk(path))
                    FileIoTotals.AddTo(virtualPaths, file, entry.Value);
            }
        foreach (var entry in processTotals)
        {
            diskBytes += entry.Value.TotalBytes;
            if (!byPath)
                continue;
            var named = namedTotals.GetValueOrDefault(entry.Key);
            namedBytes += named.TotalBytes;
            var missing = entry.Value.Minus(named);
            if (missing.Operations > 0)
                entries.Add(entry.Key, missing);
        }
        if (pending != null) fileNameQueries.Resolve(pending);
        return new Snapshot(byPath ? entries : processTotals, diskBytes, namedBytes, seconds, status,
            eventsLost, keyless, outsideScope, end, virtualPaths, diskFiles?.Where(pair => pair.Value.Operations > 0)
                .Select(pair => new DiskFileMeasurement(pair.Key.Disk, pair.Key.File, pair.Value)).ToArray());
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
                        string path = VirtualPath(entry.Key.Path);
                        FileIoTotals.AddTo(entries, (entry.Key.Disk, path), entry.Value);
                    }
        }
        return new VirtualSnapshot(entries.Select(entry => new VirtualDiskIo(entry.Key.Disk, entry.Key.Path, entry.Value)).ToArray(),
            seconds, status, eventsLost);
    }

    public void Dispose()
    {
        lock (lifecycle)
        {
            disposed = true;
            session?.Stop();
        }
        fileNameQueries.Dispose();
    }
}

internal sealed class GuestIoWatch : IDisposable
{
    private readonly System.Threading.CancellationTokenSource stop = new();
    private readonly Queue<(long Completed, GuestIoSample Sample)> frames = new();
    private volatile Snapshot current = new(new(), new(), null, 0, 0, 0, 0, false, 0, null);
    private readonly VmDisk disk;

    public sealed record Snapshot(Dictionary<FileIoKey, FileIoTotals> Paths, Dictionary<FileIoKey, FileIoTotals> Processes,
        string Device, int Seconds, double DiskSeconds, long DiskReadBytes, long DiskWriteBytes,
        bool Limited, long Latest, string Error);

    public GuestIoWatch(VmDisk disk)
    {
        this.disk = disk;
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
                row.AddTo(paths);
        }
        foreach (var entry in paths)
            FileIoTotals.AddTo(processes, entry.Key.ProcessKey, entry.Value);
        current = new Snapshot(paths, processes, device, seconds, diskSeconds, diskRead, diskWrite, limited, latest, error);
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
        VmDiskIo.ReleaseDisk(disk);
    }
}

internal sealed class GuestDiskWatches : IDisposable
{
    private readonly Dictionary<VmDisk, (GuestIoWatch Watch, long LastNeeded)> guestWatches = new();

    internal GuestIoWatch.Snapshot Read(VmDisk disk) => guestWatches.GetValueOrDefault(WatchKey(disk)).Watch?.Read();

    internal static IGrouping<VmDisk, KeyValuePair<FileIoKey, FileIoTotals>>[] Candidates(
        IEnumerable<KeyValuePair<FileIoKey, FileIoTotals>> virtualPaths, Dictionary<string, VmDiskOwners> owners, HashSet<string> expandedGuests)
    {
        return expandedGuests.Count == 0 || owners == null || virtualPaths == null
            ? Array.Empty<IGrouping<VmDisk, KeyValuePair<FileIoKey, FileIoTotals>>>()
            : virtualPaths.Select(entry => (Entry: entry,
                    Disk: owners.GetValueOrDefault(VmDiskIo.Normalize(entry.Key.Path))?.Attachment))
                .Where(pair => pair.Disk != null && expandedGuests.Contains(pair.Disk.ExpansionTarget))
                .GroupBy(pair => WatchKey(pair.Disk), pair => pair.Entry).ToArray();
    }

    internal static VmDisk WatchKey(VmDisk disk) => disk with { Path = "" };

    internal void Track(IEnumerable<VmDisk> disks, HashSet<string> expandedGuests)
    {
        long now = Stopwatch.GetTimestamp();
        var wanted = disks.Select(WatchKey).ToHashSet();
        // A disk filter changes which rows are shown. Keep recent samples across
        // navigation for as long as the host I/O that can use them stays relevant.
        foreach (var entry in guestWatches.Where(entry => !wanted.Contains(entry.Key) &&
            (!expandedGuests.Contains(entry.Key.ExpansionTarget) ||
             Stopwatch.GetElapsedTime(entry.Value.LastNeeded, now).TotalSeconds >= VolumeFileIo.WindowSeconds)).ToArray())
        {
            entry.Value.Watch.Dispose();
            guestWatches.Remove(entry.Key);
        }
        foreach (var disk in wanted)
        {
            var watch = guestWatches.TryGetValue(disk, out var existing) ? existing.Watch : new GuestIoWatch(disk);
            guestWatches[disk] = (watch, now);
        }
    }

    public void Dispose()
    {
        foreach (var entry in guestWatches.Values)
            entry.Watch.Dispose();
        guestWatches.Clear();
    }
}

internal sealed class DiskIoDetails : Panel
{
    private const double BytesPerMiB = 1024 * 1024;
    private static readonly Dictionary<FileIoKey, FileIoTotals> EmptyRows = new();
    private readonly Timer refreshTimer = new() { Interval = 2000 };
    private readonly Label title = new() { AutoEllipsis = true };
    private readonly Label status = new() { AutoEllipsis = true };
    private readonly Label totals = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight,
        AccessibleName = "Table I/O totals", Visible = false };
    private readonly ToolTip detailsHelp = new();
    private readonly Button collapse = new() { Text = "Collapse" };
    private readonly Button close = new() { Text = "Close" };
    private readonly TabControl tabs = new();
    private readonly TabPage processTab = new("Processes");
    private readonly TabPage pathTab = new("Paths");
    private readonly TabPage guestProcessTab = new("Guest Processes");
    private readonly TabPage guestPathTab = new("Guest Paths");
    private readonly RankedTable<DiskIoRow> paths = NewTable(true);
    private readonly RankedTable<DiskIoRow> processes = NewTable(false);
    private readonly RankedTable<DiskIoRow> guestPaths = NewTable(true, guestOnly: true);
    private readonly RankedTable<DiskIoRow> guestProcesses = NewTable(false, guestOnly: true);
    private readonly Button allDisks = new() { Text = "Clear disk" };
    private bool detailsOpen;
    private string filter = "";
    private VolumeFileIo capture;
    private System.Threading.CancellationTokenSource inventoryCancellation;
    private Task<Dictionary<string, VmDiskOwners>> inventory;
    private VolumePathIndex inventoryPathIndex;
    private readonly GuestDiskWatches guestWatches = new();
    private HashSet<string> expandedGuests = new(StringComparer.OrdinalIgnoreCase);
    private bool groupByMachine;
    private int? bandWidth;
    private HashSet<int> fileDiskScope;
    private string fileDiskScopeLabel = "All disks";
    private bool showAttributionGaps;
    private int topRows;
    private readonly ProcessCommands commands = new();
    private VmDisk guestDisk;
    private string diskName;
    private string totalsFull, totalsCompact;
    private int? selectedDisk;
    private string selectedPath;
    private bool openVirtualGuest;
    private int[] catalogDisks = Array.Empty<int>();
    private VolumePathIndex captureIndex;
    private bool expanded = true;

    private sealed record RowSelection(FileIoKey Key, string Machine);

    public bool HasSelection => selectedDisk.HasValue;
    public Dictionary<string, VmDiskOwners> VirtualDiskOwners =>
        inventory is { IsCompletedSuccessfully: true } ? inventory.Result : null;
    public event EventHandler LayoutChanged;
    public event EventHandler CloseRequested;

    public DiskIoDetails()
    {
        Size = new Size(600, 300);
        title.SetBounds(8, 6, Width - 255, 23);
        title.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        status.SetBounds(8, 32, Width - 16, 21);
        status.Anchor = title.Anchor;
        collapse.SetBounds(Width - 154, 2, 76, 26);
        close.SetBounds(Width - 72, 2, 64, 26);
        allDisks.SetBounds(Width - 239, 2, 79, 26);
        allDisks.Anchor = collapse.Anchor = close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        allDisks.Click += (_, _) => SelectAllDisks();
        collapse.Click += (_, _) => SetExpanded(!expanded);
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
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
        {
            table.RankingChanged += (_, _) => RefreshRows();
            table.ViewportChanged += (_, _) => commands.Refresh();
        }
        paths.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenGuest(paths.GetItemAt(e.X, e.Y));
        };
        paths.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && paths.SelectedIndices.Count > 0)
            {
                e.Handled = true;
                OpenGuest(paths.Items[paths.SelectedIndices[0]]);
            }
        };
        foreach (var table in new[] { paths, guestPaths })
            table.KeyDown += (_, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C && table.SelectedIndices.Count > 0 &&
                    table.Items[table.SelectedIndices[0]].Tag is RowSelection { Key.Path.Length: > 0 } row)
                {
                    try { Clipboard.SetText(row.Key.Path); }
                    catch (ExternalException) { }
                    e.Handled = true;
                }
            };
        Controls.AddRange(new Control[] { title, status, allDisks, collapse, close, tabs, totals });
        totals.BringToFront();
        Visible = false;
    }

    private static RankedTable<DiskIoRow> NewTable(bool includePath, bool guestOnly = false)
    {
        var table = new RankedTable<DiskIoRow>();
        if (includePath)
            table.AddColumn("Filename", 170, row => row.Key.Filename);
        ColumnHeader path = includePath ? table.AddColumn("Filepath", 220, row => row.Key.Filepath) : null;
        var process = table.AddColumn("Process", 120, row => row.Key.Process);
        table.AddColumn("Machine", 110, row => row.Machine);
        string basis = guestOnly ? "Guest " : "";
        string total = guestOnly ? "Guest MiB/s" : "I/O impact MiB/s";
        var rank = table.AddMetric(total, guestOnly ? 108 : 126, row => row.Totals.TotalBytes, MiBRate);
        table.AddMetric(basis + "Read MiB/s", guestOnly ? 110 : 85, row => row.Totals.ReadBytes, MiBRate);
        table.AddMetric(basis + "Write MiB/s", guestOnly ? 110 : 85, row => row.Totals.WriteBytes, MiBRate);
        table.AddMetric("Ops/s", 70, row => row.Totals.Operations, Rate);
        if (!guestOnly)
            table.AddMetric("Guest MiB/s", 100, row => row.Guest?.Total, (value, _) => FormatRate(value));
        table.AddColumn("PID", 65, row => row.Key.Pid, numeric: true);
        table.AddMetric("Total MiB", 85, row => row.Totals.TotalBytes, (value, _) => FormatRate(value / BytesPerMiB));
        table.AddMetric("Reads", 70, row => row.Totals.Reads, (value, _) => FormatCount(value));
        table.AddMetric("Writes", 70, row => row.Totals.Writes, (value, _) => FormatCount(value));
        table.AddMetric("Total ops", 90, row => row.Totals.Operations, (value, _) => FormatCount(value));
        if (!guestOnly)
        {
            table.AddMetric("Guest read MiB/s", 130, row => row.Guest?.Read, (value, _) => FormatRate(value));
            table.AddMetric("Guest write MiB/s", 130, row => row.Guest?.Write, (value, _) => FormatRate(value));
        }
        if (!includePath)
            table.AddColumn("Command line", 470, null);
        table.SortBy(rank);
        table.SizeChanged += (_, _) =>
        {
            var flexible = path ?? process;
            int available = table.ClientSize.Width - 20 -
                table.Columns.Cast<ColumnHeader>().Take(path != null ? 9 : 7)
                    .Where(column => column != flexible).Sum(column => column.Width);
            flexible.Width = path == null ? Math.Clamp(available, 150, 245) : Math.Max(150, available);
        };
        return table;
    }

    private static string FormatRate(double? value) => value?.ToString("N2") ?? "—";
    private static string FormatCount(double value) => value.ToString("N0");
    private static string Rate(double amount, double seconds) => seconds > 0 ? FormatRate(amount / seconds) : "—";
    private static string MiBRate(double amount, double seconds) => Rate(amount / BytesPerMiB, seconds);

    public int HeightFor(int available) => expanded ? Math.Min(345, Math.Max(180, available * 3 / 5)) : 34;

    public void SetDiskScope(HashSet<int> disks, string label)
    {
        fileDiskScope = disks;
        fileDiskScopeLabel = label;
    }

    public void SetOptions(CpuExpansionTarget[] guests, bool group, int limit, string search, int? bands, bool showGaps)
    {
        expandedGuests = guests.Select(guest => guest.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        groupByMachine = group;
        topRows = limit;
        bandWidth = bands;
        showAttributionGaps = showGaps;
        filter = search;
        RefreshRows();
    }

    public void ApplyColors(Color background, Color foreground, Color card)
    {
        BackColor = card;
        ForeColor = foreground;
        foreach (var table in new[] { paths, processes, guestPaths, guestProcesses })
        {
            table.BackColor = background;
            table.ForeColor = foreground;
        }
        allDisks.BackColor = collapse.BackColor = close.BackColor = totals.BackColor = card;
    }

    public void SelectDisk(int diskNumber, string name, VolumePathIndex index) =>
        Select(diskNumber, name, null, index);

    public void SelectVirtualDisk(int diskNumber, string path, VolumePathIndex index) =>
        Select(diskNumber, System.IO.Path.GetFileName(path), path, index);

    private void Select(int diskNumber, string name, string path, VolumePathIndex index)
    {
        bool newSelection = selectedDisk != diskNumber || !string.Equals(selectedPath, path, StringComparison.OrdinalIgnoreCase);
        if (newSelection)
            openVirtualGuest = false;
        selectedDisk = diskNumber;
        selectedPath = path == null ? null : VmDiskIo.Normalize(path);
        diskName = name;
        captureIndex = index;
        UpdateCapture();
        if (newSelection)
        {
            ClearGuest();
            openVirtualGuest = path != null;
            tabs.SelectedTab = processTab;
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
        if (!detailsOpen && catalogDisks.Length == 0)
        {
            capture?.Dispose();
            capture = null;
            inventoryCancellation?.Cancel();
            inventoryCancellation?.Dispose();
            inventoryCancellation = null;
            inventory = null;
            return;
        }
        if (capture == null)
            capture = new VolumeFileIo(detailsOpen, catalogDisks, captureIndex);
        else
            capture.SetScope(detailsOpen, catalogDisks, captureIndex);
        if (inventory == null || inventoryPathIndex != captureIndex)
        {
            inventoryCancellation?.Cancel();
            inventoryCancellation?.Dispose();
            inventoryCancellation = new();
            inventoryPathIndex = captureIndex;
            var inventoryToken = inventoryCancellation.Token;
            var inventoryIndex = inventoryPathIndex;
            inventory = Task.Run(() => VmDiskIo.ReadInventory(inventoryToken, inventoryIndex), inventoryToken);
        }
    }

    private void SetExpanded(bool value)
    {
        expanded = value;
        collapse.Text = expanded ? "Collapse" : "Expand";
        tabs.Visible = status.Visible = expanded;
        if (!expanded)
        {
            totals.Visible = false;
            commands.Suspend();
            guestWatches.Dispose();
        }
        refreshTimer.Enabled = expanded && detailsOpen;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        RefreshRows();
    }

    public void SetOpen(bool open, VolumePathIndex index)
    {
        captureIndex = index;
        if (detailsOpen == open)
        {
            UpdateCapture();
            return;
        }
        detailsOpen = open;
        UpdateCapture();
        if (open)
            SetExpanded(true);
        else
        {
            refreshTimer.Stop();
            commands.Suspend();
            guestWatches.Dispose();
            totals.Visible = false;
        }
    }

    public void SelectAllDisks()
    {
        selectedDisk = null;
        selectedPath = null;
        openVirtualGuest = false;
        ClearGuest();
        RefreshRows();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            guestWatches.Dispose();
            inventoryCancellation?.Cancel();
            inventoryCancellation?.Dispose();
            refreshTimer.Dispose();
            commands.Dispose();
            detailsHelp.Dispose();
            capture?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void RefreshRows()
    {
        if (!detailsOpen || capture == null || !expanded)
            return;
        if (openVirtualGuest && inventory is { IsCompleted: true })
        {
            openVirtualGuest = false;
            var owner = VirtualDiskOwners?.GetValueOrDefault(VmDiskIo.Normalize(selectedPath));
            if (owner?.Attachment != null)
            {
                OpenGuest(owner.Attachment, selectTab: false);
                return;
            }
        }
        bool guestByPath = tabs.SelectedTab == guestPathTab;
        bool inGuest = guestDisk != null && (guestByPath || tabs.SelectedTab == guestProcessTab);
        title.Text = inGuest ? guestDisk.VMName + "  |  " + System.IO.Path.GetFileName(guestDisk.Path) : "File I/O  |  " + (HasSelection ? diskName : fileDiskScopeLabel);
        allDisks.Visible = HasSelection;
        bool byPath = tabs.SelectedTab == pathTab;
        var snapshot = capture.Read(byPath, selectedDisk, selectedPath, expandedGuests.Count > 0 && !byPath, fileDiskScope);
        var entries = snapshot.Entries;
        var owners = VirtualDiskOwners;
        IEnumerable<KeyValuePair<FileIoKey, FileIoTotals>> virtualPaths = byPath ? entries : snapshot.VirtualPaths;
        var candidates = GuestDiskWatches.Candidates(virtualPaths, owners, expandedGuests);
        guestWatches.Track(guestDisk == null ? candidates.Select(group => group.Key) :
            candidates.Select(group => group.Key).Append(guestDisk), expandedGuests);
        if (inGuest)
        {
            RefreshGuest(guestByPath);
            return;
        }
        var guestSamples = candidates.Select(group => (Group: group, Sample: guestWatches.Read(group.Key))).ToArray();
        var expansions = guestSamples.Where(pair => pair.Sample.Seconds > 0 && pair.Sample.Paths.Count > 0)
            .Select(pair => new DiskIoExpansion(pair.Group.Key.VMName, pair.Group.ToArray(),
                byPath ? pair.Sample.Paths : pair.Sample.Processes, pair.Sample.Seconds)).ToArray();
        string window = snapshot.DiskBytes == 0 ? "No disk I/O in this sample" : $"{snapshot.Seconds:0} s sample";
        if (byPath && selectedPath == null && snapshot.DiskBytes > 0)
            window += $"  |  {(double)snapshot.NamedBytes / snapshot.DiskBytes:P0} of Windows bytes matched to paths";
        status.Text = snapshot.Status ?? window;
        var notes = new List<string>();
        if (snapshot.OutsideScopeBytes > 0 && snapshot.Seconds > 0)
        {
            notes.Add($"Disks excluded by {fileDiskScopeLabel} contributed {snapshot.OutsideScopeBytes / BytesPerMiB / snapshot.Seconds:N1} MiB/s.");
            notes.Add("This can include Storage Spaces backing drives doing work already measured on the virtual disks. Choose All disks under Disk filters to see those devices.");
        }
        if (snapshot.EventsLost > 0)
            notes.Add($"Windows dropped {snapshot.EventsLost:N0} trace events.");
        if (byPath && selectedPath == null && snapshot.DiskBytes > snapshot.NamedBytes)
        {
            long missingName = snapshot.DiskBytes - snapshot.NamedBytes - snapshot.KeylessBytes;
            if (snapshot.KeylessBytes > 0)
                notes.Add($"Windows supplied no file key for {snapshot.KeylessBytes / BytesPerMiB:N1} MiB.");
            if (missingName > 0)
                notes.Add($"The trace had no filename for the file keys on {missingName / BytesPerMiB:N1} MiB.");
        }
        if (byPath && inventory is { IsFaulted: true })
            notes.Add(inventory.Exception.GetBaseException().Message);
        if (byPath && owners != null && entries.Keys.Any(key => owners.TryGetValue(VmDiskIo.Normalize(key.Path), out var match) && match.Attachment != null))
            notes.Add("Double-click a VHDX to see guest I/O.");
        var rows = DiskIoMerge.Expand(Environment.MachineName, entries, expansions, byPath);
        bool hasGuestRows = rows.Any(row => row.Guest != null);
        if (hasGuestRows)
            notes.Add("Guest I/O impact is estimated from the guest's share of the VHDX traffic.");
        var pendingRows = guestSamples.Where(candidate => candidate.Sample.Seconds <= 0 || candidate.Sample.Paths.Count == 0)
            .SelectMany(candidate => candidate.Group.Select(entry => (Key: byPath ? entry.Key : entry.Key.ProcessKey,
                Disk: $"{candidate.Group.Key.VMName} / {entry.Key.Filename}", candidate.Sample.Error)))
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => (
                Label: group.All(entry => entry.Error != null) ? "Retrying…" : "Sampling…",
                Help: string.Join("\n", group.Select(entry => entry.Error == null
                    ? $"Collecting guest I/O for {entry.Disk}." : $"{entry.Disk} — {entry.Error}").Distinct())));
        var totals = FillTable(byPath ? paths : processes, rows, byPath, guestOnly: false, snapshot.Seconds, owners, pendingRows);
        SetStatusHelp(string.Join("\n", notes.Prepend(status.Text)));
        string description = hasGuestRows
            ? "Guest rows divide their VHDX's measured Windows I/O using the guest's file I/O proportions."
            : "Completed Windows I/O for the current selection.";
        ShowTotals(totals, snapshot.Seconds, description);
    }

    private void OpenGuest(ListViewItem item)
    {
        if (item?.Tag is RowSelection row &&
            VirtualDiskOwners?.GetValueOrDefault(VmDiskIo.Normalize(row.Key.Path))?.Attachment is { } attachment &&
            row.Machine == Environment.MachineName)
            OpenGuest(attachment);
    }

    private void OpenGuest(VmDisk attachment, bool selectTab = true)
    {
        guestDisk = attachment;
        if (!tabs.TabPages.Contains(guestPathTab))
            tabs.TabPages.AddRange(new[] { guestProcessTab, guestPathTab });
        if (selectTab)
            tabs.SelectedTab = guestPathTab;
        RefreshRows();
    }

    private void ClearGuest()
    {
        guestDisk = null;
        tabs.TabPages.Remove(guestPathTab);
        tabs.TabPages.Remove(guestProcessTab);
        guestPaths.ClearRows();
        guestProcesses.ClearRows();
    }

    private void RefreshGuest(bool byPath)
    {
        var table = byPath ? guestPaths : guestProcesses;
        var sample = guestWatches.Read(guestDisk);
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
        var totals = FillTable(table, entries.Select(entry => new DiskIoRow(entry.Key, guestDisk.VMName, entry.Value)).ToArray(),
            byPath, guestOnly: true, sample?.Seconds ?? 0, null);
        SetStatusHelp(status.Text);
        string comparison = sample is { DiskSeconds: > 0 }
            ? $"Guest disk counters during the sample runs: read {sample.DiskReadBytes / BytesPerMiB / sample.DiskSeconds:0.##} MiB/s, write {sample.DiskWriteBytes / BytesPerMiB / sample.DiskSeconds:0.##} MiB/s."
            : "Guest disk counters are unavailable.";
        ShowTotals(totals, sample?.Seconds ?? 0, "Observed guest ext4 file I/O, including cached I/O. File rate uses sampled time. " + comparison);
    }

    private void SetStatusHelp(string description)
    {
        if (detailsHelp.GetToolTip(status) != description)
            detailsHelp.SetToolTip(status, description);
        status.AccessibleDescription = description;
    }

    private void ShowTotals(FileIoTotals values, double seconds, string description)
    {
        double read = values.ReadBytes / BytesPerMiB, write = values.WriteBytes / BytesPerMiB;
        double total = values.TotalBytes / BytesPerMiB;
        totalsFull = seconds <= 0 ? "Waiting for I/O totals..." :
            $"Read {read:N2} MiB / {values.Reads:N0} ops   |   Write {write:N2} MiB / {values.Writes:N0} ops   |   Total {total:N2} MiB / {values.Operations:N0} ops ({total / seconds:N2} MiB/s)";
        totalsCompact = seconds <= 0 ? totalsFull :
            $"R {read:N2}  |  W {write:N2}  |  Total {total:N2} MiB ({total / seconds:N2} MiB/s)";
        string help = $"{totalsFull}\n{description}\nIncludes all matching I/O, including attribution gaps.";
        if (detailsHelp.GetToolTip(totals) != help)
            detailsHelp.SetToolTip(totals, help);
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

    private FileIoTotals FillTable(RankedTable<DiskIoRow> table, DiskIoRow[] entries, bool byPath, bool guestOnly,
        double seconds, Dictionary<string, VmDiskOwners> owners,
        Dictionary<FileIoKey, (string Label, string Help)> pendingRows = null)
    {
        var matched = RowFilter.Apply(entries, filter, row => new[] { row.Key.Process, row.Machine,
            row.Key.Pid.ToString(), row.Key.Path }).ToArray();
        var gaps = matched.Where(IsGap).ToArray();
        if (gaps.Length > 0 && seconds > 0)
        {
            if (byPath)
                status.Text = $"Unresolved paths {MiBRate(gaps.Sum(row => row.Totals.TotalBytes), seconds)} MiB/s  |  " + status.Text;
            else
            {
                long kernel = gaps.Where(IsWindowsKernel).Sum(row => row.Totals.TotalBytes);
                long unidentified = gaps.Sum(row => row.Totals.TotalBytes) - kernel;
                if (unidentified > 0)
                    status.Text = $"Unidentified process {MiBRate(unidentified, seconds)} MiB/s  |  " + status.Text;
                if (kernel > 0)
                    status.Text = $"Windows kernel {MiBRate(kernel, seconds)} MiB/s  |  " + status.Text;
            }
        }

        var ranked = showAttributionGaps ? matched : matched.Where(row => !IsGap(row) || Pending(row).Label != null);
        table.Render(ranked, row => row.Machine, groupByMachine, topRows, Item, bandWidth is int width
            ? (width, row => row.Totals.TotalBytes, (double)entries.Sum(row => row.Totals.TotalBytes), "I/O") : null,
            (row, key) => key is RowSelection selection && selection.Key == row.Key && selection.Machine == row.Machine,
            seconds);
        if (byPath)
            commands.Suspend();
        else
            commands.Update(table, table.Columns.Count - 1, item => item.Tag is RowSelection row ? CommandIdentity(row) : null);
        return DiskIoMerge.Sum(matched.Select(row => row.Totals));

        static bool IsWindowsKernel(DiskIoRow row) => row.Machine == Environment.MachineName && row.Key.Pid == 4;
        bool IsGap(DiskIoRow row) => byPath ? row.Key.Path.Length == 0 || row.Key.Path == "(path unavailable)" :
            IsWindowsKernel(row) || row.Key.Pid <= 0 || row.Key.Process.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

        static ProcessIdentity CommandIdentity(RowSelection row)
        {
            // The WSL trace reports kernel PIDs, which can differ from the distro's /proc IDs.
            int pid = row.Machine.StartsWith("WSL / ", StringComparison.OrdinalIgnoreCase) ? 0 : row.Key.Pid;
            return new ProcessIdentity(row.Machine, pid, row.Key.Process);
        }

        (string Label, string Help) Pending(DiskIoRow row) =>
            row.Guest == null && pendingRows != null ? pendingRows.GetValueOrDefault(row.Key) : default;

        ListViewItem Item(DiskIoRow row, ListViewGroup group)
        {
            var value = row.Totals;
            string path = row.Key.Path;
            var vm = byPath && row.Guest == null ? owners?.GetValueOrDefault(VmDiskIo.Normalize(path)) : null;
            var pending = Pending(row);
            bool wslInactive = vm?.Attachment is { WslDistro: not null } wsl && !expandedGuests.Contains(wsl.ExpansionTarget);
            var cells = new List<string>();
            if (byPath)
            {
                cells.Add(IsGap(row) ? "Unresolved file path" : row.Key.Filename);
                cells.Add(IsGap(row) ? "" : row.Key.Filepath);
            }
            string process = IsWindowsKernel(row) ? "System (Windows kernel)" :
                row.Key.Pid <= 0 ? "Unidentified process" : row.Key.Process;
            cells.AddRange(new[] { process, row.Machine,
                MiBRate(value.TotalBytes, seconds), MiBRate(value.ReadBytes, seconds),
                MiBRate(value.WriteBytes, seconds), Rate(value.Operations, seconds) });
            if (!guestOnly)
                cells.Add(row.Guest?.Total.ToString("N2") ?? pending.Label ?? (wslInactive ? "WSL inactive" : "—"));
            cells.AddRange(new[] { row.Key.Pid > 0 ? row.Key.Pid.ToString() : "—",
                FormatRate(value.TotalBytes / BytesPerMiB), FormatCount(value.Reads), FormatCount(value.Writes), FormatCount(value.Operations) });
            if (!guestOnly)
                cells.AddRange(new[] { FormatRate(row.Guest?.Read), FormatRate(row.Guest?.Write) });
            if (!byPath)
                cells.Add("");
            string help = byPath ? path + (vm?.Label != null ? "\nVM: " + vm.Label : "") : "";
            if (wslInactive)
                help += "\nGuest file sampling requires a running distribution and WSL selected under Guests.";
            if (pending.Help != null)
                help = help.Length == 0 ? pending.Help : help + "\n" + pending.Help;
            var selection = new RowSelection(row.Key, row.Machine);
            var item = new ListViewItem(cells.ToArray(), group)
            {
                Tag = selection,
                ToolTipText = help
            };
            if (!byPath)
                commands.Fill(item, cells.Count - 1, CommandIdentity(selection));
            return item;
        }
    }
}
