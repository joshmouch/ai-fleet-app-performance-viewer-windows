using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class CpuProcessDetails : Panel
{
    private readonly CheckedListBox targets = new()
    {
        CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Expand guest CPU"
    };
    private readonly ComboBox ranking = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "CPU ranking" };
    private readonly NumericUpDown limit = new() { Minimum = 1, Maximum = 50, Value = 10, AccessibleName = "Number of top processes" };
    private readonly Label status = new() { AutoEllipsis = true };
    private readonly ListView table = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly Button close = new() { Text = "Close" };
    private readonly Func<int> interval;
    private CancellationTokenSource lifetime;
    private CancellationTokenSource sample;
    private CpuExpansionTarget[] selected = Array.Empty<CpuExpansionTarget>();
    private ClusterCpuSample latest;
    private bool updatingTargets;

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
        ranking.SetBounds(110, 5, 128, 25);
        ranking.Items.AddRange(new object[] { "Top per machine", "Merged top" });
        ranking.SelectedIndex = 0;
        var topLabel = new Label { Text = "Top", AutoSize = true, Location = new Point(250, 10) };
        limit.SetBounds(280, 5, 52, 25);
        close.SetBounds(header.Width - 72, 2, 64, 26);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        status.SetBounds(8, 33, Width - 16, 20);
        status.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange(new Control[] { title, ranking, topLabel, limit, close, status });

        var body = new Panel { Dock = DockStyle.Fill };
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 160, Padding = new Padding(0, 0, 6, 0) };
        var sidebarTitle = new Label { Text = "Expand guest CPU", Dock = DockStyle.Top, Height = 22, Padding = new Padding(5, 2, 0, 0) };
        sidebar.Controls.Add(targets);
        sidebar.Controls.Add(sidebarTitle);
        var name = table.Columns.Add("Process", 200);
        var machineColumn = table.Columns.Add("Machine", 130);
        table.Columns.Add("PID", 52, HorizontalAlignment.Right);
        table.Columns.Add("Guest CPU %", 82, HorizontalAlignment.Right);
        table.Columns.Add("Host CPU %", 80, HorizontalAlignment.Right);
        table.Columns.Add("Host cores", 70, HorizontalAlignment.Right);
        table.SizeChanged += (_, _) =>
        {
            int available = table.ClientSize.Width - SystemInformation.VerticalScrollBarWidth -
                table.Columns.Cast<ColumnHeader>().Skip(2).Sum(column => column.Width);
            machineColumn.Width = Math.Clamp(available - 70, 90, 130);
            name.Width = Math.Max(70, available - machineColumn.Width);
        };
        body.Controls.Add(table);
        body.Controls.Add(sidebar);
        Controls.Add(body);
        Controls.Add(header);

        ranking.SelectedIndexChanged += (_, _) => Render();
        limit.ValueChanged += (_, _) => Render();
        targets.ItemCheck += (_, _) =>
        {
            if (!updatingTargets && IsOpen)
                BeginInvoke((Action)StartSampling);
        };
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
        StartSampling();
        _ = RefreshTargets(lifetime.Token);
    }

    private async Task RefreshTargets(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var available = await Task.Run(CpuProcessSampler.ReadTargets, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!targets.Items.Cast<CpuExpansionTarget>().SequenceEqual(available))
                {
                    var checkedTargets = targets.CheckedItems.Cast<CpuExpansionTarget>().ToHashSet();
                    updatingTargets = true;
                    targets.BeginUpdate();
                    try
                    {
                        targets.Items.Clear();
                        foreach (var target in available)
                            targets.Items.Add(target, checkedTargets.Contains(target));
                    }
                    finally
                    {
                        targets.EndUpdate();
                        updatingTargets = false;
                    }
                    StartSampling();
                }
                await Task.Delay(TimeSpan.FromSeconds(15), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public void ApplyColors(Color background, Color foreground, Color card)
    {
        BackColor = close.BackColor = ranking.BackColor = limit.BackColor = card;
        ForeColor = foreground;
        table.BackColor = targets.BackColor = background;
        table.ForeColor = targets.ForeColor = ranking.ForeColor = limit.ForeColor = foreground;
    }

    private async void StartSampling()
    {
        if (!IsOpen)
            return;
        var requested = targets.CheckedItems.Cast<CpuExpansionTarget>().ToArray();
        if (sample != null && !sample.IsCancellationRequested && selected.SequenceEqual(requested))
            return;
        selected = requested;
        sample?.Cancel();
        using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        sample = current;
        var cancellation = current.Token;
        latest = null;
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
        var machines = latest.Guests.Prepend(new CpuMachineProcesses(Environment.MachineName, host.Rows));
        int maximum = (int)limit.Value;
        bool group = ranking.SelectedIndex == 0 && latest.Guests.Length > 0;
        status.Text = $"{host.Seconds:0.0} s host sample  |  Guest % of busy CPU  |  Host % of Windows capacity";
        if (latest.Unexpanded.Length > 0)
            status.Text += "  |  Host rows kept for " + string.Join(", ", latest.Unexpanded);

        table.BeginUpdate();
        try
        {
            table.Items.Clear();
            table.Groups.Clear();
            table.ShowGroups = group;
            if (group)
            {
                foreach (var machine in machines)
                {
                    string heading = machine.VmCores is double cores
                        ? $"{machine.Name}  |  {100 * cores / host.LogicalProcessors:0.##}% host CPU"
                        : machine.Name;
                    var section = new ListViewGroup(heading);
                    table.Groups.Add(section);
                    table.Items.AddRange(machine.Rows.OrderByDescending(row => row.Cores).ThenBy(row => row.Pid)
                        .Take(maximum).Select(row => NewRow(machine.Name, row, host.LogicalProcessors, section)).ToArray());
                }
            }
            else
            {
                table.Items.AddRange(machines.SelectMany(machine => machine.Rows.Select(row => (machine.Name, Row: row)))
                    .OrderByDescending(entry => entry.Row.Cores).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Row.Pid).Take(maximum)
                    .Select(entry => NewRow(entry.Name, entry.Row, host.LogicalProcessors)).ToArray());
            }
        }
        finally { table.EndUpdate(); }
    }

    private static ListViewItem NewRow(string machine, CpuProcessRow row, int processors, ListViewGroup group = null) =>
        new(new[] { row.Process, machine, row.Pid.ToString(),
            row.GuestCpuShare is double share ? (100 * share).ToString("0.##") : "",
            (100 * row.Cores / processors).ToString("0.##"), row.Cores.ToString("0.##") }, group);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            SetOpen(false);
        base.Dispose(disposing);
    }
}
