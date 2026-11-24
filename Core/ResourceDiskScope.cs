#nullable enable

public static class ResourceDiskScope
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static DiskIoRow[] Rows(PerformanceWindow frame, ResourceNode node, Func<string, bool> includesGuest, bool includeAll)
    {
        if (node.Kind is ResourceKind.WindowsIo or ResourceKind.UnattributedIo)
        {
            var workload = node.Kind == ResourceKind.WindowsIo ? DiskIoWorkload.WindowsFile : DiskIoWorkload.Unattributed;
            return SelectFiles(frame, null, null, includeAll).Where(file => DiskIoBreakdown.Classify(file.File.Path) == workload)
                .Select(file => new DiskIoRow(file.File, frame.Host, file.Totals)).ToArray();
        }

        var backing = BackingPaths(frame, node, includeAll);
        var files = SelectFiles(frame, node.Disk,
            node.Kind is ResourceKind.VirtualDisk or ResourceKind.VirtualMachine ? backing : null, includeAll)
            .GroupBy(row => row.File).Select(group => new MeasuredFile(group.Key, DiskIoMerge.Sum(group.Select(row => row.Totals))))
            .ToArray();
        var scoped = frame with { DiskPaths = frame.DiskPaths with { Files = files } };
        return GuestProjection.DiskRows(scoped, includesGuest,
            backing == null ? null : guest => guest.BackingPaths.Any(backing.Contains));
    }

    public static IEnumerable<GuestDiskWindow> DeviceWindows(PerformanceWindow frame, ResourceNode node) =>
        (frame.Guests?.Disks ?? []).Where(guest => guest.Seconds > 0 && guest.Machine == node.Machine &&
            guest.Target == node.Target && guest.Device == node.Device && guest.BackingPaths.Contains(node.BackingPath, PathComparer));

    public static DiskIoBreakdown? Breakdown(PerformanceWindow frame, int? disk, bool includeAll) =>
        frame.DiskPaths.Seconds > 0
            ? DiskIoBreakdown.Measure(SelectFiles(frame, disk, null, includeAll), frame.DiskPaths.Seconds)
            : null;

    private static HashSet<string>? BackingPaths(PerformanceWindow frame, ResourceNode node, bool includeAll)
    {
        if (node.BackingPath != null) return new([node.BackingPath], PathComparer);
        if (node.Kind == ResourceKind.VirtualMachine)
            return node.Children.Select(child => child.BackingPath).OfType<string>().ToHashSet(PathComparer);
        if (frame.Resources is not { } samples) return null;
        if (node.Disk is int number)
            return samples.VirtualDisks.Where(file => file.Number == number).Select(file => file.Path).ToHashSet(PathComparer);
        var visible = VisibleNumbers(samples, includeAll);
        return visible == null ? null : samples.VirtualDisks.Where(file => visible.Contains(file.Number))
            .Select(file => file.Path).ToHashSet(PathComparer);
    }

    private static IEnumerable<MeasuredFile> SelectFiles(PerformanceWindow frame, int? disk, HashSet<string>? backing, bool includeAll)
    {
        if (frame.Resources is not { } samples)
            return disk.HasValue ? [] : backing == null ? frame.DiskPaths.Files
                : frame.DiskPaths.Files.Where(file => backing.Contains(file.File.Path));

        var visible = VisibleNumbers(samples, includeAll);
        if (disk == null && visible == null && backing == null) return frame.DiskPaths.Files;
        return samples.Files.Where(row => (disk.HasValue ? row.Number == disk : visible == null || visible.Contains(row.Number)) &&
            (backing == null || backing.Contains(row.File.Path)))
            .Select(row => new MeasuredFile(row.File, row.Totals));
    }

    private static HashSet<int>? VisibleNumbers(ResourceMeasurements samples, bool includeAll) =>
        includeAll || samples.Disks.Length == 0 ? null : ResourceTree.VisibleDisks(samples, false).Select(disk => disk.Number).ToHashSet();
}
