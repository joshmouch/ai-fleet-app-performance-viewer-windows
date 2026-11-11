using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed record ProcessIdentity(string Machine, int Pid, string Name, long Started = 0);

internal sealed class ProcessCommands : IDisposable
{
    private readonly Dictionary<ProcessIdentity, (long ReadAt, string Command)> cache = new();
    private ListView table;
    private int column;
    private bool viewportOnly;
    private Func<ListViewItem, ProcessIdentity> identify;
    private CancellationTokenSource pending;
    private bool disposed;

    public void Update(ListView visibleTable, int commandColumn, Func<ListViewItem, ProcessIdentity> identity, bool viewportOnly = false)
    {
        table = visibleTable;
        column = commandColumn;
        identify = identity;
        this.viewportOnly = viewportOnly;
        Refresh();
    }

    public void Suspend() => table = null;

    public void Refresh()
    {
        if (table == null || disposed)
            return;
        foreach (var key in cache.Where(pair => Stopwatch.GetElapsedTime(pair.Value.ReadAt).TotalSeconds >=
            (pair.Value.Command == null || pair.Key.Started == 0 ? 10 : 60)).Select(pair => pair.Key).ToArray())
            cache.Remove(key);

        var missing = new HashSet<ProcessIdentity>();
        int start = viewportOnly ? table.TopItem?.Index ?? 0 : 0;
        int end = viewportOnly ? Math.Min(table.Items.Count, start + table.ClientSize.Height / FontRowHeight() + 2)
            : table.Items.Count;
        for (int i = start; i < end; i++)
        {
            var item = table.Items[i];
            var process = identify(item);
            if (process == null)
                continue;
            if (process.Pid <= 0)
                item.SubItems[column].Text = "—";
            else if (cache.TryGetValue(process, out var entry))
            {
                item.SubItems[column].Text = entry.Command ?? "—";
                item.ToolTipText = entry.Command ?? "Command line unavailable";
            }
            else
            {
                item.SubItems[column].Text = "…";
                missing.Add(process);
            }
        }

        if (pending == null && missing.Count > 0)
            Load(missing.ToArray());
    }

    private int FontRowHeight() => table.Items.Count > 0 ? Math.Max(1, table.GetItemRect(0).Height) : 1;

    private async void Load(ProcessIdentity[] processes)
    {
        using var current = new CancellationTokenSource();
        pending = current;
        try
        {
            var batches = await Task.WhenAll(processes.GroupBy(process => process.Machine, StringComparer.OrdinalIgnoreCase)
                .Select(group => ReadMachine(group.Key, group.ToArray(), current.Token)));
            if (current.IsCancellationRequested || disposed)
                return;

            long readAt = Stopwatch.GetTimestamp();
            var commands = batches.SelectMany(batch => batch).ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var process in processes)
                cache[process] = (readAt, commands.GetValueOrDefault(process));
        }
        catch (Exception) when (current.IsCancellationRequested) { }
        finally
        {
            pending = null;
            Refresh();
        }
    }

    private static async Task<Dictionary<ProcessIdentity, string>> ReadMachine(string machine, ProcessIdentity[] processes,
        CancellationToken cancellation)
    {
        try
        {
            if (string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return await Task.Run(() => ReadWindows(processes), cancellation).ConfigureAwait(false);
            string[] pids = processes.Select(process => process.Pid.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray();
            string output = machine.StartsWith("WSL / ", StringComparison.OrdinalIgnoreCase)
                ? await ReadWsl(machine[6..], pids, cancellation).ConfigureAwait(false)
                : await VmDiskIo.ReadGuestCommands(machine, pids, cancellation).ConfigureAwait(false);
            return ParseLinux(output, processes);
        }
        catch (Exception) when (!cancellation.IsCancellationRequested) { return new(); }
    }

    private static Dictionary<ProcessIdentity, string> ReadWindows(ProcessIdentity[] processes)
    {
        var byPid = processes.ToLookup(process => process.Pid);
        string where = string.Join(" OR ", byPid.Select(group => "ProcessId=" + group.Key.ToString(CultureInfo.InvariantCulture)));
        using var query = new ManagementObjectSearcher("SELECT ProcessId, Name, CreationDate, CommandLine FROM Win32_Process WHERE " + where);
        query.Options.Timeout = TimeSpan.FromSeconds(5);
        using var rows = query.Get();
        var result = new Dictionary<ProcessIdentity, string>();
        foreach (ManagementObject row in rows)
            using (row)
            {
                if (row["CommandLine"] is not string command || string.IsNullOrWhiteSpace(command))
                    continue;
                long started = row["CreationDate"] is string date
                    ? ManagementDateTimeConverter.ToDateTime(date).ToUniversalTime().Ticks : 0;
                string name = Path.GetFileNameWithoutExtension(row["Name"] as string);
                foreach (var process in byPid[Convert.ToInt32(row["ProcessId"], CultureInfo.InvariantCulture)])
                    if ((process.Started == 0 || Math.Abs(process.Started - started) < TimeSpan.TicksPerMillisecond) &&
                        (string.Equals(process.Name, name, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(process.Name, row["Name"] as string, StringComparison.OrdinalIgnoreCase)))
                        result[process] = command;
            }
        return result;
    }

    private static async Task<string> ReadWsl(string distro, string[] pids, CancellationToken cancellation)
    {
        string code = await VmDiskIo.ReadScript("GuestCommands.sh", cancellation).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var result = await VmDiskIo.Run(CpuProcessSampler.Wsl,
            new[] { "--distribution", distro, "--exec", "/bin/sh", "-s", "--" }.Concat(pids), code, deadline.Token)
            .ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : "";
    }

    private static Dictionary<ProcessIdentity, string> ParseLinux(string output, ProcessIdentity[] processes)
    {
        var byPid = processes.ToLookup(process => process.Pid);
        var result = new Dictionary<ProcessIdentity, string>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.TrimEnd('\r').Split('\t', 4);
            if (fields.Length != 4 || !int.TryParse(fields[0], out int pid) || !long.TryParse(fields[1], out long started))
                continue;
            foreach (var process in byPid[pid])
                if ((process.Started != 0 ? process.Started == started : process.Name == fields[2]) && fields[3].Length > 0)
                    result[process] = fields[3];
        }
        return result;
    }

    public void Dispose()
    {
        disposed = true;
        table = null;
        if (pending != null)
            _ = pending.CancelAsync();
    }
}
