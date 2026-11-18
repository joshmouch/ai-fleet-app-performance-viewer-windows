namespace PerformanceViewer.Components;

public sealed record MetricPoint(long Time, double Value);
public sealed record MetricSeries(string Labels, string Signal, MetricPoint[] Points);
public sealed record MetricTimeline(long Start, long End, int Step, MetricSeries[] Series);

public interface IMetricTimelines
{
    // Null means the server has no configured authority for this resource.
    Task<MetricTimeline?> ReadAsync(bool disk, CancellationToken stop);
}
