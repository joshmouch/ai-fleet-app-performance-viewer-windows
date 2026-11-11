using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

internal sealed record CpuProcessRow(int Pid, string Process, double Cores);
internal sealed record CpuProcessSample(int LogicalProcessors, double Seconds, CpuProcessRow[] Rows,
    double TotalCores = 0, double ProcessCores = 0);
internal sealed record CpuMachineProcesses(string Name, CpuProcessRow[] Rows, double? VmCores = null);
internal sealed record ClusterCpuSample(CpuProcessSample Host, CpuMachineProcesses[] Guests, string[] Unexpanded);
internal sealed record CpuExpansionTarget(string Name, bool IsWsl = false)
{
    public override string ToString() => Name;
}

internal static class CpuProcessSampler
{
    private sealed record Guest(string Name, int WorkerPid);
    private sealed record GuestOwner(string Name, HashSet<int> AccountingPids);
    private static readonly string Wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    public static CpuExpansionTarget[] ReadTargets()
    {
        var targets = new List<CpuExpansionTarget>();
        try
        {
            targets.AddRange(ReadGuests().Select(guest => new CpuExpansionTarget(guest.Name)));
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException) { }
        if (FindWslProcess().HasValue)
            targets.Add(new CpuExpansionTarget("WSL", IsWsl: true));
        return targets.ToArray();
    }

