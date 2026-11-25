using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

try
{
    var (mode, preview) = args switch
    {
        ["Source"] => ("Source", false),
        ["Package"] => ("Package", false),
        ["Source", "--pin-preview"] => ("Source", true),
        ["Package", "--pin-preview"] => ("Package", true),
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
    if (preview) start.ArgumentList.Add("--pin-preview");
    else
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            start.UseShellExecute = true;
            start.Verb = "runas";
            try
            {
                using var elevated = Process.Start(start);
                return 0;
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) // UAC declined.
            {
                start.UseShellExecute = false;
                start.Verb = "";
            }
        }
    }
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
