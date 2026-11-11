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

internal sealed record CpuProcessRow(int Pid, string Process, double Cores, double? GuestCpuShare = null,
    double? AttributedCores = null, long Started = 0);
internal sealed record CpuProcessSample(int LogicalProcessors, double Seconds, CpuProcessRow[] Rows,
    double TotalCores = 0, double ProcessCores = 0);
internal sealed record CpuMachineProcesses(string Name, CpuProcessRow[] Rows);
internal sealed record GuestCpuCoverage(string Machine, double? HostCores, double AttributedCores, bool SampleAvailable);
internal sealed record ClusterCpuSample(CpuProcessSample Host, CpuMachineProcesses[] Guests, GuestCpuCoverage[] Coverage);
internal sealed record CpuExpansionTarget(string Name, bool IsWsl = false, string CounterName = null)
{
    public string CounterInstance => IsWsl ? CounterName : Name;
    public string CounterPrefix => CounterInstance == null ? null : CounterInstance + ":hv vp ";
    public override string ToString() => Name;
}

internal static class CpuProcessSampler
{
    private sealed record Guest(string Name, int WorkerPid);
    private sealed record GuestOwner(string Name, HashSet<int> AccountingPids);
    internal static readonly string Wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");

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
        if (FindWslProcess() is int wslPid)
            targets.Add(new CpuExpansionTarget("WSL", IsWsl: true, CounterName: ReadWslCounterName(wslPid)));
        return targets.ToArray();
    }

    private static string ReadWslCounterName(int wslPid)
    {
        try
        {
            using var wsl = new ManagementObject($"Win32_Process.Handle='{wslPid}'");
            using var worker = new ManagementObject($"Win32_Process.Handle='{wsl["ParentProcessId"]}'");
            if (worker["CommandLine"] is string command)
                foreach (string argument in command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (Guid.TryParse(argument.Trim('"'), out Guid vm))
                        return vm.ToString();
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException) { }
        return null;
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
        var wslNames = wslPid.HasValue ? ReadRunningWslDistros(cancellation) : Task.FromResult(Array.Empty<WslDistro>());
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
        var hypervisorTask = Task.Run(() => ReadGuestBudgets(targets, cancellation), cancellation);
        var guestTasks = targets.Where(target => !target.IsWsl)
            .Select(async target => (Target: target, Sample: await ReadGuest(target.Name, cancellation).ConfigureAwait(false)))
            .ToArray();
        var wslTask = ReadWsl(distrosToSample.Select(distro => distro.Name).ToArray(), cancellation);
        await Task.WhenAll(guestTasks.Cast<Task>().Append(hostTask).Append(wslTask).Append(hypervisorTask)).ConfigureAwait(false);

        var host = await hostTask.ConfigureAwait(false);
        var budgets = await hypervisorTask.ConfigureAwait(false);
        var expanded = new List<CpuMachineProcesses>();
        var coverage = new List<GuestCpuCoverage>();
        var replacedPids = new HashSet<int>();
        foreach (var task in guestTasks)
        {
            var (target, sample) = await task.ConfigureAwait(false);
            var owner = owners.FirstOrDefault(owner => owner.Name == target.Name);
            var wrappers = host.Rows.Where(row => owner?.AccountingPids.Contains(row.Pid) == true).ToArray();
            double? budget = budgets.GetValueOrDefault(target) ?? (wrappers.Length > 0 ? wrappers.Sum(row => row.Cores) : null);
            var rows = sample == null ? Array.Empty<CpuProcessRow>() : Scale(sample, sample.TotalCores, budget);
            coverage.Add(new GuestCpuCoverage(target.Name, budget, AssignedCores(rows), sample != null));
            if (rows.Length > 0)
                expanded.Add(new CpuMachineProcesses(target.Name, rows));
            if (budget.HasValue)
                replacedPids.UnionWith(wrappers.Select(row => row.Pid));
        }
        if (targets.FirstOrDefault(target => target.IsWsl) is { } wsl)
        {
            var distros = await wslTask.ConfigureAwait(false);
            var wrapper = host.Rows.FirstOrDefault(row => row.Pid == wslPid);
            double? budget = budgets.GetValueOrDefault(wsl) ?? wrapper?.Cores;
            var distroRows = Array.Empty<CpuMachineProcesses>();
            if (distros.Length > 0)
            {
                double busy = Math.Max(distros.Max(distro => distro.Sample.TotalCores),
                    distros.Sum(distro => distro.Sample.ProcessCores));
                distroRows = distros.Select(distro => new CpuMachineProcesses(
                    "WSL / " + distro.Name, Scale(distro.Sample, busy, budget))).ToArray();
                expanded.AddRange(distroRows);
            }
            coverage.Add(new GuestCpuCoverage(wsl.Name, budget,
                AssignedCores(distroRows.SelectMany(distro => distro.Rows)), distros.Length > 0));
            if (budget.HasValue && wrapper != null)
                replacedPids.Add(wrapper.Pid);
        }
        return new ClusterCpuSample(host with { Rows = host.Rows.Where(row => !replacedPids.Contains(row.Pid)).ToArray() },
            expanded.ToArray(), coverage.ToArray());
    }

    private static async Task<Dictionary<CpuExpansionTarget, double?>> ReadGuestBudgets(
        CpuExpansionTarget[] targets, CancellationToken cancellation)
    {
        var result = new Dictionary<CpuExpansionTarget, double?>();
        if (!targets.Any(target => target.CounterPrefix != null))
            return result;
        try
        {
            using var query = new PdhQuery(@"\Hyper-V Hypervisor Virtual Processor(*)\% Guest Run Time");
            await Task.Delay(1000, cancellation).ConfigureAwait(false);
            query.Collect();
            var values = query.Read(0);
            foreach (var target in targets.Where(target => target.CounterPrefix != null))
            {
                var processors = values.Where(pair => pair.Key.StartsWith(target.CounterPrefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(pair.Key.AsSpan(target.CounterPrefix.Length), out _)).ToArray();
                if (processors.Length > 0)
                    result.Add(target, processors.Sum(pair => Math.Clamp(pair.Value, 0, 100)) / 100);
            }
        }
        catch (InvalidOperationException) { }
        return result;
    }

    private static CpuProcessRow[] Scale(CpuProcessSample sample, double busy, double? budget) =>
        sample.Rows.Select(row => row with
        {
            GuestCpuShare = sample.TotalCores > 0 ? row.Cores / sample.TotalCores : null,
            AttributedCores = busy > 0 ? budget * (row.Cores / busy) : null
        }).ToArray();

    private static double AssignedCores(IEnumerable<CpuProcessRow> rows) =>
        rows.Sum(row => row.AttributedCores ?? 0);

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
            var samples = await Task.WhenAll(names.Select(name => ReadWslDistro(name, code, cancellation))).ConfigureAwait(false);
            return samples.Where(sample => sample.Sample != null).ToArray();
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return Array.Empty<(string, CpuProcessSample)>();
        }
    }

    internal sealed record WslDistro(string Name, string Vhdx);

    internal static WslDistro[] ReadWslDistros()
    {
        try
        {
            using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            var distros = new List<WslDistro>();
            foreach (string key in registry?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var distro = registry.OpenSubKey(key);
                if (distro?.GetValue("Version") is 2 && distro.GetValue("DistributionName") is string name)
                {
                    string basePath = distro.GetValue("BasePath") as string;
                    string file = distro.GetValue("VhdFileName") as string ?? "ext4.vhdx";
                    distros.Add(new WslDistro(name, string.IsNullOrWhiteSpace(basePath) ? null : Path.Combine(basePath, file)));
                }
            }
            return distros.OrderBy(distro => distro.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return Array.Empty<WslDistro>();
        }
    }

    internal static async Task<HashSet<string>> ReadRunningWslNames(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var list = await VmDiskIo.Run(Wsl, new[] { "--list", "--running", "--quiet" }, null, deadline.Token,
            Encoding.Unicode).ConfigureAwait(false);
        if (list.ExitCode != 0)
        {
            const string disabled = "ERROR_SERVICE_DISABLED";
            if (list.Output.Contains(disabled, StringComparison.Ordinal) ||
                list.Error.Replace("\0", "").Contains(disabled, StringComparison.Ordinal))
                throw new InvalidOperationException("The Windows WSL service is disabled.");
            throw new InvalidOperationException("Windows could not list the running WSL distributions.");
        }
        return list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim('\r', '\uFEFF')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<WslDistro[]> ReadRunningWslDistros(CancellationToken cancellation)
    {
        try
        {
            var running = await ReadRunningWslNames(cancellation).ConfigureAwait(false);
            return ReadWslDistros().Where(distro => running.Contains(distro.Name)).ToArray();
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return Array.Empty<WslDistro>();
        }
    }

    private static async Task<(string Name, CpuProcessSample Sample)> ReadWslDistro(
        string name, string code, CancellationToken cancellation)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var result = await VmDiskIo.Run(Wsl, new[] { "--distribution", name, "--exec", "/bin/sh", "-s" }, code,
                deadline.Token).ConfigureAwait(false);
            return (name, result.ExitCode == 0 ? ParseGuest(result.Output) : null);
        }
        catch (Exception) when (!cancellation.IsCancellationRequested) { return (name, null); }
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
            else if (fields.Length == 4 && int.TryParse(fields[0], out int pid) &&
                long.TryParse(fields[1], out long ticks) && ticks > 0 && long.TryParse(fields[2], out long started))
                ticksByProcess.Add(new CpuProcessRow(pid, fields[3], ticks, Started: started));
        }
        if (!(seconds > 0 && ticksPerSecond > 0 && logicalProcessors > 0))
            throw new InvalidOperationException("Linux CPU sample was incomplete.");
        double divisor = seconds * ticksPerSecond;
        return new CpuProcessSample(logicalProcessors, seconds,
            ticksByProcess.OrderByDescending(row => row.Cores).ThenBy(row => row.Pid)
                .Select(row => row with { Cores = row.Cores / divisor }).ToArray(),
            Math.Max(busy, processTicks) / divisor, processTicks / divisor);
    }

    private static async Task<CpuProcessSample> ReadGuest(string name, CancellationToken cancellation)
    {
        try { return await VmDiskIo.SampleCpu(name, cancellation).ConfigureAwait(false); }
        catch (Exception) when (!cancellation.IsCancellationRequested) { return null; }
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
                (entry.Value.Time - before[entry.Key].Time).TotalSeconds / seconds, Started: entry.Key.Started))
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
