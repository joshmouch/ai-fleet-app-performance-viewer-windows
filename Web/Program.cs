using PerformanceViewer.Web;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("ViewerPort", 5217);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1";
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<WindowSources>();
builder.Services.AddHttpClient("metric-history").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<PerformanceViewer.Components.IMetricTimelines, PrometheusTimelines>();
builder.Services.AddResponseCompression();
builder.Services.AddHostedService(services => services.GetRequiredService<WindowSources>());
var app = builder.Build();
app.UseResponseCompression();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/api/metrics/{resource}", async (string resource, PerformanceViewer.Components.IMetricTimelines history, CancellationToken stop) =>
{
    if (resource is not ("cpu" or "disk")) return Results.NotFound();
    try
    {
        var result = await history.ReadAsync(resource == "disk", stop);
        return result == null ? Results.NoContent() : Results.Json(result);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
    { return Results.Problem("Metric history is unavailable.", statusCode: 503); }
});
app.MapGet("/api/windows", (WindowSources sources, HttpContext context) =>
{
    var snapshot = sources.Read();
    context.Response.Headers.CacheControl = "private, no-cache";
    context.Response.Headers.ETag = snapshot.Tag;
    if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(t => t.Tag == snapshot.Tag || t.Tag == "*") == true)
        return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Json(snapshot.Windows);
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
