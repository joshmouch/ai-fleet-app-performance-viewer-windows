public enum DiskIoWorkload { WindowsFile, BackingFile, Unattributed }

public readonly record struct DiskIoBreakdown(DiskGuestRate WindowsFiles, DiskGuestRate BackingFiles, DiskGuestRate Unattributed)
{
    public DiskGuestRate Total => WindowsFiles + BackingFiles + Unattributed;

    public static DiskIoWorkload Classify(string path) => string.IsNullOrEmpty(path) ? DiskIoWorkload.Unattributed
        : FileIoKey.IsVirtualDisk(path) ? DiskIoWorkload.BackingFile : DiskIoWorkload.WindowsFile;

    public static DiskIoBreakdown Measure(IEnumerable<MeasuredFile> files, double seconds)
    {
        FileIoTotals windows = default, backing = default, unattributed = default;
        foreach (var file in files)
        {
            switch (Classify(file.File.Path))
            {
                case DiskIoWorkload.WindowsFile: windows.Add(file.Totals); break;
                case DiskIoWorkload.BackingFile: backing.Add(file.Totals); break;
                case DiskIoWorkload.Unattributed: unattributed.Add(file.Totals); break;
            }
        }
        return new(DiskGuestRate.From(windows, seconds), DiskGuestRate.From(backing, seconds), DiskGuestRate.From(unattributed, seconds));
    }

    public static DiskIoBreakdown operator +(DiskIoBreakdown left, DiskIoBreakdown right) =>
        new(left.WindowsFiles + right.WindowsFiles, left.BackingFiles + right.BackingFiles, left.Unattributed + right.Unattributed);
}
