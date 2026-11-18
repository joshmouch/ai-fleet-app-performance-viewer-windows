using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PerformanceViewer.Components;

namespace PerformanceViewer.Web;

public sealed class ClickHouseFileHistory : IFileHistory
{
    public sealed class Source
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public string Scope { get; set; } = "host";
        public string Layer { get; set; } = "physical";
        public int LookbackHours { get; set; } = 24;
        public Uri? Endpoint { get; set; }
        public string? User { get; set; }
        public string? PasswordEnvironment { get; set; }
    }

    private readonly Dictionary<string, Source> sources;
    private readonly IHttpClientFactory clients;
    public ClickHouseFileHistory(IConfiguration config, IHttpClientFactory clients)
    {
        this.clients = clients;
        sources = (config.GetSection("FileHistory").Get<Source[]>() ?? []).ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var s in sources.Values)
            if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Host) || s.LookbackHours < 1 ||
                string.IsNullOrEmpty(s.Scope) || s.Layer is not ("physical" or "guest-logical") ||
                s.Endpoint is not { IsAbsoluteUri: true } url || url.Scheme is not ("http" or "https") ||
                url.Query.Length != 0 || url.Fragment.Length != 0 || url.UserInfo.Length != 0)
                throw new InvalidDataException("Invalid FileHistory source configuration.");
    }

    public Task<string[]> SourcesAsync(CancellationToken stop) => Task.FromResult(sources.Keys.ToArray());

    public async Task<StoredFileWindow?> ReadAsync(string source, DateTimeOffset at, CancellationToken stop)
    {
        if (!sources.TryGetValue(source, out var s)) throw new KeyNotFoundException("Unknown history source.");
        static string Bound(DateTimeOffset t) => Uri.EscapeDataString(t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        var from = at.AddHours(-s.LookbackHours);
        var url = new UriBuilder(s.Endpoint!) { Query = $"readonly=1&session_timezone=UTC&param_host={Uri.EscapeDataString(s.Host)}" +
            $"&param_scope={Uri.EscapeDataString(s.Scope)}&param_layer={Uri.EscapeDataString(s.Layer)}" +
            $"&param_from={Bound(from)}&param_to={Bound(at.AddMilliseconds(1))}" };
        const string sql = """
            SELECT Variants = 1 AS UniqueBody, if(Variants = 1, Body, '') AS Body
            FROM otel.performance_file_window_latest(host={host:String}, scope={scope:String}, layer={layer:String},
                from={from:DateTime64(3)}, to={to:DateTime64(3)})
            FORMAT JSONEachRow
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, url.Uri) { Content = new StringContent(sql, Encoding.UTF8, "text/plain") };
        if (!string.IsNullOrEmpty(s.User))
        {
            var password = s.PasswordEnvironment == null ? "" : Environment.GetEnvironmentVariable(s.PasswordEnvironment)
                ?? throw new InvalidDataException("History source credential is unavailable.");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(s.User + ":" + password)));
        }
        using var client = clients.CreateClient("file-history");
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stop);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stop));
        var line = await reader.ReadLineAsync(stop);
        if (string.IsNullOrEmpty(line)) return null;
        using var record = JsonDocument.Parse(line);
        if (record.RootElement.GetProperty("UniqueBody").GetInt32() != 1)
            throw new InvalidDataException("Stored copies of this window disagree.");
        var window = StoredFileWindow.Decode(record.RootElement.GetProperty("Body").GetString()!);
        if (window.Host != s.Host || window.Scope != s.Scope || window.Layer != s.Layer)
            throw new InvalidDataException("Stored window does not match the configured source.");
        return window;
    }
}
