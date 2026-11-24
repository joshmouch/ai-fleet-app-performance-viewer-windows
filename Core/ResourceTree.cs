#nullable enable

public enum ResourceKind { System, Host, CpuGroup, Processor, Disk, WindowsIo, UnattributedIo, VirtualMachine, GuestCpuDistribution, VirtualDisk, GuestDevice }
public enum DiskGrouping { Storage, Workloads, Vhdx }

public sealed record ResourceScope(string? Host = null, string? Target = null, string? Machine = null)
{
    public string? Name => Machine ?? Target ?? Host;
}

public readonly record struct ResourceReading(double? Active = null, double? Read = null, double? Write = null,
    double? Windows = null, double? Guests = null, double? Hypervisor = null, double? Vmmem = null);

public sealed record ResourceNode(string Id, string Name, string? Type, ResourceKind Kind, ResourceReading Reading,
    ResourceNode[] Children, string? Host = null, int? Disk = null, string? BackingPath = null,
    string? Target = null, string? Machine = null, string? Device = null, double? HostShare = null,
    DateTimeOffset? MeasuredAt = null);

public static class ResourceTree
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static ResourceNode Build(bool disk, IEnumerable<PerformanceWindow> frames,
        Func<string, string, bool> includesGuest, bool includeAllDisks, DiskGrouping diskGrouping = DiskGrouping.Storage,
        bool showLogicalProcessors = true)
    {
        var sources = frames.OrderBy(w => w.Host, PathComparer).ToArray();
        var hosts = sources.Select(w => disk
            ? DiskHost(w, includesGuest, includeAllDisks, diskGrouping)
            : CpuHost(w, includesGuest, showLogicalProcessors)).ToArray();
        var capacity = sources.Select(frame => frame.Cpu.LogicalProcessors).ToArray();
        var reading = disk ? DiskTotal(hosts) : new ResourceReading(
            CapacityAverage(hosts, capacity, reading => reading.Active),
            Windows: CapacityAverage(hosts, capacity, reading => reading.Windows),
            Guests: CapacityAverage(hosts, capacity, reading => reading.Guests),
            Hypervisor: CapacityAverage(hosts, capacity, reading => reading.Hypervisor));
        return new("system", hosts.Length > 1 ? "All hosts" : "This system", null, ResourceKind.System, reading,
            hosts.Length == 1 ? hosts[0].Children : hosts, Host: hosts.Length == 1 ? hosts[0].Host : null);
    }

    public static IEnumerable<ResourceNode> Walk(ResourceNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Walk(child))
                yield return descendant;
    }

    public static ResourceScope? SharedScope(ResourceNode node) => node.Kind switch
    {
        ResourceKind.System => new(),
        ResourceKind.Host => new(node.Host),
        ResourceKind.VirtualMachine or ResourceKind.GuestCpuDistribution when node.Target is string target =>
            new(node.Host, target, PathComparer.Equals(target, node.Machine) ? null : node.Machine),
        _ => null
    };

    public static ResourceNode ResolveScope(ResourceNode root, ResourceScope scope, bool disk)
    {
        if (scope.Host == null) return root;
        var host = root.Children.FirstOrDefault(node => node.Kind == ResourceKind.Host && PathComparer.Equals(node.Host, scope.Host));
        var hostRoot = host ?? root;
        if (scope.Target == null)
            return host ?? new((disk ? "disk/" : "cpu/") + scope.Host, scope.Host, null, ResourceKind.Host,
                PathComparer.Equals(root.Host, scope.Host) ? root.Reading : default,
                PathComparer.Equals(root.Host, scope.Host) ? root.Children : [], scope.Host);
        var guest = Walk(hostRoot).FirstOrDefault(node => (!disk || node.Disk == null) && SharedScope(node) is { } candidate &&
            PathComparer.Equals(candidate.Host, scope.Host) && PathComparer.Equals(candidate.Target, scope.Target) &&
            PathComparer.Equals(candidate.Machine, scope.Machine));
        if (guest != null) return guest;

        var files = disk ? Walk(hostRoot).Where(node => node.Kind == ResourceKind.VirtualDisk &&
            PathComparer.Equals(node.Host, scope.Host) && PathComparer.Equals(node.Target ?? node.Machine, scope.Target) &&
            (scope.Machine == null || PathComparer.Equals(node.Machine, scope.Machine))).ToArray() : [];
        var id = (disk ? "disk-scope/" : "cpu-scope/") + scope.Host + "/" + scope.Target +
            (scope.Machine == null ? "" : "/" + scope.Machine);
        return new(id, scope.Name!, null, ResourceKind.VirtualMachine, files.Length > 0 ? DiskTotal(files) : default, files,
            scope.Host, Target: scope.Target, Machine: scope.Machine, HostShare: Sum(files.Select(file => file.HostShare)));
    }

    private static ResourceNode CpuHost(PerformanceWindow frame, Func<string, string, bool> includesGuest, bool showLogicalProcessors)
    {
        string host = frame.Host, id = "cpu/" + host;
        var counters = frame.Resources?.Cpu;
        double? busy = counters?.Busy ?? (frame.Cpu.LogicalProcessors > 0
            ? frame.Cpu.TotalCores * 100 / frame.Cpu.LogicalProcessors : null);
        var reading = new ResourceReading(busy, Windows: counters?.Windows, Guests: counters?.Guests,
            Hypervisor: counters?.Hypervisor);
        var children = new List<ResourceNode>();
        if (showLogicalProcessors && counters?.Processors is { Length: > 0 } processors)
            children.Add(new(id + "/logical", "Logical processors", "Windows host", ResourceKind.CpuGroup,
                new(busy), processors.Select(p => new ResourceNode(id + "/processor/" + p.Id,
                    "CPU " + p.Id, null, ResourceKind.Processor, new(p.Busy), [], host,
                    HostShare: Share(p.Busy, 100.0 * frame.Cpu.LogicalProcessors))).ToArray(), host,
                HostShare: Share(busy, 100)));
        if (frame.Guests?.Cpu is { Sample: { } cluster } guestSample)
        {
            var processesByTarget = cluster.Guests.ToLookup(guest => guest.Target ?? guest.Name, PathComparer);
            var guestNodes = cluster.Coverage
                .Where(coverage => includesGuest(host, coverage.Machine))
                .Select(coverage =>
                {
                    string target = coverage.Machine;
                    string guestId = id + "/guest/" + target;
                    bool wsl = PathComparer.Equals(target, "WSL");
                    // Per-VM accounting measures the whole VM independently of how many guest PIDs were sampled.
                    var measured = GuestProjection.HostPercent(cluster.Host, coverage.HostCores);
                    var vmmem = coverage.ReplacedRows is { Length: > 0 } wrappers
                        ? GuestProjection.HostPercent(cluster.Host, wrappers.Sum(row => row.Cores)) : null;
                    var distributions = wsl ? processesByTarget[target].Select(guest =>
                    {
                        var attributed = GuestProjection.HostPercent(cluster.Host, Sum(guest.Rows.Select(row => row.AttributedCores)));
                        return new ResourceNode(guestId + "/" + guest.Name, guest.Name, "WSL distribution", ResourceKind.GuestCpuDistribution,
                            new(attributed), [], host, Target: target, Machine: guest.Name, HostShare: Share(attributed, 100),
                            MeasuredAt: guestSample.End);
                    }).ToArray() : [];
                    return new ResourceNode(guestId, target, wsl ? "Shared WSL VM" : "Hyper-V virtual machine", ResourceKind.VirtualMachine,
                        new(measured, Vmmem: vmmem), distributions, host, Target: target, HostShare: Share(measured, 100),
                        MeasuredAt: guestSample.End);
                }).OrderBy(node => node.Name, PathComparer).ToArray();
            if (guestNodes.Length > 0)
            {
                var guestCpu = Sum(guestNodes.Select(n => n.Reading.Active));
                children.Add(new(id + "/guests", "Guests", "Selected guests", ResourceKind.CpuGroup,
                    new(guestCpu), guestNodes, host, HostShare: Share(guestCpu, 100), MeasuredAt: guestSample.End));
            }
        }
        return new(id, host, "Windows host", ResourceKind.Host, reading, children.ToArray(), host,
            HostShare: Share(busy, 100));
    }

    private static ResourceNode DiskHost(PerformanceWindow frame, Func<string, string, bool> includesGuest,
        bool includeAll, DiskGrouping grouping)
    {
        string id = "disk/" + frame.Host;
        var sample = frame.Resources;
        if (sample is null || sample.Disks.Length == 0)
        {
            var bytes = frame.DiskPaths.Totals();
            return new(id, frame.Host, null, ResourceKind.Host, Rate(bytes, frame.DiskPaths.Seconds), [], frame.Host,
                HostShare: Share(bytes.TotalBytes, bytes.TotalBytes));
        }

        var visible = VisibleDisks(sample, includeAll);
        var files = sample.Files.ToLookup(row => row.Number);
        var diskBytes = visible.ToDictionary(disk => disk.Number, disk => files[disk.Number].Sum(row => row.Totals.TotalBytes));
        // Measure every share against the same Windows trace window, including unattributed traffic.
        var hostBytes = frame.DiskPaths.Seconds > 0 ? diskBytes.Values.Sum() : 0;
        var virtualFiles = sample.VirtualDisks.ToLookup(file => file.Number);
        var guestWindows = (frame.Guests?.Disks ?? [])
            .SelectMany(window => window.BackingPaths.Select(path => (Path: path, Window: window)))
            .ToLookup(pair => pair.Path, pair => pair.Window, PathComparer);
        var disks = visible.OrderBy(disk => disk.Mounts.FirstOrDefault() ?? disk.Name, PathComparer).Select(disk =>
        {
            string diskId = id + "/" + disk.Number;
            var virtualDisks = virtualFiles[disk.Number].DistinctBy(file => file.Path, PathComparer);
            var perPath = files[disk.Number].Where(row => row.File.Path.Length > 0).ToLookup(row => row.File.Path, PathComparer);
            ResourceNode Vhdx(VirtualDiskMeasurement file)
            {
                string fileId = diskId + "/" + file.Path.ToUpperInvariant();
                var backingBytes = DiskIoMerge.Sum(perPath[file.Path].Select(row => row.Totals));
                var backingShare = Share(backingBytes.TotalBytes, hostBytes);
                var guestDevices = guestWindows[file.Path].Where(guest => !string.IsNullOrEmpty(guest.Device))
                    .DistinctBy(guest => (guest.Machine, guest.Target, guest.Device)).ToArray();
                // A single matched device can use the backing file's measured impact. A shared backing
                // file has no per-device Windows measurement, so the guest rates do not drive the tint.
                var deviceShare = guestDevices.Length == 1 ? backingShare : null;
                var devices = guestDevices.Where(guest => includesGuest(frame.Host, guest.Target)).Select(guest =>
                    new ResourceNode(fileId + "/device/" + guest.Target + "/" + guest.Machine + "/" + guest.Device,
                        guest.Device, "Guest device", ResourceKind.GuestDevice,
                        Rate(DiskIoMerge.Sum(guest.Files.Select(row => row.Totals)), guest.Seconds), [],
                        frame.Host, disk.Number, file.Path, guest.Target, guest.Machine, guest.Device, deviceShare)).ToArray();
                return new(fileId, FileName(file.Path), null, ResourceKind.VirtualDisk, Rate(backingBytes, frame.DiskPaths.Seconds),
                    devices, frame.Host, disk.Number, file.Path, file.Target, file.Machine, HostShare: backingShare);
            }

            return new ResourceNode(diskId, disk.Mounts.FirstOrDefault() ?? disk.Name, disk.Kind, ResourceKind.Disk,
                new(disk.Active, disk.ReadMiB, disk.WriteMiB), virtualDisks.Select(Vhdx).ToArray(), frame.Host, disk.Number,
                HostShare: Share(diskBytes[disk.Number], hostBytes));
        }).ToArray();
        var vhdx = disks.SelectMany(disk => disk.Children).ToArray();
        ResourceNode[] children = grouping switch
        {
            DiskGrouping.Workloads => [..WindowsWorkloads(), ..GroupByMachine(vhdx, frame.Host, id + "/workloads", null)],
            DiskGrouping.Vhdx => vhdx.Select(file => file with { Type = file.Machine }).ToArray(),
            _ => disks.Select(disk => disk with
            {
                Children = GroupByMachine(disk.Children, frame.Host, disk.Id, disk.Disk)
            }).ToArray()
        };
        return new(id, frame.Host, null, ResourceKind.Host, DiskTotal(disks), children, frame.Host,
            HostShare: Share(hostBytes, hostBytes));

        IEnumerable<ResourceNode> WindowsWorkloads()
        {
            if (ResourceDiskScope.Breakdown(frame, null, includeAll) is not { } captured) yield break;
            var hostRate = hostBytes / (1048576 * frame.DiskPaths.Seconds);
            yield return new(id + "/windows", "Windows files", "Host file I/O", ResourceKind.WindowsIo,
                new(Read: captured.WindowsFiles.Read, Write: captured.WindowsFiles.Write),
                [], frame.Host, HostShare: Share(captured.WindowsFiles.Total, hostRate));
            yield return new(id + "/unattributed", "Unattributed I/O", null, ResourceKind.UnattributedIo,
                new(Read: captured.Unattributed.Read, Write: captured.Unattributed.Write),
                [], frame.Host, HostShare: Share(captured.Unattributed.Total, hostRate));
        }
    }

    internal static DiskResourceMeasurement[] VisibleDisks(ResourceMeasurements samples, bool includeAll)
    {
        if (includeAll) return samples.Disks;
        var mounted = samples.Disks.Where(disk => disk.StorageSpaces || disk.Mounts.Length > 0).ToArray();
        return mounted.Length == 0 ? samples.Disks : mounted;
    }

    private static ResourceNode[] GroupByMachine(ResourceNode[] disks, string host, string parentId, int? number)
    {
        return disks.Where(disk => disk.Machine != null).GroupBy(disk => disk.Machine!, PathComparer)
            .Select(machine =>
            {
                var files = machine.ToArray();
                return new ResourceNode(parentId + "/vm/" + machine.Key.ToUpperInvariant(), machine.Key,
                    machine.All(file => file.Target == "WSL") ? "WSL distribution" : "Hyper-V virtual machine",
                    ResourceKind.VirtualMachine, DiskTotal(files), files, host, number,
                    Target: files.Select(file => file.Target).OfType<string>().FirstOrDefault() ?? machine.Key, Machine: machine.Key,
                    HostShare: Sum(files.Select(file => file.HostShare)));
            }).Concat(disks.Where(disk => disk.Machine == null)).ToArray();
    }

    private static string FileName(string path) => path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];

    private static double? Share(double? amount, double total) =>
        amount is double measured && double.IsFinite(measured) && total > 0 ? Math.Clamp(measured / total, 0, 1) : null;

    public static ResourceReading Rate(FileIoTotals totals, double seconds) => seconds <= 0
        ? default : new(Read: totals.ReadBytes / (1048576 * seconds), Write: totals.WriteBytes / (1048576 * seconds));

    private static ResourceReading DiskTotal(ResourceNode[] nodes) =>
        new(nodes.Select(node => node.Reading.Active).Max(), Sum(nodes.Select(node => node.Reading.Read)),
            Sum(nodes.Select(node => node.Reading.Write)));

    private static double? Sum(IEnumerable<double?> values)
    {
        var known = values.OfType<double>().ToArray();
        return known.Length > 0 ? known.Sum() : null;
    }

    private static double? CapacityAverage(ResourceNode[] nodes, int[] capacity, Func<ResourceReading, double?> value)
    {
        double weighted = 0, total = 0;
        for (int i = 0; i < nodes.Length; i++)
            if (value(nodes[i].Reading) is double reading && capacity[i] > 0)
            {
                weighted += reading * capacity[i];
                total += capacity[i];
            }
        return total == 0 ? null : weighted / total;
    }
}
