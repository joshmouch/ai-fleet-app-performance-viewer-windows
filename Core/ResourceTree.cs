#nullable enable

public enum ResourceKind { System, Host, CpuGroup, Processor, Disk, WindowsIo, VirtualMachine, VirtualDisk, GuestDevice }
public enum DiskGrouping { Storage, Workloads, Vhdx }

public readonly record struct ResourceReading(double? Active = null, double? Read = null, double? Write = null,
    double? Windows = null, double? Guests = null, double? Hypervisor = null);

public sealed record ResourceNode(string Id, string Name, string? Type, ResourceKind Kind, ResourceReading Reading,
    ResourceNode[] Children, string? Host = null, int? Disk = null, string? BackingPath = null,
    string? Target = null, string? Machine = null, string? Device = null);

public static class ResourceTree
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static ResourceNode Build(bool disk, IEnumerable<PerformanceWindow> frames,
        Func<string, string, bool> includesGuest, bool includeAllDisks, DiskGrouping diskGrouping = DiskGrouping.Storage)
    {
        var sources = frames.OrderBy(w => w.Host, PathComparer).ToArray();
        var hosts = sources.Select(w => disk
            ? DiskHost(w, includesGuest, includeAllDisks, diskGrouping)
            : CpuHost(w, includesGuest)).ToArray();
        var capacity = sources.Select(frame => frame.Cpu.LogicalProcessors).ToArray();
        var reading = disk ? DiskTotal(hosts) : new ResourceReading(
            CapacityAverage(hosts, capacity, reading => reading.Active),
            Windows: CapacityAverage(hosts, capacity, reading => reading.Windows),
            Guests: CapacityAverage(hosts, capacity, reading => reading.Guests),
            Hypervisor: CapacityAverage(hosts, capacity, reading => reading.Hypervisor));
        return new("system", hosts.Length > 1 ? "All hosts" : "This system", null, ResourceKind.System, reading,
            hosts.Length == 1 ? hosts[0].Children : hosts);
    }

    public static IEnumerable<ResourceNode> Walk(ResourceNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Walk(child))
                yield return descendant;
    }

    private static ResourceNode CpuHost(PerformanceWindow frame, Func<string, string, bool> includesGuest)
    {
        string host = frame.Host, id = "cpu/" + host;
        var counters = frame.Resources?.Cpu;
        double? busy = counters?.Busy ?? (frame.Cpu.LogicalProcessors > 0
            ? frame.Cpu.TotalCores * 100 / frame.Cpu.LogicalProcessors : null);
        var reading = new ResourceReading(busy, Windows: counters?.Windows, Guests: counters?.Guests,
            Hypervisor: counters?.Hypervisor);
        var children = new List<ResourceNode>();
        if (counters?.Processors is { Length: > 0 } processors)
            children.Add(new(id + "/logical", "Logical processors", "Windows host", ResourceKind.CpuGroup,
                new(busy), processors.Select(p => new ResourceNode(id + "/processor/" + p.Id,
                    "CPU " + p.Id, null, ResourceKind.Processor, new(p.Busy), [], host)).ToArray(), host));
        var cluster = frame.Guests?.Cpu?.Sample;
        if (cluster != null)
        {
            var guestNodes = cluster.Guests
                .Where(guest => includesGuest(host, guest.Target ?? guest.Name))
                .Select(guest =>
                {
                    string target = guest.Target ?? guest.Name;
                    double? impact = cluster.Host.LogicalProcessors <= 0 ? null :
                        guest.Rows.Sum(row => row.AttributedCores ?? 0) * 100 / cluster.Host.LogicalProcessors;
                    return new ResourceNode(id + "/guest/" + guest.Name, guest.Name, "Guest", ResourceKind.VirtualMachine,
                        new(impact), [], host, Target: target, Machine: guest.Name);
                }).OrderBy(node => node.Name, PathComparer).ToArray();
            if (guestNodes.Length > 0)
                children.Add(new(id + "/guests", "Guests", "Host-equivalent CPU", ResourceKind.CpuGroup,
                    new(counters?.Guests ?? Sum(guestNodes.Select(n => n.Reading.Active))), guestNodes, host));
        }
        return new(id, host, "Windows host", ResourceKind.Host, reading, children.ToArray(), host);
    }

    private static ResourceNode DiskHost(PerformanceWindow frame, Func<string, string, bool> includesGuest,
        bool includeAll, DiskGrouping grouping)
    {
        string id = "disk/" + frame.Host;
        var sample = frame.Resources;
        if (sample is null || sample.Disks.Length == 0)
        {
            var bytes = frame.DiskPaths.Totals();
            return new(id, frame.Host, null, ResourceKind.Host, Rate(bytes, frame.DiskPaths.Seconds), [], frame.Host);
        }

        var visible = VisibleDisks(sample, includeAll);
        var files = sample.Files.ToLookup(row => row.Number);
        var virtualFiles = sample.VirtualDisks.ToLookup(file => file.Number);
        var guestWindows = (frame.Guests?.Disks ?? []).Where(window => includesGuest(frame.Host, window.Target))
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
                var devices = guestWindows[file.Path].Where(guest => !string.IsNullOrEmpty(guest.Device))
                    .DistinctBy(guest => (guest.Machine, guest.Target, guest.Device))
                    .Select(guest => new ResourceNode(fileId + "/device/" + guest.Target + "/" + guest.Machine + "/" + guest.Device,
                        guest.Device, "Guest device", ResourceKind.GuestDevice,
                        Rate(DiskIoMerge.Sum(guest.Files.Select(row => row.Totals)), guest.Seconds), [],
                        frame.Host, disk.Number, file.Path, guest.Target, guest.Machine, guest.Device)).ToArray();
                var fileReading = Rate(DiskIoMerge.Sum(perPath[file.Path].Select(row => row.Totals)), frame.DiskPaths.Seconds);
                return new(fileId, FileName(file.Path), null, ResourceKind.VirtualDisk, fileReading, devices,
                    frame.Host, disk.Number, file.Path, file.Target, file.Machine);
            }

            return new ResourceNode(diskId, disk.Mounts.FirstOrDefault() ?? disk.Name, disk.Kind, ResourceKind.Disk,
                new(disk.Active, disk.ReadMiB, disk.WriteMiB), virtualDisks.Select(Vhdx).ToArray(), frame.Host, disk.Number);
        }).ToArray();
        var vhdx = disks.SelectMany(disk => disk.Children).ToArray();
        ResourceNode[] children = grouping switch
        {
            DiskGrouping.Workloads =>
            [
                new(id + "/windows", "Windows", "Host file I/O", ResourceKind.WindowsIo,
                    Rate(DiskIoMerge.Sum(ResourceDiskScope.NativeWindowsFiles(frame, includeAll).Select(row => row.Totals)),
                        frame.DiskPaths.Seconds), [], frame.Host),
                ..GroupByMachine(vhdx, frame.Host, id + "/workloads", null)
            ],
            DiskGrouping.Vhdx => vhdx.Select(file => file with { Type = file.Machine }).ToArray(),
            _ => disks.Select(disk => disk with
            {
                Children = GroupByMachine(disk.Children, frame.Host, disk.Id, disk.Disk)
            }).ToArray()
        };
        return new(id, frame.Host, null, ResourceKind.Host, DiskTotal(disks), children, frame.Host);
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
                    ResourceKind.VirtualMachine, DiskTotal(files), files, host, number, Machine: machine.Key);
            }).Concat(disks.Where(disk => disk.Machine == null)).ToArray();
    }

    private static string FileName(string path) => path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];

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
