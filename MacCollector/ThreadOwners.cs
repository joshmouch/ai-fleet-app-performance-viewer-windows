using System.Diagnostics;
using System.Runtime.InteropServices;

sealed record ProcessOwner(int Pid, string Name, long Started);
sealed class ThreadOwners(int? selected)
{
    [DllImport("/usr/lib/libproc.dylib")] static extern int proc_pidinfo(int pid, int flavor, ulong arg, [Out] ulong[] buffer, int size);
    readonly ulong[] buffer = new ulong[16384];
    readonly object gate = new();
    readonly Dictionary<ulong, (ProcessOwner Owner, long LastSeen)> owners = new();
    public int Failures { get; private set; }
    public ProcessOwner Find(ulong tid) { lock (gate) return owners.TryGetValue(tid, out var value) ? value.Owner : null; }
    public void Refresh()
    {
        var now = Stopwatch.GetTimestamp();
        var seen = new List<(ulong Tid, ProcessOwner Owner)>();
        var failures = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (selected is int pid && process.Id != pid) continue;
                try
                {
                    var identity = new ProcessOwner(process.Id, process.ProcessName, process.StartTime.ToUniversalTime().Ticks);
                    // XNU PROC_PIDLISTTHREADIDS=28. Flavor 6 returns handles, not fs_usage kernel IDs.
                    var bytes = proc_pidinfo(process.Id, 28, 0, buffer, buffer.Length * sizeof(ulong));
                    if (bytes <= 0 || bytes >= buffer.Length * sizeof(ulong)) { failures++; continue; }
                    process.Refresh();
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.Started) { failures++; continue; }
                    for (var i = 0; i < bytes / sizeof(ulong); i++) seen.Add((buffer[i], identity));
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { failures++; }
            }
        }
        lock (gate)
        {
            foreach (var item in seen) owners[item.Tid] = (item.Owner, now);
            // Retain exited owners for delayed completions. Expiry never changes accounted rows.
            foreach (var tid in owners.Where(x => Stopwatch.GetElapsedTime(x.Value.LastSeen, now).TotalSeconds > 10).Select(x => x.Key).ToArray()) owners.Remove(tid);
            Failures = failures;
        }
    }
}
