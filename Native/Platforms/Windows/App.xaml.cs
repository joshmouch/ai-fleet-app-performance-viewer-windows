namespace PerformanceViewer.Native.WinUI;
public partial class App : MauiWinUIApplication
{
    public App()
    {
        var source = Environment.GetCommandLineArgs().Contains("--source", StringComparer.Ordinal);
        string mode = source ? "Source" : "Package";
        UnhandledException += (_, failure) => ViewerStartupLog.Write(mode, "Windows UI failure.", failure.Exception);
        try { ViewerWindowsIdentity.IdentifyProcess(source); }
        catch (Exception error) { ViewerStartupLog.Write(mode, "Could not set the taskbar identity.", error); }
        InitializeComponent();
    }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
