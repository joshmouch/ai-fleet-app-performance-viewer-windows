namespace PerformanceViewer.Components;

public sealed class ViewerPreferences
{
    public bool ShowCpu { get; set; } = true;
    public bool ShowDisk { get; set; }
    public bool ShowLogicalProcessors { get; set; }
    public DiskGrouping Grouping { get; set; } = DiskGrouping.Storage;
    public string Host { get; set; } = "";
    public bool Guests { get; set; } = true;
    public HashSet<Guest> HiddenGuests { get; set; } = [];
    public bool AllDisks { get; set; }
    public double MinCpuActivity { get; set; }
    public double MinDiskActivity { get; set; } = DiskActivity.DefaultActiveCutoff;
    public double MinRate { get; set; } = DiskActivity.DefaultTransferCutoff;
    public double MinFolderPercent { get; set; } = DiskActivity.DefaultFolderCutoff;
    public int MaxCards { get; set; } = 12;
    public int MaxRows { get; set; } = 20;
    public int? Band { get; set; } = 10;
    public string CpuSort { get; set; } = "impact";
    public string DiskSort { get; set; } = "impact";

    public sealed record Guest(string Host, string Target);

    public bool IncludesGuest(string host, string target) => Guests && !HiddenGuests.Contains(new(host, target));
}
