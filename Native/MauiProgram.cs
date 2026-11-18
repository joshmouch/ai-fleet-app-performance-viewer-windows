using Microsoft.AspNetCore.Components.WebView.Maui;

namespace PerformanceViewer.Native;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<ViewerApp>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<PerformanceViewer.Components.IMetricTimelines, LoopbackMetricTimelines>();
        builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false
        }) { BaseAddress = new Uri($"http://127.0.0.1:{Environment.GetEnvironmentVariable("ViewerPort") ?? "5217"}/") });
        return builder.Build();
    }
}

internal sealed class ViewerApp : Application
{
    protected override Window CreateWindow(IActivationState? state)
    {
        var browser = new BlazorWebView { HostPage = "wwwroot/index.html" };
        browser.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(NativeDashboard) });
        return new Window(new ContentPage { Content = browser }) { Title = "Cluster Performance Viewer" };
    }
}