    private static Guest[] ReadGuests()
    {
        using var query = new ManagementObjectSearcher(@"root\virtualization\v2",
            "SELECT ElementName, ProcessID FROM Msvm_ComputerSystem WHERE EnabledState = 2");
        query.Options.Timeout = TimeSpan.FromSeconds(5);
        using var rows = query.Get();
        var guests = new List<Guest>();
        foreach (ManagementObject row in rows)
            using (row)
            {
                int pid = Convert.ToInt32(row["ProcessID"]);
                string name = (string)row["ElementName"];
                if (pid > 0 && VmDiskIo.IsGuestAddress(name))
                    guests.Add(new Guest(name, pid));
            }
        return guests.OrderBy(guest => guest.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static GuestOwner[] ReadGuestOwners(HashSet<string> selected)
    {
        var guests = ReadGuests().Where(guest => selected.Contains(guest.Name)).ToArray();
        if (guests.Length == 0)
            return Array.Empty<GuestOwner>();

        var processesByWorker = guests.ToDictionary(guest => guest.WorkerPid, _ => new HashSet<int>());
        using var query = new ManagementObjectSearcher(
            "SELECT Name, ProcessId, ParentProcessId FROM Win32_Process WHERE Name LIKE 'vmmem%'");
        query.Options.Timeout = TimeSpan.FromSeconds(5);
        using var rows = query.Get();
        foreach (ManagementObject row in rows)
            using (row)
            {
                if (IsVmmem((string)row["Name"]) &&
                    processesByWorker.TryGetValue(Convert.ToInt32(row["ParentProcessId"]), out var processes))
                    processes.Add(Convert.ToInt32(row["ProcessId"]));
            }
        return guests.Select(guest => new GuestOwner(guest.Name, processesByWorker[guest.WorkerPid])).ToArray();
    }

    private static bool IsVmmem(string name) =>
        string.Equals(name, "vmmem", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "vmmem.exe", StringComparison.OrdinalIgnoreCase);

    public static async Task<ClusterCpuSample> ReadCluster(CpuExpansionTarget[] targets, CancellationToken cancellation)
    {
        var selected = targets.Where(target => !target.IsWsl).Select(target => target.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool expandWsl = targets.Any(target => target.IsWsl);
        int? wslPid = expandWsl ? FindWslProcess() : null;
        var wslNames = wslPid.HasValue ? ReadRunningWslDistros(cancellation) : Task.FromResult(Array.Empty<string>());
        GuestOwner[] owners;
        try
        {
            owners = selected.Count == 0 ? Array.Empty<GuestOwner>() :
                await Task.Run(() => ReadGuestOwners(selected), cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException)
        {
            owners = Array.Empty<GuestOwner>();
        }
        var distrosToSample = await wslNames.ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var hostTask = Task.Run(() => ReadHost(cancellation), cancellation);
        var guestTasks = owners.Select(owner => ReadGuest(owner, cancellation)).ToArray();
        var wslTask = ReadWsl(distrosToSample, cancellation);
        await Task.WhenAll(guestTasks.Cast<Task>().Append(hostTask).Append(wslTask)).ConfigureAwait(false);

        var host = await hostTask.ConfigureAwait(false);
        var expanded = new List<CpuMachineProcesses>();
        var unexpanded = selected.Except(owners.Select(owner => owner.Name), StringComparer.OrdinalIgnoreCase).ToList();
        var replacedPids = new HashSet<int>();
        foreach (var task in guestTasks)
        {
            var (owner, sample) = await task.ConfigureAwait(false);
            if (sample == null)
            {
                unexpanded.Add(owner.Name);
                continue;
            }
            var wrappers = host.Rows.Where(row => IsVmmem(row.Process) && owner.AccountingPids.Contains(row.Pid)).ToArray();
            if (wrappers.Length == 0)
                continue;
            if (sample.TotalCores <= 0 || sample.Rows.Length == 0)
            {
                unexpanded.Add(owner.Name);
                continue;
            }
            double vmCores = wrappers.Sum(row => row.Cores);
            expanded.Add(Scale(owner.Name, sample, vmCores / sample.TotalCores, vmCores));
            replacedPids.UnionWith(wrappers.Select(row => row.Pid));
        }
        if (expandWsl)
        {
            var distros = await wslTask.ConfigureAwait(false);
            var wrapper = host.Rows.FirstOrDefault(row => row.Pid == wslPid &&
                string.Equals(row.Process, "vmmemWSL", StringComparison.OrdinalIgnoreCase));
            double busy = distros.Length == 0 ? 0 :
                Math.Max(distros.Max(distro => distro.Sample.TotalCores), distros.Sum(distro => distro.Sample.ProcessCores));
            if (distros.Length == 0 || (wrapper != null && (busy <= 0 || !distros.Any(distro => distro.Sample.Rows.Length > 0))))
                unexpanded.Add("WSL");
            else if (wrapper != null)
            {
                double scale = wrapper.Cores / busy;
                expanded.AddRange(distros.Where(distro => distro.Sample.Rows.Length > 0).Select(distro =>
                    Scale("WSL / " + distro.Name, distro.Sample, scale, distro.Sample.ProcessCores * scale)));
                replacedPids.Add(wrapper.Pid);
            }
        }
        return new ClusterCpuSample(host with { Rows = host.Rows.Where(row => !replacedPids.Contains(row.Pid)).ToArray() },
            expanded.ToArray(), unexpanded.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static CpuMachineProcesses Scale(string name, CpuProcessSample sample, double scale, double cores) =>
        new(name, sample.Rows.Select(row => row with { Cores = row.Cores * scale }).ToArray(), cores);

    private static int? FindWslProcess()
    {
        var processes = Process.GetProcessesByName("vmmemWSL");
        try { return processes.Length == 1 ? processes[0].Id : null; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static async Task<(string Name, CpuProcessSample Sample)[]> ReadWsl(string[] names, CancellationToken cancellation)
    {
        if (names.Length == 0)
            return Array.Empty<(string, CpuProcessSample)>();
        try
        {
            string code = await VmDiskIo.ReadScript("GuestCpu.sh", cancellation).ConfigureAwait(false);
            return await Task.WhenAll(names.Select(name => ReadWslDistro(name, code, cancellation))).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return Array.Empty<(string, CpuProcessSample)>();
        }
    }

    private static async Task<string[]> ReadRunningWslDistros(CancellationToken cancellation)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var list = await VmDiskIo.Run(Wsl, new[] { "--list", "--running", "--quiet" }, null, deadline.Token,
                Encoding.Unicode).ConfigureAwait(false);
            if (list.ExitCode != 0)
                return Array.Empty<string>();
            var running = list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim('\r', '\uFEFF')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in registry?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var distro = registry.OpenSubKey(key);
                if (distro?.GetValue("Version") is 2 && distro.GetValue("DistributionName") is string name && running.Contains(name))
                    names.Add(name);
            }
            return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<(string Name, CpuProcessSample Sample)> ReadWslDistro(
        string name, string code, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var result = await VmDiskIo.Run(Wsl, new[] { "--distribution", name, "--exec", "/bin/sh", "-s" }, code,
            deadline.Token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("WSL CPU sampling failed.");
        return (name, ParseGuest(result.Output));
    }

    internal static CpuProcessSample ParseGuest(string output)
    {
        var ticksByProcess = new List<CpuProcessRow>();
        double seconds = 0, ticksPerSecond = 0, busy = 0, processTicks = 0;
        int logicalProcessors = 0;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length == 6 && fields[0] == "sample")
            {
                seconds = double.Parse(fields[1], CultureInfo.InvariantCulture);
                ticksPerSecond = double.Parse(fields[2], CultureInfo.InvariantCulture);
                busy = double.Parse(fields[3], CultureInfo.InvariantCulture);
                processTicks = double.Parse(fields[4], CultureInfo.InvariantCulture);
                logicalProcessors = int.Parse(fields[5], CultureInfo.InvariantCulture);
            }
            else if (fields.Length == 3 && int.TryParse(fields[0], out int pid) &&
                long.TryParse(fields[1], out long ticks) && ticks > 0)
                ticksByProcess.Add(new CpuProcessRow(pid, fields[2], ticks));
        }
        if (!(seconds > 0 && ticksPerSecond > 0 && logicalProcessors > 0))
            throw new InvalidOperationException("Linux CPU sample was incomplete.");
        double divisor = seconds * ticksPerSecond;
        return new CpuProcessSample(logicalProcessors, seconds,
            ticksByProcess.OrderByDescending(row => row.Cores).ThenBy(row => row.Pid).Take(50)
                .Select(row => row with { Cores = row.Cores / divisor }).ToArray(),
            Math.Max(busy, processTicks) / divisor, processTicks / divisor);
    }

    private static async Task<(GuestOwner Owner, CpuProcessSample Sample)> ReadGuest(GuestOwner owner, CancellationToken cancellation)
    {
        if (owner.AccountingPids.Count > 0)
        {
            try
            {
                return (owner, await VmDiskIo.SampleCpu(owner.Name, cancellation).ConfigureAwait(false));
            }
            catch (Exception) when (!cancellation.IsCancellationRequested) { }
        }
        return (owner, null);
    }

    public static async Task<CpuProcessSample> ReadHost(CancellationToken cancellation)
    {
        var before = ReadHostTimes();
        long start = Stopwatch.GetTimestamp();
        await Task.Delay(1000, cancellation).ConfigureAwait(false);
        var after = ReadHostTimes();
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var rows = after.Where(entry => before.TryGetValue(entry.Key, out var previous) && entry.Value.Time > previous.Time)
            .Select(entry => new CpuProcessRow(entry.Key.Pid, entry.Value.Name,
                (entry.Value.Time - before[entry.Key].Time).TotalSeconds / seconds))
            .ToArray();
        return new CpuProcessSample(Math.Max(1, (int)GetActiveProcessorCount(ushort.MaxValue)), seconds, rows);
    }

    private static Dictionary<(int Pid, long Started), (string Name, TimeSpan Time)> ReadHostTimes()
    {
        var result = new Dictionary<(int, long), (string, TimeSpan)>();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    if (process.Id != 0)
                        result[(process.Id, process.StartTime.ToUniversalTime().Ticks)] = (process.ProcessName, process.TotalProcessorTime);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        return result;
    }
}
