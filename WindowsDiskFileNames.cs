using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

// A metadata query on a named open causes Kernel-File to report its stream key.
// The ETW session alone decides which earlier disk completions belong to that key.
internal sealed class WindowsDiskFileNames : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<int, long> attempted = new();
    private Task worker = Task.CompletedTask;
    private long next;
    private bool disposed;

    public void Resolve(Dictionary<int, long> pending)
    {
        lock (attempted)
        {
            long now = Environment.TickCount64;
            if (disposed || pending.Count == 0 || now < next || !worker.IsCompleted) return;
            foreach (var pid in attempted.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
                attempted.Remove(pid);
            var targets = pending.Where(p => p.Key > 4 && p.Key != Environment.ProcessId && !attempted.ContainsKey(p.Key))
                .OrderByDescending(p => p.Value).Take(4).Select(p => p.Key).ToArray();
            next = now + 5000;
            if (targets.Length == 0) return;
            foreach (int pid in targets) attempted[pid] = now + VolumeFileIo.WindowSeconds * 1000L;
            var cancellation = stop.Token;
            worker = Task.Run(() => Run(targets, cancellation));
        }
    }

    private static async Task Run(int[] targets, CancellationToken cancellation)
    {
        // A filesystem driver may stall metadata queries. Keep the ETW consumer and UI independent.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var executable = Environment.ProcessPath;
            var assembly = Assembly.GetEntryAssembly();
            if (executable == null || assembly?.EntryPoint?.DeclaringType != typeof(Timeline)) return;
            var args = new List<string>();
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                args.Add(Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll"));
            args.Add("--resolve-disk-files");
            args.AddRange(targets.Select(id => id.ToString()));
            await VmDiskIo.Run(executable, args, null, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or Win32Exception or InvalidOperationException) { }
    }

    public void Dispose()
    {
        lock (attempted)
        {
            if (disposed) return;
            disposed = true;
            stop.Cancel();
            _ = worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        }
    }

    public static int Query(string[] args)
    {
        foreach (var arg in args)
            if (int.TryParse(arg, out int pid) && pid > 4) Query(pid);
        return 0;
    }

    private static unsafe void Query(int pid)
    {
        const int queryInformation = 0x0400, vmRead = 0x0010, duplicateHandle = 0x0040;
        using var process = OpenProcess(queryInformation | vmRead | duplicateHandle, false, pid);
        if (process.IsInvalid) return;
        const int handlesWithNamesAndBasicInfo = 0x04 | 0x08 | 0x10;
        if (PssCaptureSnapshot(process, handlesWithNamesAndBasicInfo, 0, out var snapshot) != 0) return;
        try
        {
            if (PssWalkMarkerCreate(0, out var marker) != 0) return;
            try
            {
                var queried = new HashSet<string>(StringComparer.Ordinal);
                char* buffer = stackalloc char[32768];
                const int walkHandles = 2;
                const uint haveType = 1;
                HandleEntry entry;
                while (PssWalkSnapshot(snapshot, walkHandles, marker, &entry, sizeof(HandleEntry)) == 0)
                {
                    if ((entry.Flags & haveType) == 0 || entry.TypeName == null ||
                        !new ReadOnlySpan<char>(entry.TypeName, entry.TypeNameLength / sizeof(char))
                        .TrimEnd('\0').SequenceEqual("File")) continue;
                    QueryFile(process, entry.Handle, buffer, queried);
                }
            }
            finally { PssWalkMarkerFree(marker); }
        }
        finally { PssFreeSnapshot(new IntPtr(-1), snapshot); }
    }

    private const int ReadAttributes = 0x80;
    private static unsafe void QueryFile(SafeProcessHandle process, IntPtr handle, char* buffer, HashSet<string> queried)
    {
        if (!DuplicateHandle(process, handle, new IntPtr(-1), out var metadata, ReadAttributes, false, 0)) return;
        using (metadata)
        {
            if (GetFileType(metadata) != 1) return;
            uint length = GetFinalPathNameByHandleW(metadata, buffer, 32768, 0x02 | 0x08);
            if (length == 0 || length >= 32768) return;
            var name = new ReadOnlySpan<char>(buffer, (int)length);
            if (!name.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase)) return;
            string path = @"\\?\GLOBALROOT" + name.ToString();
            if (!queried.Add(path)) return;
            using var alias = CreateFileW(path, ReadAttributes, 7, 0, 3, 0, 0);
            if (!alias.IsInvalid) GetFileSizeEx(alias, out _);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HandleEntry
    {
        public IntPtr Handle;
        public uint Flags, ObjectType;
        public System.Runtime.InteropServices.ComTypes.FILETIME CaptureTime;
        public uint Attributes, GrantedAccess, HandleCount, PointerCount, PagedPoolCharge, NonPagedPoolCharge;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public ushort TypeNameLength;
        public char* TypeName;
        public ushort ObjectNameLength;
        public char* ObjectName;
        // Thread is the largest arm of the native PSS_HANDLE_ENTRY union.
        public ThreadHandleInformation TypeSpecificInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadHandleInformation
    {
        public uint ExitStatus;
        public IntPtr TebBaseAddress;
        public uint ProcessId, ThreadId;
        public UIntPtr AffinityMask;
        public int Priority, BasePriority;
        public IntPtr Win32StartAddress;
    }

    [DllImport("kernel32")] private static extern SafeProcessHandle OpenProcess(int rights, bool inherit, int pid);
    [DllImport("kernel32")] private static extern uint PssCaptureSnapshot(SafeProcessHandle process, int flags, int context, out IntPtr snapshot);
    [DllImport("kernel32")] private static extern uint PssFreeSnapshot(IntPtr owner, IntPtr snapshot);
    [DllImport("kernel32")] private static extern uint PssWalkMarkerCreate(IntPtr allocator, out IntPtr marker);
    [DllImport("kernel32")] private static extern uint PssWalkMarkerFree(IntPtr marker);
    [DllImport("kernel32")] private static extern unsafe uint PssWalkSnapshot(IntPtr snapshot, int kind, IntPtr marker, HandleEntry* entry, int size);
    [DllImport("kernel32")] private static extern bool DuplicateHandle(SafeProcessHandle process, IntPtr source, IntPtr current, out SafeFileHandle result, int access, bool inherit, int options);
    [DllImport("kernel32")] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32", ExactSpelling = true)] private static extern unsafe uint GetFinalPathNameByHandleW(SafeFileHandle handle, char* path, uint length, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern SafeFileHandle CreateFileW(string path, int access, int share, IntPtr security, int creation, int flags, IntPtr template);
    [DllImport("kernel32")] private static extern bool GetFileSizeEx(SafeFileHandle handle, out long length);
}
