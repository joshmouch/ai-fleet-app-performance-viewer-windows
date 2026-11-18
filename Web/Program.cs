using PerformanceViewer.Web;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("ViewerPort", 5217);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1";
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<WindowSources>();
builder.Services.AddHttpClient("file-history").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<PerformanceViewer.Components.IFileHistory, ClickHouseFileHistory>();
builder.Services.AddResponseCompression();
builder.Services.AddHostedService(services => services.GetRequiredService<WindowSources>());
var app = builder.Build();
app.UseResponseCompression();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/api/history/sources", (PerformanceViewer.Components.IFileHistory history, CancellationToken stop) => history.SourcesAsync(stop));
app.MapGet("/api/history/window", async (string source, DateTimeOffset at, PerformanceViewer.Components.IFileHistory history, CancellationToken stop) =>
{
    try
    {
        var window = await history.ReadAsync(source, at, stop);
        return window == null ? Results.NoContent() : Results.Json(window);
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidDataException e) { return Results.Problem(e.Message, statusCode: 422); }
    catch (ArgumentOutOfRangeException) { return Results.BadRequest(); }
    catch (HttpRequestException) { return Results.Problem("History database unavailable.", statusCode: 503); }
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
