using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class CpuProcessDetails : Panel
{
    private readonly Label status = new() { AutoEllipsis = true };
    private readonly RankedTable<ProcessRow> table = new();
    private readonly ProcessCommands commands = new();
    private readonly ToolTip coverageHelp = new();
    private readonly ColumnHeader commandColumn;
    private readonly Button close = new() { Text = "Close" };
    private readonly Func<int> interval;
    private CancellationTokenSource lifetime;
    private CancellationTokenSource sample;
    private CpuExpansionTarget[] selected = Array.Empty<CpuExpansionTarget>();
    private ClusterCpuSample latest;
    private bool groupByMachine;
    private string filter = "";
    private sealed record ProcessRow(string Machine, CpuProcessRow Process, double? Impact, double? Cores);
    private int topRows;

    public bool IsOpen => lifetime != null;
    public event EventHandler CloseRequested;

    public CpuProcessDetails(Func<int> interval)
    {
        this.interval = interval;
        AccessibleName = "CPU processes";
        Visible = false;
        Size = new Size(600, 300);
        var header = new Panel { Dock = DockStyle.Top, Height = 55, Width = Width };
        var title = new Label { Text = "CPU processes", AutoSize = true, Location = new Point(8, 10) };
        close.SetBounds(header.Width - 72, 2, 64, 26);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        status.SetBounds(8, 33, Width - 16, 20);
        status.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange(new Control[] { title, close, status });

        table.AddColumn("Process", 142, row => row.Process.Process);
        table.AddColumn("Machine", 130, row => row.Machine);
        var impact = table.AddColumn("CPU impact %", 112, row => row.Impact, numeric: true);
        table.AddColumn("Guest busy %", 110, row => row.Process.GuestCpuShare, numeric: true);
        table.AddColumn("Host cores", 88, row => row.Cores, numeric: true);
        table.AddColumn("PID", 72, row => row.Process.Pid, numeric: true);
        commandColumn = table.AddColumn("Command line", 350, null);
        table.SortBy(impact);
        table.RankingChanged += (_, _) => Render();
        table.SizeChanged += (_, _) =>
        {
            int available = table.ClientSize.Width - SystemInformation.VerticalScrollBarWidth -
                table.Columns.Cast<ColumnHeader>().Where(column => column != commandColumn).Sum(column => column.Width);
            commandColumn.Width = Math.Max(250, available - 4);
        };
        Controls.Add(table);
        Controls.Add(header);
    }

    public void SetOpen(bool open)
    {
        if (open == IsOpen)
            return;
        if (!open)
        {
            var previous = lifetime;
            lifetime = null;
            commands.Suspend();
            previous.Cancel();
            previous.Dispose();
            return;
        }

        lifetime = new CancellationTokenSource();
        StartSampling();
    }

    public void SetOptions(CpuExpansionTarget[] guests, bool group, int limit, string search)
    {
        bool changedGuests = !selected.SequenceEqual(guests);
        selected = guests;
        groupByMachine = group;
        topRows = limit;
        filter = search;
        if (changedGuests && IsOpen)
            StartSampling();
        else if (IsOpen)
            Render();
    }

    public void ApplyColors(Color background, Color foreground, Color card)
    {
        BackColor = close.BackColor = card;
        ForeColor = foreground;
        table.BackColor = background;
        table.ForeColor = foreground;
    }

    private async void StartSampling()
    {
        if (!IsOpen)
            return;
        var requested = selected;
        sample?.Cancel();
        using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        sample = current;
        var cancellation = current.Token;
        latest = null;
        commands.Suspend();
        table.Items.Clear();
        table.Groups.Clear();
        status.Text = "Collecting CPU usage…";
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    var reading = await CpuProcessSampler.ReadCluster(requested, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    latest = reading;
                    Render();
                }
                catch (Exception e) when (!cancellation.IsCancellationRequested)
                {
                    latest = null;
                    commands.Suspend();
                    table.Items.Clear();
                    table.Groups.Clear();
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

    private void Render()
    {
        if (latest == null)
            return;
        var host = latest.Host;
        var measured = latest.Coverage.Where(guest => guest.HostCores.HasValue).ToArray();
        double budget = measured.Sum(guest => guest.HostCores.Value);
        double missing = measured.Sum(guest => Math.Max(0, guest.HostCores.Value - guest.AttributedCores));
        string sampleTime = $"{host.Seconds:0.0} s sample";
        status.Text = budget > 0
            ? $"Guest CPU {100 * Math.Clamp(1 - missing / budget, 0, 1):0}% attributed  |  {100 * missing / host.LogicalProcessors:0.##}% of host capacity unattributed  |  {sampleTime}"
            : sampleTime;
        var unavailable = latest.Coverage.Where(guest => !guest.SampleAvailable || !guest.HostCores.HasValue)
            .Select(guest => guest.Machine).ToArray();
        if (unavailable.Length > 0)
            status.Text += "  |  Incomplete samples: " + string.Join(", ", unavailable);
        string guestDomain = VmDiskIo.GuestDomain();
        status.Text += guestDomain == null ? "  |  Guest domain: not set" : "  |  Guest domain: " + guestDomain;
        string help = string.Join("\n", latest.Coverage.Select(guest =>
            $"{guest.Machine}: " + (guest.HostCores is double cores
                ? $"{100 * guest.AttributedCores / host.LogicalProcessors:0.##}% of host capacity attributed out of {100 * cores / host.LogicalProcessors:0.##}% measured"
                : "Host CPU budget unavailable") + (guest.SampleAvailable ? "" : " (guest sample unavailable)"))
            .Prepend("CPU impact % is measured against host capacity. Guest busy % is each process's share of its guest's used CPU."));
        if (coverageHelp.GetToolTip(status) != help)
            coverageHelp.SetToolTip(status, help);
        var rows = host.Rows.Select(row => new ProcessRow(Environment.MachineName, row,
                100 * row.Cores / host.LogicalProcessors, row.Cores))
            .Concat(latest.Guests.SelectMany(machine => machine.Rows.Select(row => new ProcessRow(machine.Name, row,
                100 * row.AttributedCores / host.LogicalProcessors, row.AttributedCores))));
        table.Render(rows.Where(row => RowFilter.Matches(filter, row.Process.Process, row.Machine, row.Process.Pid.ToString())),
            row => row.Machine, groupByMachine, topRows, NewRow);
        commands.Update(table, commandColumn.Index, item => (ProcessIdentity)item.Tag);
    }

    private static ListViewItem NewRow(ProcessRow row, ListViewGroup group) =>
        new(new[] { row.Process.Process, row.Machine, row.Impact?.ToString("0.##") ?? "—",
            (100 * row.Process.GuestCpuShare)?.ToString("0.##") ?? "—",
            row.Cores?.ToString("0.##") ?? "—", row.Process.Pid > 0 ? row.Process.Pid.ToString() : "—", "" }, group)
        { Tag = new ProcessIdentity(row.Machine, row.Process.Pid, row.Process.Process, row.Process.Started) };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SetOpen(false);
            commands.Dispose();
            coverageHelp.Dispose();
        }
        base.Dispose(disposing);
    }
}
