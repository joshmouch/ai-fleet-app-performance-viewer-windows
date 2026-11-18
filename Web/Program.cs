using PerformanceViewer.Web;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("ViewerPort", 5217);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1";
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<WindowSources>();
builder.Services.AddResponseCompression(options =>
{
    // dotnet watch injects its browser refresh script into the development HTML response.
    if (builder.Environment.IsDevelopment())
        options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
            .Where(type => !type.Equals("text/html", StringComparison.OrdinalIgnoreCase));
});
builder.Services.AddHostedService(services => services.GetRequiredService<WindowSources>());
var app = builder.Build();
app.UseResponseCompression();
app.UseAntiforgery();
app.MapStaticAssets();
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
