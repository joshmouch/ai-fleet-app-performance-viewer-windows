using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

public static class FileWindowExport
{
    public static bool IsCollectionStatus(DiskPathWindow disk) =>
        disk.Seconds == 0 && !string.IsNullOrWhiteSpace(disk.Status) && disk.Files.Length == 0
        && disk.CapturedBytes == 0 && disk.NamedBytes == 0 && disk.KeylessBytes == 0 && disk.EventsLost == 0;

    public static byte[] Encode(PerformanceWindow window)
    {
        window.DiskPaths.RequireHostAggregate();
        var disk = window.DiskPaths;
        var body = StoredFileWindow.From(window).Encode();
        static object Attribute(string key, string value) => new { key, value = new { stringValue = value } };
        var record = new {
            timeUnixNano = checked(disk.End.ToUnixTimeMilliseconds() * 1_000_000L).ToString(CultureInfo.InvariantCulture),
            attributes = new[] { Attribute("event.name", "performance_viewer.file_window") }, body = new { stringValue = body }
        };
        return JsonSerializer.SerializeToUtf8Bytes(new { resourceLogs = new[] { new {
            resource = new { attributes = new[] { Attribute("service.name", "ai-fleet-performance-viewer"), Attribute("host.name", window.Host) } },
            scopeLogs = new[] { new { scope = new { name = "performance-viewer" }, logRecords = new[] { record } } }
        } } });
    }

    public static async Task<int> Run(string[] args)
    {
        var endpoint = ConfiguredEndpoint();
        if (args.Length > 1 || endpoint == null)
        {
            Console.Error.WriteLine("Set OTEL_EXPORTER_OTLP_LOGS_ENDPOINT to the HTTP logs URL. Supply one NDJSON file or pipe collector windows to stdin.");
            return 2;
        }
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        long lineNumber = 0;
        try
        {
            using var client = OpenReceiver(endpoint);
            using var file = args.Length == 1 ? File.OpenText(args[0]) : null;
            var input = file ?? Console.In;
            while (await input.ReadLineAsync(cancel.Token) is string line)
            {
                lineNumber++;
                var frame = PerformanceWindow.Decode(line);
                if (IsCollectionStatus(frame.DiskPaths))
                {
                    Console.Error.WriteLine(frame.DiskPaths.Status);
                    continue;
                }
                await Send(client, Compress(frame), cancel.Token);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return 130; }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or FormatException
            or InvalidDataException or OperationCanceledException or ArgumentException or OverflowException)
        {
            Console.Error.WriteLine($"File-window export stopped at input line {lineNumber} ({e.GetType().Name}). {e.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static Uri ConfiguredEndpoint() =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"), UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https" ? endpoint : null;

    internal static HttpClient OpenReceiver(Uri endpoint = null)
    {
        endpoint ??= ConfiguredEndpoint();
        if (endpoint == null)
            throw new InvalidDataException("Set OTEL_EXPORTER_OTLP_LOGS_ENDPOINT to the HTTP logs URL.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = endpoint };
        try
        {
            var headers = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_HEADERS")
                ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS") ?? "";
            foreach (var header in headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = header.Split('=', 2);
                if (parts.Length != 2) throw new InvalidDataException("Malformed OTLP header setting.");
                try { client.DefaultRequestHeaders.Add(parts[0], Uri.UnescapeDataString(parts[1])); }
                catch (Exception e) when (e is FormatException or InvalidOperationException)
                { throw new InvalidDataException("Invalid OTLP request header."); }
            }
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    internal static byte[] Compress(PerformanceWindow window)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encode(window));
        return buffer.ToArray();
    }

    internal static async Task Send(HttpClient client, byte[] bytes, CancellationToken stop)
    {
        for (int attempt = 0; ; attempt++)
        {
            TimeSpan delay = TimeSpan.FromSeconds(1 << attempt);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            try
            {
                using var reply = await client.PostAsync(client.BaseAddress, content, stop);
                if (reply.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await reply.Content.ReadAsStreamAsync(stop));
                    if (json.RootElement.TryGetProperty("partialSuccess", out var partial) &&
                        partial.TryGetProperty("rejectedLogRecords", out var rejected) && rejected.ToString() != "0")
                        throw new InvalidDataException("Collector rejected a file window.");
                    return;
                }
                if (attempt >= 3 || (int)reply.StatusCode is not (429 or 502 or 503 or 504))
                    reply.EnsureSuccessStatusCode();
                var retry = reply.Headers.RetryAfter;
                if (retry?.Delta is {} wait) delay = wait;
                else if (retry?.Date is {} date && date > DateTimeOffset.UtcNow) delay = date - DateTimeOffset.UtcNow;
            }
            catch (HttpRequestException e) when (e.StatusCode == null && attempt < 3) { }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested && attempt < 3) { }
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, stop);
        }
    }
}
