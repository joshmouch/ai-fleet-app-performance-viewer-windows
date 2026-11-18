using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class CollectorProcess
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Signal(int pid, int signal);

    public static void RequestStop(Process process)
    {
        if (process.HasExited) return;
        if (OperatingSystem.IsWindows()) process.Kill(entireProcessTree: true);
        // sudo forwards SIGTERM to its privileged trace child before closing its pipes.
        else if (Signal(process.Id, 15) != 0 && !process.HasExited)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
}
