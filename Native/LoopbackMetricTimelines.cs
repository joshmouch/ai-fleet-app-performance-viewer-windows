using System.Net;
using System.Net.Http.Json;
using PerformanceViewer.Components;

namespace PerformanceViewer.Native;

internal sealed class LoopbackMetricTimelines(HttpClient client) : IMetricTimelines
{
    public async Task<MetricTimeline?> ReadAsync(bool disk, CancellationToken stop)
    {
        using var response = await client.GetAsync("api/metrics/" + (disk ? "disk" : "cpu"), HttpCompletionOption.ResponseHeadersRead, stop);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(NativeJson.Default.MetricTimeline, stop);
    }
}
