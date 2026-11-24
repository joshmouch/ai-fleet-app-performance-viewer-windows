using System.Globalization;

namespace PerformanceViewer.Components;

public sealed record ResourcePoint(DateTimeOffset At, ResourceReading Reading, double Seconds);
public sealed record ResourceCardEntry(ResourceNode Node, ResourceReading Reading);
public sealed record ResourceDetailMetric(string Key, string Label, string Format, string Suffix = "", string? Hint = null,
    bool Sortable = true, Func<ResourceDetailRow, object?>? Denominator = null)
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

public sealed record ResourceDetailRow(string[] Labels, double?[] Values, double Weight = 0, string? Host = null, string? Guest = null);

public sealed record ResourceDetailTable(ResourceDetailMetric[] Metrics, ResourceDetailRow[] Rows, double TotalWeight = 0,
    string? Note = null, string? Summary = null, bool Paths = false, string? SortedBy = null)
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
            Denominator: row => row.Host),
        new("guest", "Guest CPU share", "N2", "%", "Share of sampled CPU time within this guest.",
            Denominator: row => (row.Host, row.Guest))
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
            new("reads", "Read ops", "N0"), new("writes", "Write ops", "N0"), new("operations", "Total ops", "N0")
        ];
    }

    public static ResourceDetailTable Rank(ResourceDetailMetric[] metrics, IEnumerable<(bool Eligible, ResourceDetailRow Row)> rows,
        string sort, int limit, string? note = null, string? summary = null, bool paths = false, string? fallback = null)
        => Rank(metrics, rows, (row, column) => row.Values[column], row => row, sort, limit, note, summary, paths, fallback);

    public static ResourceDetailTable Rank<TRow>(ResourceDetailMetric[] metrics, IEnumerable<(bool Eligible, TRow Row)> rows,
        Func<TRow, int, double?> value, Func<TRow, ResourceDetailRow> display, string sort, int limit,
        string? note = null, string? summary = null, bool paths = false, string? fallback = null)
    {
        int column = Array.FindIndex(metrics, metric => metric.Key == sort && metric.Sortable);
        if (column < 0) column = Array.FindIndex(metrics, metric => metric.Sortable);
        int fallbackColumn = Array.FindIndex(metrics, metric => metric.Key == fallback);
        var weighted = rows.Select(entry =>
        {
            var (eligible, row) = entry;
            var weight = value(row, column);
            if (column == 0 && fallbackColumn >= 0) weight ??= value(row, fallbackColumn);
            return (Eligible: eligible, Row: row, Weight: weight.GetValueOrDefault());
        }).ToArray();
        var metric = metrics[column];
        var bandTotal = metric.Denominator == null || metric.CanAdd(weighted.Select(entry => display(entry.Row)), column)
            ? weighted.Sum(entry => entry.Weight) : 0;
        var visible = weighted.Where(entry => entry.Eligible).OrderByDescending(entry => entry.Weight)
            .Take(Math.Clamp(limit, 1, 100)).Select(entry => display(entry.Row) with { Weight = entry.Weight }).ToArray();
        return new(metrics, visible, bandTotal, note, summary, paths, metric.Key);
    }
}
