using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using PerformanceViewer.Components;

namespace PerformanceViewer.Web;

public sealed class PrometheusTimelines : IMetricTimelines, IDisposable
{
    public sealed class Settings
    {
        public Uri? Endpoint { get; set; }
        public string? CpuRatio { get; set; }
        public string? DiskReadBytesPerSecond { get; set; }
        public string? DiskWriteBytesPerSecond { get; set; }
        public int RangeSeconds { get; set; } = 600;
        public int StepSeconds { get; set; } = 10;
        public int? LatencyOffsetSeconds { get; set; }
        public string? BearerTokenEnvironment { get; set; }
    }

    private sealed class Cache : IDisposable
    {
        public readonly SemaphoreSlim Gate = new(1);
        public MetricTimeline? Latest;
        public void Dispose() => Gate.Dispose();
    }
    private readonly Settings settings;
    private readonly IHttpClientFactory clients;
    private readonly Cache cpu = new(), disks = new();
    public PrometheusTimelines(IConfiguration config, IHttpClientFactory clients)
    {
        this.clients = clients;
        settings = config.GetSection("MetricHistory").Get<Settings>() ?? new();
        if (settings.Endpoint is {} uri && (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || settings.StepSeconds < 1 ||
            settings.RangeSeconds < settings.StepSeconds || settings.LatencyOffsetSeconds < 0))
            throw new InvalidDataException("Invalid MetricHistory endpoint, step or range.");
    }

    public async Task<MetricTimeline?> ReadAsync(bool disk, CancellationToken stop)
    {
        if (settings.Endpoint == null) return null;
        var queries = disk
            ? new[] { ("Read", settings.DiskReadBytesPerSecond), ("Write", settings.DiskWriteBytesPerSecond) }
            : new[] { ("CPU", settings.CpuRatio) };
        var enabled = queries.Where(q => !string.IsNullOrWhiteSpace(q.Item2)).ToArray();
        if (enabled.Length == 0) return null;
        var cache = disk ? disks : cpu;
        await cache.Gate.WaitAsync(stop);
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long end = now - now % settings.StepSeconds;
            if (cache.Latest?.End == end) return cache.Latest;
            long start = end - settings.RangeSeconds;
            var groups = await Task.WhenAll(enabled.Select(q => Query(q.Item1, q.Item2!, start, end, stop)));
            return cache.Latest = new(start, end, settings.StepSeconds, groups.SelectMany(g => g).ToArray());
        }
        finally { cache.Gate.Release(); }
    }

    private async Task<MetricSeries[]> Query(string signal, string expression, long start, long end, CancellationToken stop)
    {
        var url = new Uri(settings.Endpoint!.AbsoluteUri.TrimEnd('/') + "/api/v1/query_range");
        var form = new Dictionary<string, string> {
            ["query"] = expression, ["start"] = start.ToString(CultureInfo.InvariantCulture),
            ["end"] = end.ToString(CultureInfo.InvariantCulture), ["step"] = settings.StepSeconds.ToString(CultureInfo.InvariantCulture)
        };
        if (settings.LatencyOffsetSeconds is int offset) form["latency_offset"] = offset.ToString(CultureInfo.InvariantCulture) + "s";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        if (settings.BearerTokenEnvironment is string name)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidDataException("Metric history credential is unavailable."));
        using var client = clients.CreateClient("metric-history");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(stop);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: stop);
        if (doc.RootElement.GetProperty("status").GetString() != "success") throw new InvalidDataException("Metric query failed.");
        var data = doc.RootElement.GetProperty("data");
        if (data.GetProperty("resultType").GetString() != "matrix") throw new InvalidDataException("Expected a range of metric samples.");
        return data.GetProperty("result").EnumerateArray().Select(entry =>
        {
            var labels = entry.GetProperty("metric").EnumerateObject().Where(x => x.Name != "__name__")
                .OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name + "=" + x.Value.GetString());
            var points = new List<MetricPoint>();
            foreach (var pair in entry.GetProperty("values").EnumerateArray())
                if (double.TryParse(pair[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                    points.Add(new((long)pair[0].GetDouble(), value));
            return new MetricSeries(string.Join(", ", labels), signal, points.ToArray());
        }).ToArray();
    }

    public void Dispose() { cpu.Dispose(); disks.Dispose(); }
}
