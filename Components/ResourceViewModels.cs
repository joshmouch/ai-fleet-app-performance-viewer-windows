using System.Globalization;

namespace PerformanceViewer.Components;

public sealed record ResourcePoint(DateTimeOffset At, ResourceReading Reading, double Seconds);
public sealed record ResourceCardEntry(ResourceNode Node, ResourceReading Reading);
public enum ResourceDetailView { Processes, Paths, Folders }
public sealed record ResourceDetailMetric(string Key, string Label, string Format, string Suffix = "", string? Hint = null,
    bool Sortable = true, Func<ResourceDetailRow, object?>? Denominator = null, string? BandBasis = null,
    string? RequiredScope = null)
{
    public string Display(double? value) => value is double number
        ? number.ToString(Format, CultureInfo.CurrentCulture) + Suffix : "—";

    public bool CanAdd(IEnumerable<ResourceDetailRow> rows, int column) =>
        Denominator == null || rows.Where(row => row.Values[column].HasValue).Select(Denominator).Distinct().Take(2).Count() < 2;

    public double? Subtotal(IEnumerable<ResourceDetailRow> rows, int column)
    {
        if (!CanAdd(rows, column)) return null;
        var values = rows.Select(row => row.Values[column]).OfType<double>().ToArray();
        return values.Length == 0 ? null : values.Sum();
    }
}

public sealed record ResourceDetailRow(string[] Labels, double?[] Values, double Weight = 0, string? Host = null, string? Guest = null,
    DiskPathFolder? Folder = null);
public sealed record ResourceDetailBand(int? Start, ResourceDetailRow[] Rows)
{
    public double Weight => Rows.Sum(row => row.Weight);
}

public sealed record ResourceDetailTable(ResourceDetailMetric[] Metrics, ResourceDetailRow[] Rows, double TotalWeight = 0,
    string? Note = null, string? Summary = null, bool Paths = false, string? SortedBy = null, string? BandIssue = null)
{
    public static readonly string[] ProcessColumns = ["Process", "PID", "Environment"];
    public string[] LabelColumns => Paths ? ["Process", "File", "Folder", "PID", "Environment"] : ProcessColumns;
    public static string[] Identity(string process, int pid, string environment, FileIoKey? file = null)
    {
        var identity = new[] { process, pid.ToString(CultureInfo.CurrentCulture), environment };
        return file is { } path
            ? [process, path.Path.Length == 0 ? "Unresolved file path" : path.Filename, path.Filepath, identity[1], environment]
            : identity;
    }

    public static readonly ResourceDetailMetric[] CpuMetrics =
    [
        new("impact", "Host CPU", "N2", "%", "CPU time as a share of the host's total capacity.",
            Denominator: row => row.Host, BandBasis: "Host CPU in process rows", RequiredScope: "one host"),
        new("guest", "Guest CPU share", "N2", "%", "Share of sampled CPU time within this guest.",
            Denominator: row => (row.Host, row.Guest), BandBasis: "Guest CPU in process rows", RequiredScope: "one guest")
    ];

    public static ResourceDetailMetric[] DiskMetrics(bool guestOnly)
    {
        string basis = guestOnly ? "Guest" : "Host";
        return
        [
            new("impact", "Host MiB/s", "N2", Hint: "Captured host read and write rate, including I/O attributed to guests.",
                Sortable: !guestOnly),
            new("guest", "Guest MiB/s", "N2", Hint: "Guest file read and write rate, including cached I/O."),
            new("read", basis + " read MiB/s", "N2"), new("write", basis + " write MiB/s", "N2"),
            new("reads", "Read ops", "N0", BandBasis: basis + " read operations"),
            new("writes", "Write ops", "N0", BandBasis: basis + " write operations"),
            new("operations", "Total ops", "N0", BandBasis: basis + " total operations")
        ];
    }

    public ResourceDetailMetric? SortMetric => Metrics.FirstOrDefault(metric => metric.Key == SortedBy);
    public double ShownShare => TotalWeight > 0 ? Math.Min(1, Rows.Sum(row => row.Weight) / TotalWeight) : 0;
    public static string FormatShare(double share) => share switch
    {
        > 0 and < .001 => "<0.1%",
        > .999 and < 1 => ">99.9%",
        _ => share.ToString("P1", CultureInfo.CurrentCulture)
    };

    public IEnumerable<ResourceDetailBand> Bands(int size)
    {
        if (size <= 0 || BandIssue != null || TotalWeight <= 0) return [new(null, Rows)];
        double consumed = 0;
        int? Bucket(ResourceDetailRow row)
        {
            if (row.Weight <= 0) return null;
            var start = Math.Min(99, (int)(100 * consumed / TotalWeight)) / size * size;
            consumed += row.Weight;
            return start;
        }
        return Rows.GroupBy(Bucket).Select(group => new ResourceDetailBand(group.Key, group.ToArray()));
    }

    public static ResourceDetailTable Rank(ResourceDetailMetric[] metrics, IEnumerable<(bool Eligible, ResourceDetailRow Row)> rows,
        string sort, int limit, string? note = null, string? summary = null, bool paths = false)
        => Rank(metrics, rows, (row, column) => row.Values[column], row => row, sort, limit, note, summary, paths);

    public static ResourceDetailTable Rank<TRow>(ResourceDetailMetric[] metrics, IEnumerable<(bool Eligible, TRow Row)> rows,
        Func<TRow, int, double?> value, Func<TRow, ResourceDetailRow> display, string sort, int limit,
        string? note = null, string? summary = null, bool paths = false)
    {
        int column = Array.FindIndex(metrics, metric => metric.Key == sort && metric.Sortable);
        if (column < 0) column = Array.FindIndex(metrics, metric => metric.Sortable);
        var weighted = rows.Select(entry =>
            (entry.Eligible, entry.Row, Weight: value(entry.Row, column).GetValueOrDefault())).ToArray();
        var metric = metrics[column];
        var bandIssue = metric.Denominator != null && !metric.CanAdd(weighted.Select(entry => display(entry.Row)), column)
            ? $"Bands for {metric.Label} require {metric.RequiredScope}."
            : weighted.Any(entry => entry.Weight < 0) ? $"Bands unavailable for negative {metric.Label} values." : null;
        var bandTotal = bandIssue == null ? weighted.Sum(entry => entry.Weight) : 0;
        var visible = weighted.Where(entry => entry.Eligible).OrderByDescending(entry => entry.Weight)
            .Take(Math.Clamp(limit, 1, 100)).Select(entry => display(entry.Row) with { Weight = entry.Weight }).ToArray();
        return new(metrics, visible, bandTotal, note, summary, paths, metric.Key, bandIssue);
    }
}
