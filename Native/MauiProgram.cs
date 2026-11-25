namespace PerformanceViewer.Native;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        return MauiApp.CreateBuilder().UseMauiApp<ViewerApp>().Build();
    }
}

internal sealed class ViewerApp : Application
{
    protected override Window CreateWindow(IActivationState? state)
    {
        var status = new Label { Text = "Opening Performance Viewer…", Margin = 16 };
        var page = new ContentPage { Content = status };
        var args = Environment.GetCommandLineArgs()[1..];
        bool source = args.Contains("--source", StringComparer.Ordinal);
        var window = new Window(page) { Title = source ? "Performance Viewer (Source)" : "Performance Viewer (Package)" };
        if (args.LastOrDefault() == "--pin-preview")
        {
            status.Text = window.Title;
            return window;
        }
        var lifetime = new CancellationTokenSource();
        var stop = lifetime.Token;
        bool started = false;
        window.Destroying += (_, _) => lifetime.Cancel();
        page.Loaded += (_, _) =>
        {
            if (started) return;
            started = true;
            _ = OpenViewer();
        };
        return window;

        async Task OpenViewer()
        {
            ViewerStartupLog.Write(window.Title, "Desktop window loaded.");
            status.Text = source ? "Building the viewer from source…" : "Starting the local viewer server…";
            try
            {
                using var server = await Task.Run(() => LocalViewerServer.Start(args, stop), stop).ConfigureAwait(false);
                WebView? browser = null;
                await server.Watch((address, message) => page.Dispatcher.Dispatch(() =>
                {
                    if (stop.IsCancellationRequested) return;
                    if (address == null)
                    {
                        ShowFailure(message);
                        return;
                    }
                    try
                    {
                        if (browser == null)
                        {
                            browser = new WebView();
                            browser.Navigating += (_, navigation) =>
                                navigation.Cancel = !Uri.TryCreate(navigation.Url, UriKind.Absolute, out var target) ||
                                    target.Scheme != server.Address.Scheme || target.Authority != server.Address.Authority;
                            browser.Navigated += (_, navigation) =>
                            {
                                if (stop.IsCancellationRequested) return;
                                if (navigation.Result == WebNavigationResult.Success)
                                    ViewerStartupLog.Write(window.Title, "Dashboard loaded.");
                                else
                                    ShowFailure($"The desktop browser could not load the dashboard ({navigation.Result}).");
                            };
                        }
                        browser.Source = new UrlWebViewSource { Url = address.AbsoluteUri };
                        page.Content = browser;
                        ViewerStartupLog.Write(window.Title, "Local server ready. Opening dashboard.");
                    }
                    catch (Exception error) { ShowFailure("The desktop browser could not start.", error); }
                }), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                page.Dispatcher.Dispatch(() => ShowFailure(error.Message, error));
            }
        }

        void ShowFailure(string message, Exception? error = null)
        {
            if (stop.IsCancellationRequested) return;
            ViewerStartupLog.Write(window.Title, message, error);
            status.Text = message;
            page.Content = status;
        }
    }
}
