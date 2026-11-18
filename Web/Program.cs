using PerformanceViewer.Web;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("ViewerPort", 5217);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1";
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<WindowSources>();
builder.Services.AddHostedService(services => services.GetRequiredService<WindowSources>());
var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/api/windows", (WindowSources sources) => sources.Read());
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
