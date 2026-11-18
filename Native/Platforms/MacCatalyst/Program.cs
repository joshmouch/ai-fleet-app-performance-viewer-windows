using Foundation;
using UIKit;
namespace PerformanceViewer.Native;
public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate { }
