public sealed record GuestCpuWindow(DateTimeOffset End, ClusterCpuSample Sample);
public sealed record GuestDiskWindow(string Machine, string Target, string Device, string[] BackingPaths,
    double Seconds, MeasuredFile[] Files, bool Limited, string Status);
public sealed record GuestWindow(GuestCpuWindow Cpu, GuestDiskWindow[] Disks, string DiskInventoryStatus, string CpuStatus = null);
public sealed record RankedCpuProcess(string Machine, CpuProcessRow Row, double? HostPercent);

public static class GuestProjection
{
    public static IEnumerable<RankedCpuProcess> CpuRows(PerformanceWindow frame, Func<string, bool> selected)
    {
        var cluster = frame.Guests?.Cpu?.Sample;
        bool Expand(GuestCpuCoverage g) => selected(g.Machine) && g.SampleAvailable && g.AttributedCores > 0;
        if (cluster == null || !cluster.Coverage.Any(Expand))
        {
            foreach (var row in frame.Cpu.Rows)
                yield return new(frame.Host, row, row.Cores * 100 / frame.Cpu.LogicalProcessors);
            yield break;
        }

        // Keep wrappers and guest shares within the same host measurement window.
        var host = cluster.Host;
        foreach (var row in host.Rows)
            yield return new(frame.Host, row, row.Cores * 100 / host.LogicalProcessors);
        foreach (var coverage in cluster.Coverage)
        {
            if (!Expand(coverage))
            {
                foreach (var row in coverage.ReplacedRows ?? [])
                    yield return new(frame.Host, row, row.Cores * 100 / host.LogicalProcessors);
                continue;
            }
            foreach (var guest in cluster.Guests.Where(g => (g.Target ?? g.Name) == coverage.Machine))
                foreach (var row in guest.Rows)
                    yield return new(guest.Name, row, row.AttributedCores * 100 / host.LogicalProcessors);
        }
    }

    public static DiskIoRow[] DiskRows(PerformanceWindow frame, Func<string, bool> selected,
        Func<GuestDiskWindow, bool> inScope = null)
    {
        var disks = (frame.Guests?.Disks ?? []).Where(d => selected(d.Target) && (inScope?.Invoke(d) ?? true) &&
            d.Seconds > 0 && d.Files.Length > 0).ToArray();
        if (disks.Length == 0)
            return frame.DiskPaths.Files.Select(f => new DiskIoRow(f.File, frame.Host, f.Totals)).ToArray();
        var entries = frame.DiskPaths.Files.ToDictionary(f => f.File, f => f.Totals);
        var backing = entries.ToLookup(pair => pair.Key.Path, StringComparer.OrdinalIgnoreCase);
        var expansions = disks
            .Select(d => new DiskIoExpansion(d.Machine, d.BackingPaths.SelectMany(path => backing[path]).ToArray(),
                d.Files.ToDictionary(f => f.File, f => f.Totals), d.Seconds)).ToArray();
        return DiskIoMerge.Expand(frame.Host, entries, expansions, true);
    }
}
