using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed record CpuProcessRow(int Pid, string Process, double Cores);
internal sealed record CpuProcessSample(int LogicalProcessors, double Seconds, CpuProcessRow[] Rows);

internal sealed class CpuProcessDetails : Panel
{
    private const int TopRows = 50;
    private readonly ComboBox machine = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Machine" };
    private readonly Label status = new() { AutoEllipsis = true };
    private readonly ListView table = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly Button close = new() { Text = "Close" };
    private readonly Func<int> interval;
    private CancellationTokenSource lifetime;
    private CancellationTokenSource sample;
    private string inventoryError;

    public bool IsOpen => lifetime != null;
    public event EventHandler CloseRequested;

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    public CpuProcessDetails(Func<int> interval)
    {
        this.interval = interval;
        AccessibleName = "CPU processes";
        Visible = false;
        Size = new Size(600, 300);
        var header = new Panel { Dock = DockStyle.Top, Height = 59, Width = Width };
        var title = new Label { Text = "CPU processes", AutoSize = true, Location = new Point(8, 10) };
        machine.SetBounds(110, 5, 255, 25);
        machine.Items.Add(Environment.MachineName + " (Windows host)");
        machine.SelectedIndex = 0;
        close.SetBounds(header.Width - 72, 2, 64, 26);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        status.SetBounds(8, 34, Width - 16, 21);
        status.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange(new Control[] { title, machine, close, status });
        var name = table.Columns.Add("Process", 235);
        table.Columns.Add("PID", 85, HorizontalAlignment.Right);
        table.Columns.Add("CPU %", 95, HorizontalAlignment.Right);
        table.Columns.Add("Cores used", 105, HorizontalAlignment.Right);
        table.SizeChanged += (_, _) => name.Width = Math.Max(160, table.ClientSize.Width - 310);
        Controls.Add(table);
        Controls.Add(header);
        machine.SelectedIndexChanged += (_, _) => StartSampling();
    }

    public void SetOpen(bool open)
    {
        if (open == IsOpen)
            return;
        if (!open)
        {
            var previous = lifetime;
            lifetime = null;
            previous.Cancel();
            previous.Dispose();
            return;
        }

        lifetime = new CancellationTokenSource();
        inventoryError = null;
        StartSampling();
        _ = RefreshMachines(lifetime.Token);
    }

    private async Task RefreshMachines(CancellationToken cancellation)
    {
        try
        {
            var guests = await VmDiskIo.ReadRunningGuests(cancellation);
            if (cancellation.IsCancellationRequested || machine.Items.Cast<string>().Skip(1).SequenceEqual(guests))
                return;
            var selected = (string)machine.SelectedItem;
            while (machine.Items.Count > 1)
                machine.Items.RemoveAt(machine.Items.Count - 1);
            machine.Items.AddRange(guests);
            machine.SelectedIndex = Math.Max(0, machine.Items.IndexOf(selected));
        }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested)
                inventoryError = "Hyper-V guest list unavailable";
        }
    }

    public void ApplyColors(Color background, Color foreground, Color card)
    {
        BackColor = close.BackColor = card;
        ForeColor = foreground;
        table.BackColor = background;
        machine.BackColor = card;
        table.ForeColor = machine.ForeColor = foreground;
    }

    private async void StartSampling()
    {
        if (!IsOpen || machine.SelectedIndex < 0)
            return;
        sample?.Cancel();
        using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        sample = current;
        var cancellation = current.Token;
        string guest = machine.SelectedIndex == 0 ? null : (string)machine.SelectedItem;
        table.Items.Clear();
        status.Text = "Collecting CPU usage…";
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    var reading = guest == null
                        ? await Task.Run(() => ReadHost(cancellation), cancellation)
                        : await VmDiskIo.SampleCpu(guest, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    UpdateTable(reading, guest);
                }
                catch (Exception e) when (!cancellation.IsCancellationRequested)
                {
                    table.Items.Clear();
                    status.Text = e.Message;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.25,
                    interval() - Stopwatch.GetElapsedTime(start).TotalSeconds)), cancellation);
            }
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (sample == current)
                sample = null;
        }
    }

    private void UpdateTable(CpuProcessSample reading, string guest)
    {
        string capacity = guest == null ? "logical CPUs" : "vCPUs";
        status.Text = $"{reading.Seconds:0.0} s sample  |  {reading.LogicalProcessors} {capacity}  |  CPU % is share of machine capacity";
        if (inventoryError != null)
            status.Text += "  |  " + inventoryError;
        table.BeginUpdate();
        try
        {
            table.Items.Clear();
            table.Items.AddRange(reading.Rows.Select(row => new ListViewItem(new[] { row.Process,
                row.Pid.ToString(), (100 * row.Cores / reading.LogicalProcessors).ToString("0.##"),
                row.Cores.ToString("0.##") })).ToArray());
        }
        finally { table.EndUpdate(); }
    }

    private static async Task<CpuProcessSample> ReadHost(CancellationToken cancellation)
    {
        var before = ReadHostTimes();
        long start = Stopwatch.GetTimestamp();
        await Task.Delay(1000, cancellation).ConfigureAwait(false);
        var after = ReadHostTimes();
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var rows = after.Where(entry => before.TryGetValue(entry.Key, out var previous) && entry.Value.Time > previous.Time)
            .Select(entry => new CpuProcessRow(entry.Key.Pid, entry.Value.Name,
                (entry.Value.Time - before[entry.Key].Time).TotalSeconds / seconds))
            .OrderByDescending(row => row.Cores).ThenBy(row => row.Pid).Take(TopRows).ToArray();
        return new CpuProcessSample(Math.Max(1, (int)GetActiveProcessorCount(ushort.MaxValue)), seconds, rows);
    }

    private static Dictionary<(int Pid, long Started), (string Name, TimeSpan Time)> ReadHostTimes()
    {
        var result = new Dictionary<(int, long), (string, TimeSpan)>();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    if (process.Id != 0)
                        result[(process.Id, process.StartTime.ToUniversalTime().Ticks)] = (process.ProcessName, process.TotalProcessorTime);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            SetOpen(false);
        base.Dispose(disposing);
    }
}
