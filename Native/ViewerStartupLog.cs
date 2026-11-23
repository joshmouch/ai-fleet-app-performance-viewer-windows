namespace PerformanceViewer.Native;

internal static class ViewerStartupLog
{
    internal static void Write(string mode, string message, Exception? error = null)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIFleetPerformanceViewerShell");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "startup.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                File.Move(path, path + ".previous", overwrite: true);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {mode} ({Environment.ProcessId}) {message}{Environment.NewLine}" +
                (error == null ? "" : error + Environment.NewLine));
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
}
