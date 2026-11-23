using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

try
{
    string mode = args switch
    {
        ["Source"] => "Source",
        ["Package"] => "Package",
        _ => throw new InvalidDataException("Open Performance Viewer from the Source or Package shortcut.")
    };
    var settings = Path.Combine(AppContext.BaseDirectory, mode + ".json");
    using var file = File.Open(settings, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    var launch = JsonSerializer.Deserialize<LaunchSettings>(file)
        ?? throw new InvalidDataException($"The {mode} installation has no launch settings.");
    if (!File.Exists(launch.Shell) || !Directory.Exists(launch.Content))
        throw new FileNotFoundException($"The {mode} viewer installation or checkout was moved. Reinstall this mode.");

    var start = new ProcessStartInfo(launch.Shell) { WorkingDirectory = Path.GetDirectoryName(launch.Shell)! };
    start.ArgumentList.Add(mode == "Source" ? "--source" : "--package");
    start.ArgumentList.Add(launch.Content);
    using var child = Process.Start(start);
    return 0;
}
catch (Exception error)
{
    WindowsDialog.Show(IntPtr.Zero, error.Message, "Performance Viewer", 0x10);
    return 1;
}

internal sealed record LaunchSettings(string Shell, string Content);

internal static class WindowsDialog
{
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int Show(IntPtr owner, string message, string title, uint flags);
}
