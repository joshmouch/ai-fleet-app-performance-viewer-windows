using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class PdhQuery : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private IntPtr query;
    private readonly IntPtr[] counters;

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    public PdhQuery(params string[] paths)
    {
        counters = new IntPtr[paths.Length];
        Check(PdhOpenQuery(null, IntPtr.Zero, out query), "Open performance query");
        try
        {
            for (int i = 0; i < paths.Length; i++)
                Check(PdhAddEnglishCounter(query, paths[i], IntPtr.Zero, out counters[i]), "Add " + paths[i]);
            Collect();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Collect()
    {
        Check(PdhCollectQueryData(query), "Collect performance counters");
    }

    public Dictionary<string, double> Read(int index)
    {
        IntPtr counter = counters[index];
        uint bytes = 0;
        uint count;
        uint status = PdhGetFormattedCounterArray(counter, FormatDouble, ref bytes, out count, IntPtr.Zero);
        if (status != MoreData)
            Check(status, "Size performance sample");

        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            Check(PdhGetFormattedCounterArray(counter, FormatDouble, ref bytes, out count, buffer), "Read performance sample");
            int itemSize = Marshal.SizeOf(typeof(CounterItem));
            for (int i = 0; i < count; i++)
            {
                var item = (CounterItem)Marshal.PtrToStructure(IntPtr.Add(buffer, i * itemSize), typeof(CounterItem));
                if (item.Status <= 1 && !double.IsNaN(item.Value) && !double.IsInfinity(item.Value))
                    values.Add(Marshal.PtrToStringUni(item.Name), item.Value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return values;
    }

    private static void Check(uint status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException(operation + " failed (PDH 0x" + status.ToString("X8") + ").");
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero)
        {
            PdhCloseQuery(query);
            query = IntPtr.Zero;
        }
    }
}

internal sealed class CpuCounters : IDisposable
{
    private readonly PdhQuery query = new PdhQuery(@"\Processor Information(*)\% Processor Time");

    public Dictionary<string, double> Read(out double? total)
    {
        query.Collect();
        total = null;
        var values = new Dictionary<string, double>();
        foreach (var entry in query.Read(0))
        {
            double value = Math.Max(0, Math.Min(100, entry.Value));
            if (entry.Key.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                total = value;
            else if (!entry.Key.EndsWith("_Total", StringComparison.OrdinalIgnoreCase))
                values.Add(entry.Key, value);
        }
        if (values.Count == 0)
            throw new InvalidOperationException("Windows returned no logical processor samples.");
        return values;
    }

    public void Dispose()
    {
        query.Dispose();
    }
}

internal struct DiskReading
{
    public int Number;
    public double Active;
    public double ReadMiB;
    public double WriteMiB;
}

internal sealed class DiskCounters : IDisposable
{
    private const double BytesPerMiB = 1024 * 1024;
    private readonly PdhQuery query = new PdhQuery(@"\PhysicalDisk(*)\% Idle Time",
        @"\PhysicalDisk(*)\Disk Read Bytes/sec", @"\PhysicalDisk(*)\Disk Write Bytes/sec");

    public Dictionary<string, DiskReading> Read()
    {
        query.Collect();
        var idle = query.Read(0);
        var reads = query.Read(1);
        var writes = query.Read(2);
        var disks = new Dictionary<string, DiskReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in idle)
        {
            int number;
            double read, write;
            if (!int.TryParse(entry.Key.Split(' ')[0], out number) ||
                !reads.TryGetValue(entry.Key, out read) || !writes.TryGetValue(entry.Key, out write))
                continue;

            disks.Add(entry.Key, new DiskReading
            {
                Number = number,
                Active = Math.Max(0, Math.Min(100, 100 - entry.Value)),
                ReadMiB = Math.Max(0, read) / BytesPerMiB,
                WriteMiB = Math.Max(0, write) / BytesPerMiB
            });
        }
        return disks;
    }

    public void Dispose()
    {
        query.Dispose();
    }
}

internal sealed class Timeline : Form
{
    private const int HistoryIntervals = 60;
    private const int DefaultUpdateSeconds = 10;
    private const double SamplingGraceSeconds = 1.5;
    private const int OverallPanelHeight = 112;
    private const int ChartTop = 74;
    private const int PaddingSize = 16;
    private const int CardGap = 6;
    private const int CardsTop = ChartTop + OverallPanelHeight + CardGap;
    private const double DiskActivityWindow = 60;
    private const double MinimumDiskCoverage = 20;
    private const int DefaultDiskCutoff = 25;
    private const int DefaultTransferCutoff = 1;
    private const string UpdateLabel = "Update every";
    private const string DiskCutoffLabel = "Min active";
    private const string TransferCutoffLabel = "Min I/O";
    private const int DwmUseImmersiveDarkMode = 20;
    private const string WindowsPersonalization =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly int[] UpdateIntervals = { 1, 2, 5, DefaultUpdateSeconds, 15, 30 };
    private static readonly int MaxHistorySeconds = HistoryIntervals * UpdateIntervals.Max();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CpuCounters counters;
    private readonly Timer timer = new Timer { Interval = 1000 };
    private readonly ComboBox updateInterval = new ComboBox();
    private readonly ComboBox diskCutoff = new ComboBox();
    private readonly ComboBox transferCutoff = new ComboBox();
    private readonly ComboBox diskScope = new ComboBox();
    private readonly RadioButton cpuView = new() { Text = "CPU", Appearance = Appearance.Button, Checked = true };
    private readonly RadioButton diskView = new() { Text = "Disk", Appearance = Appearance.Button };
    private readonly Button detailsButton = new() { Text = "Hide details" };
    private readonly Button settingsButton = new() { Text = "Settings ▾" };
    private readonly Button diskSettingsButton = new() { Text = "Disk filters ▾", Visible = false };
    private readonly TextBox find = new() { PlaceholderText = "Find process, path, machine", AccessibleName = "Filter detail rows" };
    private readonly ContextMenuStrip settingsMenu = new();
    private readonly ContextMenuStrip diskSettingsMenu = new();
    private readonly FlowLayoutPanel toolbar = new() { WrapContents = false };
    private readonly ComboBox diskRank = new();
    private readonly Label description = new() { AutoEllipsis = true };
    private bool detailsOpen = true;
    private readonly CheckBox showVirtualDisks = new CheckBox();
    private readonly Button guestOptions = new() { Text = "Guests", FlatStyle = FlatStyle.Flat };
    private readonly ContextMenuStrip guestMenu = new();
    private readonly CheckBox groupByMachine = new() { Text = "Group by machine", AutoSize = true };
    private const int DefaultBandWidth = 10;
    private readonly TextBox bandInput = new()
    {
        Text = DefaultBandWidth.ToString(), Width = 140, MaxLength = 3,
        PlaceholderText = "Empty = off", AccessibleName = "Usage band percentage",
        AccessibleDescription = "Enter a whole percent from 1 to 100, or clear the field to turn bands off."
    };
    private int? bandWidth = DefaultBandWidth;
    private readonly CheckBox diskAttributionGaps = new() { Text = "Show disk attribution gaps", AutoSize = true };
    private readonly NumericUpDown topRows = new() { Minimum = 1, Maximum = 50, Value = 20, AccessibleName = "Number of top rows" };
    private readonly VScrollBar cpuScroll = new() { Visible = false };
    private readonly HashSet<CpuExpansionTarget> selectedGuests = new();
    private readonly HashSet<string> excludedGuests = new(StringComparer.OrdinalIgnoreCase);
    private readonly FlowLayoutPanel diskGrid = new FlowLayoutPanel();
    private readonly DiskIoDetails diskIoDetails = new DiskIoDetails();
    private readonly ToolTip diskCardTips = new ToolTip();
    private readonly List<Sample> history = new List<Sample>();
    private DiskCounters diskCounters;
    private PdhQuery guestCounters;
    private HyperVHostCounters hostCounters;
    private string hostCountersFailure;
    private string[] diskCardLayout = Array.Empty<string>();
    private CpuExpansionTarget[] availableGuests = Array.Empty<CpuExpansionTarget>();
    private double nextGuestRefresh;
    private bool loadingGuests;
    private Dictionary<int, DiskIdentity> diskIdentities = new Dictionary<int, DiskIdentity>();
    private VolumePathIndex volumePaths = new VolumePathIndex(Array.Empty<DiskVolume>());
    private readonly CpuProcessDetails processDetails;
    private DiskSummary selectedVirtualDisk;
    private string virtualDiskStatus;
    private bool loadingDiskNames;
    private bool showingDisks;
    private string selectedDisk;
    private string diskError;
    private string[] processors = new string[0];
    private string error;
    private bool? darkMode;
    private Color idleTile;
    private Color busyTile;
    private Color lineColor;
    private Color writeLineColor;
    private double nextSampleAt;
    private double lastSampleAt;
    private int UpdateSeconds { get { return (int)updateInterval.SelectedItem; } }
    private TransferLimit SelectedTransferLimit { get { return (TransferLimit)transferCutoff.SelectedItem; } }
    private DiskScope SelectedDiskScope { get { return (DiskScope)diskScope.SelectedItem; } }

    private enum DiskScope { MountedAndSpaces, MountedOnly, All }
    private const int StorageSpacesBusType = 16;

    private sealed class Sample
    {
        public double Time;
        public double StartTime;
        public bool StartsNewSegment;
        public double? Total;
        public Dictionary<string, double> Values;
        public Dictionary<string, double> GuestValues;
        public HyperVHostSample HostCpu;
        public Dictionary<string, DiskReading> Disks;
        public Dictionary<string, DiskSummary> VirtualDisks;

        public double? GetUtilization(string processor)
        {
            if (processor == null)
                return Total;

            double value;
            return Values != null && Values.TryGetValue(processor, out value) ? (double?)value : null;
        }

        public double? GetGuestUtilization(string counter) =>
            GuestValues != null && GuestValues.TryGetValue(counter, out double value) ? value : null;

        public CpuTimeSplit? GetHostCpu(int processor) =>
            HostCpu != null && HostCpu.Processors.TryGetValue(processor, out var value) ? value : null;

        public DiskReading? GetDisk(string id)
        {
            DiskReading value;
            return Disks != null && id != null && Disks.TryGetValue(id, out value) ? (DiskReading?)value : null;
        }

        public double? GetDiskValue(string id, Func<DiskReading, double> readValue)
        {
            var disk = GetDisk(id);
            return disk.HasValue ? (double?)readValue(disk.Value) : null;
        }
    }

    private sealed class DiskSummary
    {
        public string Id;
        public DiskReading Latest;
        public double Coverage;
        public double ActiveSum;
        public double ReadSum;
        public double WriteSum;
        public VirtualDiskIo VirtualDisk;
        public VmDiskOwners VirtualOwner;
        public bool BelowFilter;
        public double Rate(bool writesOnly = false) => VirtualDisk == null ? AverageTransfer(writesOnly) :
            writesOnly ? Latest.WriteMiB : Latest.ReadMiB + Latest.WriteMiB;
        public double AverageActive { get { return ActiveSum / Coverage; } }
        public double AverageTransfer(bool writesOnly) { return (writesOnly ? WriteSum : ReadSum + WriteSum) / Coverage; }
    }

    private sealed class TransferLimit
    {
        public readonly int MinimumMiB;
        public readonly bool WritesOnly;

        public TransferLimit(int minimumMiB, bool writesOnly)
        {
            MinimumMiB = minimumMiB;
            WritesOnly = writesOnly;
        }

        public override string ToString()
        {
            return MinimumMiB == 0 ? "Off" : MinimumMiB + " MiB/s " + (WritesOnly ? "write" : "total");
        }
    }

    private sealed class DiskIdentity
    {
        public readonly string Name;
        public readonly string Kind;
        public readonly bool IsStorageSpaces;
        public string[] AccessPaths;
        public DiskVolume[] Volumes;

        public DiskIdentity(string name, string kind, bool isStorageSpaces = false)
        {
            Name = name;
            Kind = kind;
            IsStorageSpaces = isStorageSpaces;
        }
    }

    private struct HistoryRange
    {
        public double Start;
        public int Seconds;
        public int FirstSample;
        public string Duration { get { return (Seconds / 60) + " min"; } }
    }

    public Timeline(CpuCounters counters)
    {
        this.counters = counters;
        Text = "AI Fleet Cluster Performance Viewer " + Application.ProductVersion + " - " + Environment.MachineName;
        ClientSize = new Size(1160, 880);
        MinimumSize = new Size(660, 500 + OverallPanelHeight);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Font = new Font("Segoe UI", 9);

        updateInterval.DropDownStyle = ComboBoxStyle.DropDownList;
        updateInterval.FlatStyle = FlatStyle.Flat;
        updateInterval.AccessibleName = "Update interval";
        updateInterval.FormattingEnabled = true;
        updateInterval.Format += delegate(object sender, ListControlConvertEventArgs e) { e.Value = e.ListItem + " s"; };
        updateInterval.Items.AddRange(UpdateIntervals.Cast<object>().ToArray());
        updateInterval.SelectedItem = DefaultUpdateSeconds;
        updateInterval.Width = 74;
        updateInterval.SelectedIndexChanged += delegate
        {
            ScheduleSample(UpdateSeconds);
            Invalidate();
        };

        diskCutoff.DropDownStyle = ComboBoxStyle.DropDownList;
        diskCutoff.FlatStyle = FlatStyle.Flat;
        diskCutoff.AccessibleName = "Minimum average disk active time";
        diskCutoff.FormattingEnabled = true;
        diskCutoff.Format += delegate(object sender, ListControlConvertEventArgs e) { e.Value = e.ListItem + "%"; };
        diskCutoff.Items.AddRange(new object[] { 5, 10, 15, DefaultDiskCutoff, 35, 50, 75, 90 });
        diskCutoff.SelectedItem = DefaultDiskCutoff;
        diskCutoff.Width = 66;
        diskCutoff.SelectedIndexChanged += DiskFilterChanged;

        transferCutoff.DropDownStyle = ComboBoxStyle.DropDownList;
        transferCutoff.FlatStyle = FlatStyle.Flat;
        transferCutoff.AccessibleName = "Minimum average disk transfer rate";
        var defaultLimit = new TransferLimit(DefaultTransferCutoff, false);
        var rates = new[] { DefaultTransferCutoff, 5, 10, 25, 50 };
        transferCutoff.Items.Add(new TransferLimit(0, false));
        transferCutoff.Items.Add(defaultLimit);
        transferCutoff.Items.AddRange(rates.Skip(1).Select(rate => new TransferLimit(rate, false)).ToArray());
        transferCutoff.Items.AddRange(rates.Select(rate => new TransferLimit(rate, true)).ToArray());
        transferCutoff.SelectedItem = defaultLimit;
        transferCutoff.MaxDropDownItems = transferCutoff.Items.Count;
        transferCutoff.Width = 112;
        transferCutoff.SelectedIndexChanged += DiskFilterChanged;

        processDetails = new CpuProcessDetails(() => UpdateSeconds);
        processDetails.CloseRequested += (_, _) => SetDetails(false);
        diskIoDetails.CloseRequested += (_, _) => SetDetails(false);
        Controls.Add(processDetails);

        groupByMachine.CheckedChanged += (_, _) => SharedOptionsChanged();
        topRows.Width = 60;
        topRows.ValueChanged += (_, _) => SharedOptionsChanged();
        cpuScroll.ValueChanged += (_, _) => Invalidate();
        Controls.Add(cpuScroll);
        UpdateGuestMenu();

        showVirtualDisks.Text = "Show VHDX cards";
        showVirtualDisks.AutoSize = true;
        showVirtualDisks.Checked = true;
        showVirtualDisks.CheckedChanged += DiskFilterChanged;

        diskScope.DropDownStyle = ComboBoxStyle.DropDownList;
        diskScope.FlatStyle = FlatStyle.Flat;
        diskScope.AccessibleName = "Disk scope";
        diskScope.FormattingEnabled = true;
        diskScope.Format += delegate(object sender, ListControlConvertEventArgs e)
        {
            if (e.ListItem is not DiskScope scope)
                return;
            e.Value = DiskScopeLabel(scope);
        };
        diskScope.Items.AddRange(new object[] { DiskScope.MountedAndSpaces, DiskScope.MountedOnly, DiskScope.All });
        diskScope.SelectedItem = DiskScope.MountedAndSpaces;
        diskScope.Width = 144;
        diskScope.DropDownWidth = 150;
        diskScope.SelectedIndexChanged += DiskFilterChanged;

        diskRank.DropDownStyle = ComboBoxStyle.DropDownList;
        diskRank.AccessibleName = "Disk card ranking";
        diskRank.Items.AddRange(new object[] { "Total MiB/s", "Read MiB/s", "Write MiB/s", "Active time %" });
        diskRank.SelectedIndex = 0;
        diskRank.Width = 144;
        diskRank.SelectedIndexChanged += DiskFilterChanged;
        AddSetting(settingsMenu, UpdateLabel, updateInterval);
        topRows.Enabled = groupByMachine.Enabled = false;
        settingsMenu.Closed += (_, _) => ApplyBandInput();
        bandInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            ApplyBandInput();
            settingsMenu.Close();
        };
        diskAttributionGaps.CheckedChanged += (_, _) => ApplyDetailOptions();
        AddSetting(settingsMenu, "Top rows", topRows);
        AddSetting(settingsMenu, "Usage band %", bandInput);
        AddSetting(settingsMenu, null, groupByMachine);
        AddSetting(diskSettingsMenu, "Rank cards by", diskRank);
        AddSetting(diskSettingsMenu, "Disk scope", diskScope);
        AddSetting(diskSettingsMenu, DiskCutoffLabel, diskCutoff);
        AddSetting(diskSettingsMenu, TransferCutoffLabel, transferCutoff);
        AddSetting(diskSettingsMenu, null, showVirtualDisks);
        AddSetting(diskSettingsMenu, null, diskAttributionGaps);
        settingsButton.Click += (_, _) => OpenMenu(settingsMenu, settingsButton);
        diskSettingsButton.Click += (_, _) => OpenMenu(diskSettingsMenu, diskSettingsButton);
        guestOptions.Click += (_, _) => OpenMenu(guestMenu, guestOptions);
        cpuView.CheckedChanged += (_, _) => { if (cpuView.Checked) SwitchView(false); };
        diskView.CheckedChanged += (_, _) => { if (diskView.Checked) SwitchView(true); };
        detailsButton.Click += (_, _) => SetDetails(!detailsOpen);
        find.TextChanged += (_, _) => ApplyDetailOptions();
        cpuView.TextAlign = diskView.TextAlign = ContentAlignment.MiddleCenter;
        cpuView.FlatStyle = diskView.FlatStyle = FlatStyle.Flat;
        foreach (var button in new[] { detailsButton, guestOptions, settingsButton, diskSettingsButton })
            button.FlatStyle = FlatStyle.Flat;
        toolbar.SetBounds(PaddingSize, 8, ClientSize.Width - 2 * PaddingSize, 32);
        toolbar.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        void AddTool(Control control, int width)
        {
            control.Size = new Size(width, 26);
            control.Margin = new Padding(0, 0, 5, 0);
            toolbar.Controls.Add(control);
        }
        AddTool(cpuView, 49);
        AddTool(diskView, 49);
        AddTool(detailsButton, 94);
        AddTool(guestOptions, 100);
        AddTool(settingsButton, 85);
        AddTool(diskSettingsButton, 94);
        AddTool(find, 128);
        find.Margin = new Padding(1, 2, 0, 0);
        toolbar.Layout += (_, _) =>
        {
            int width = Math.Max(100, toolbar.ClientSize.Width - toolbar.Controls.Cast<Control>()
                .Where(control => control != find && control.Visible).Sum(control => control.Width + control.Margin.Horizontal) - 2);
            if (find.Width != width)
                find.Width = width;
        };
        Controls.Add(toolbar);
        description.SetBounds(PaddingSize, 42, ClientSize.Width - 2 * PaddingSize, 24);
        description.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        description.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(description);

        diskGrid.SetBounds(PaddingSize, CardsTop, ClientSize.Width - 2 * PaddingSize, ClientSize.Height - CardsTop - PaddingSize);
        diskGrid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        diskGrid.AutoScroll = true;
        diskGrid.Visible = false;
        diskGrid.SizeChanged += delegate { ResizeDiskCards(); };
        Controls.Add(diskGrid);
        diskIoDetails.LayoutChanged += delegate
        {
            if (!diskIoDetails.HasSelection)
            {
                selectedVirtualDisk = null;
                selectedDisk = null;
                if (showingDisks)
                    RefreshDiskCards(clock.Elapsed.TotalSeconds);
                Invalidate();
            }
            LayoutDetailsRegion();
        };
        Controls.Add(diskIoDetails);
        Resize += delegate { LayoutDetailsRegion(); };

        timer.Tick += ReadSample;
        Shown += delegate
        {
            ReadDisks();
            TryOpenHostCounters();
            ApplyDetailOptions();
            SetDetails(true);
            RefreshGuestChoices();
            lastSampleAt = clock.Elapsed.TotalSeconds;
            nextSampleAt = clock.Elapsed.TotalSeconds + timer.Interval / 1000.0;
            timer.Start();
        };
        FormClosing += delegate
        {
            processDetails.SetOpen(false);
            diskIoDetails.TrackVirtualDisks(Array.Empty<int>(), volumePaths);
            diskIoDetails.SetOpen(false, volumePaths);
        };
        FormClosed += delegate
        {
            timer.Dispose();
            guestMenu.Dispose();
            settingsMenu.Dispose();
            diskSettingsMenu.Dispose();
            guestCounters?.Dispose();
            hostCounters?.Dispose();
            diskCardTips.Dispose();
            if (diskCounters != null)
                diskCounters.Dispose();
        };
    }

    private static void OpenMenu(ContextMenuStrip menu, Control button) => menu.Show(button, new Point(0, button.Height));

    private static void AddSetting(ContextMenuStrip menu, string text, Control control)
    {
        var row = new Panel { Width = 260, Height = 30 };
        if (text != null)
            row.Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(4, 6) });
        control.Location = new Point(text == null ? 4 : 108, 3);
        row.Controls.Add(control);
        menu.Items.Add(new ToolStripControlHost(row) { Margin = Padding.Empty, Padding = Padding.Empty });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        darkMode = null;
        RefreshTheme();
    }

    private void RefreshTheme()
    {
        bool useDark = Equals(Registry.GetValue(WindowsPersonalization, "AppsUseLightTheme", 1), 0);
        if (darkMode == useDark)
            return;

        darkMode = useDark;
        BackColor = useDark ? Color.FromArgb(16, 24, 38) : Color.FromArgb(241, 245, 250);
        ForeColor = useDark ? Color.FromArgb(235, 242, 252) : Color.FromArgb(19, 37, 61);
        idleTile = useDark ? Color.FromArgb(25, 35, 51) : Color.FromArgb(253, 254, 255);
        busyTile = useDark ? Color.FromArgb(37, 98, 148) : Color.FromArgb(125, 183, 239);
        lineColor = useDark ? Color.FromArgb(190, 235, 255) : Color.FromArgb(8, 69, 140);
        writeLineColor = useDark ? Color.FromArgb(227, 176, 255) : Color.FromArgb(119, 51, 142);
        updateInterval.BackColor = idleTile;
        updateInterval.ForeColor = ForeColor;
        diskCutoff.BackColor = idleTile;
        diskCutoff.ForeColor = ForeColor;
        transferCutoff.BackColor = idleTile;
        transferCutoff.ForeColor = ForeColor;
        guestOptions.BackColor = topRows.BackColor = bandInput.BackColor = idleTile;
        guestOptions.ForeColor = topRows.ForeColor = bandInput.ForeColor = ForeColor;
        foreach (Control control in toolbar.Controls)
        {
            control.BackColor = idleTile;
            control.ForeColor = ForeColor;
        }
        foreach (var radio in new[] { cpuView, diskView })
            radio.FlatAppearance.CheckedBackColor = Blend(idleTile, lineColor, 0.22);
        foreach (var menu in new[] { settingsMenu, diskSettingsMenu, guestMenu })
        {
            menu.BackColor = idleTile;
            menu.ForeColor = ForeColor;
            foreach (var host in menu.Items.OfType<ToolStripControlHost>())
            {
                host.Control.BackColor = idleTile;
                host.Control.ForeColor = ForeColor;
            }
        }
        diskRank.BackColor = idleTile;
        diskRank.ForeColor = ForeColor;
        description.ForeColor = Blend(ForeColor, BackColor, 0.3);
        processDetails.ApplyColors(BackColor, ForeColor, idleTile);
        diskScope.BackColor = idleTile;
        diskScope.ForeColor = ForeColor;
        diskGrid.BackColor = BackColor;
        diskIoDetails.ApplyColors(BackColor, ForeColor, idleTile);

        int titleBarDark = useDark ? 1 : 0;
        DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref titleBarDark, sizeof(int));
    }

    private static Color Blend(Color from, Color to, double fraction)
    {
        return Color.FromArgb(
            (int)Math.Round(from.R + (to.R - from.R) * fraction),
            (int)Math.Round(from.G + (to.G - from.G) * fraction),
            (int)Math.Round(from.B + (to.B - from.B) * fraction));
    }

    private static string Percent(double? value)
    {
        return value.HasValue ? value.Value.ToString("0") + "%" : "--";
    }

    private void ReadSample(object sender, EventArgs args)
    {
        RefreshTheme();
        double now = clock.Elapsed.TotalSeconds;
        var sample = new Sample
        {
            Time = now,
            StartTime = lastSampleAt,
            StartsNewSegment = now > nextSampleAt + SamplingGraceSeconds
        };
        lastSampleAt = now;
        ScheduleSample(UpdateSeconds);
        if (now >= nextGuestRefresh)
            RefreshGuestChoices();
        try
        {
            var values = counters.Read(out sample.Total);
            if (processors.Length != values.Count || values.Keys.Except(processors).Any())
            {
                processors = processors.Union(values.Keys)
                    .OrderBy(id => int.Parse(id.Split(',')[0]))
                    .ThenBy(id => int.Parse(id.Split(',')[1]))
                    .ToArray();
            }
            sample.Values = values;
            error = null;
        }
        catch (InvalidOperationException e)
        {
            error = e.Message;
        }
        if (selectedGuests.Count > 0)
        {
            try
            {
                if (guestCounters == null)
                    guestCounters = new PdhQuery(@"\Hyper-V Hypervisor Virtual Processor(*)\% Guest Run Time");
                else
                {
                    guestCounters.Collect();
                    sample.GuestValues = guestCounters.Read(0);
                }
            }
            catch (InvalidOperationException)
            {
                guestCounters?.Dispose();
                guestCounters = null;
            }
        }
        try { sample.HostCpu = hostCounters?.Read(); }
        catch (InvalidOperationException) { }
        sample.Disks = ReadDisks();
        history.Add(sample);
        history.RemoveAll(entry => entry.Time < now - MaxHistorySeconds);
        if (showingDisks)
        {
            if (diskIoDetails.HasSelection && sample.Disks != null &&
                (selectedVirtualDisk != null
                    ? !sample.Disks.Values.Any(disk => disk.Number == selectedVirtualDisk.Latest.Number)
                    : !sample.Disks.ContainsKey(selectedDisk)))
                diskIoDetails.SelectAllDisks();
            RefreshDiskCards(now, sample);
        }
        Invalidate();
    }

    private Dictionary<string, DiskReading> ReadDisks()
    {
        try
        {
            diskError = null;
            if (diskCounters == null)
            {
                diskCounters = new DiskCounters();
                return null;
            }
            return diskCounters.Read();
        }
        catch (InvalidOperationException e)
        {
            diskError = e.Message;
            return null;
        }
    }

    private void ScheduleSample(int seconds)
    {
        timer.Interval = seconds * 1000;
        nextSampleAt = clock.Elapsed.TotalSeconds + seconds;
    }

    private void SetDetails(bool open)
    {
        detailsOpen = open;
        detailsButton.Text = open ? "Hide details" : "Show details";
        processDetails.SetOpen(open && !showingDisks);
        diskIoDetails.SetOpen(open && showingDisks, volumePaths);
        LayoutDetailsRegion();
        Invalidate();
    }

    private async void RefreshGuestChoices()
    {
        if (loadingGuests)
            return;
        loadingGuests = true;
        nextGuestRefresh = clock.Elapsed.TotalSeconds + 15;
        try
        {
            var guests = await Task.Run(CpuProcessSampler.ReadTargets);
            if (IsDisposed || availableGuests.SequenceEqual(guests))
                return;
            availableGuests = guests;
            selectedGuests.Clear();
            selectedGuests.UnionWith(guests.Where(guest => !excludedGuests.Contains(guest.Name)));
            UpdateGuestMenu();
            SharedOptionsChanged();
        }
        catch (Exception) { } // async void: the guest list stays as it was; nothing may escape to crash the viewer
        finally { loadingGuests = false; }
    }

    private void UpdateGuestMenu()
    {
        while (guestMenu.Items.Count > 0)
        {
            var item = guestMenu.Items[0];
            guestMenu.Items.RemoveAt(0);
            item.Dispose();
        }
        if (availableGuests.Length == 0)
            guestMenu.Items.Add(new ToolStripMenuItem("No running guests") { Enabled = false });
        foreach (var guest in availableGuests)
        {
            var item = new ToolStripMenuItem(guest.Name) { CheckOnClick = true, Checked = selectedGuests.Contains(guest) };
            item.CheckedChanged += (_, _) =>
            {
                if (item.Checked)
                {
                    selectedGuests.Add(guest);
                    excludedGuests.Remove(guest.Name);
                }
                else
                {
                    selectedGuests.Remove(guest);
                    excludedGuests.Add(guest.Name);
                }
                SharedOptionsChanged();
            };
            guestMenu.Items.Add(item);
        }
    }

    private void ApplyDetailOptions()
    {
        var guests = availableGuests.Where(selectedGuests.Contains).ToArray();
        string filter = find.Text.Trim();
        processDetails.SetOptions(guests, groupByMachine.Checked, (int)topRows.Value, filter, bandWidth);
        diskIoDetails.SetOptions(guests, groupByMachine.Checked, (int)topRows.Value, filter, bandWidth, diskAttributionGaps.Checked);
    }

    private void ApplyBandInput()
    {
        string text = bandInput.Text.Trim();
        int? width = null;
        if (text.Length > 0)
        {
            if (!int.TryParse(text, out int value) || value is < 1 or > 100)
            {
                bandInput.Text = bandWidth?.ToString() ?? "";
                return;
            }
            width = value;
        }
        bandInput.Text = width?.ToString() ?? "";
        if (bandWidth == width)
            return;
        bandWidth = width;
        topRows.Enabled = groupByMachine.Enabled = !width.HasValue;
        ApplyDetailOptions();
    }

    private void SharedOptionsChanged()
    {
        guestOptions.Text = availableGuests.Length == 0 ? "Guests ▾" : $"Guests {selectedGuests.Count}/{availableGuests.Length} ▾";
        ApplyDetailOptions();
        if (selectedGuests.Count == 0)
        {
            guestCounters?.Dispose();
            guestCounters = null;
        }
        if (showingDisks)
            RefreshDiskCards(clock.Elapsed.TotalSeconds);
        Invalidate();
    }

    private void TryOpenHostCounters()
    {
        try { hostCounters = new HyperVHostCounters(); }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
            or System.Text.Json.JsonException or KeyNotFoundException or ArgumentException)
        {
            hostCountersFailure = e.Message; // permanent for this run, unlike a null Read(), so it is shown
        }
    }

    private async void SwitchView(bool disks)
    {
        if (showingDisks == disks)
            return;
        showingDisks = disks;
        diskSettingsButton.Visible = disks;
        cpuScroll.Visible = false;
        SetDetails(detailsOpen);
        if (disks)
            RefreshDiskCards(clock.Elapsed.TotalSeconds);
        else
        {
            diskIoDetails.TrackVirtualDisks(Array.Empty<int>(), volumePaths);
            diskGrid.Hide();
        }

        if (!disks || loadingDiskNames || diskIdentities.Count > 0)
            return;
        loadingDiskNames = true;
        try
        {
            diskIdentities = await Task.Run(LoadDiskIdentities);
            volumePaths = new VolumePathIndex(diskIdentities.Values
                .Where(item => item.Volumes != null).SelectMany(item => item.Volumes));
        }
        catch (Exception) { } // async void: disks keep their generic names; nothing may escape to crash the viewer
        finally
        {
            loadingDiskNames = false;
            if (!IsDisposed && showingDisks)
                DiskFilterChanged(this, EventArgs.Empty);
        }
    }

    private void DiskFilterChanged(object sender, EventArgs e)
    {
        if (!showingDisks)
            return;
        RefreshDiskCards(clock.Elapsed.TotalSeconds);
        Invalidate();
    }

    private static Dictionary<int, DiskIdentity> LoadDiskIdentities()
    {
        const string storageNamespace = @"root\Microsoft\Windows\Storage";
        var identities = new Dictionary<int, DiskIdentity>();
        using (var search = new ManagementObjectSearcher(storageNamespace, "SELECT Number, FriendlyName, BusType FROM MSFT_Disk"))
        using (var disks = search.Get())
        {
            foreach (ManagementObject disk in disks)
                using (disk)
                {
                    var name = disk["FriendlyName"] as string;
                    if (disk["Number"] != null)
                    {
                        int busType = Convert.ToInt32(disk["BusType"]);
                        identities[Convert.ToInt32(disk["Number"])] =
                            new DiskIdentity(name, DiskKind(busType), busType == StorageSpacesBusType);
                    }
                }
        }

        try
        {
            var localNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var search = new ManagementObjectSearcher(storageNamespace, "SELECT * FROM MSFT_StorageNode"))
            using (var nodes = search.Get())
            {
                foreach (ManagementObject node in nodes)
                    using (node)
                    {
                        string name = node["Name"] as string;
                        if (name != null && name.Split('.')[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                            localNodes.Add(node.Path.RelativePath);
                    }
            }
            using (var search = new ManagementObjectSearcher(storageNamespace,
                "SELECT DiskNumber, PhysicalDisk, StorageNode FROM MSFT_StorageNodeToPhysicalDisk"))
            using (var links = search.Get())
            {
                foreach (ManagementObject link in links)
                    using (link)
                    {
                        var nodePath = link["StorageNode"] as string;
                        if (link["DiskNumber"] == null || nodePath == null ||
                            !localNodes.Contains(new ManagementPath(nodePath).RelativePath))
                            continue;
                        int number = Convert.ToInt32(link["DiskNumber"]);
                        var path = link["PhysicalDisk"] as string;
                        if (identities.ContainsKey(number) || string.IsNullOrWhiteSpace(path))
                            continue;
                        using (var disk = new ManagementObject(path))
                        {
                            var model = disk["FriendlyName"] as string;
                            identities.Add(number, new DiskIdentity(model, "Physical drive"));
                        }
                    }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (COMException) { }

        try
        {
            var volumesByDisk = new Dictionary<int, List<DiskVolume>>();
            using (var search = new ManagementObjectSearcher(storageNamespace, "SELECT DiskNumber, AccessPaths FROM MSFT_Partition"))
            using (var partitions = search.Get())
            {
                foreach (ManagementObject partition in partitions)
                    using (partition)
                    {
                        var accessPaths = partition["AccessPaths"] as string[];
                        if (partition["DiskNumber"] == null || accessPaths == null)
                            continue;

                        var paths = accessPaths.Select(NormalizeAccessPath).Where(path => path != null)
                            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length)
                            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                        int number = Convert.ToInt32(partition["DiskNumber"]);
                        var volume = DiskVolume.Create(number, paths, accessPaths);
                        if (volume.DisplayRoot == null)
                            continue;

                        List<DiskVolume> volumes;
                        if (!volumesByDisk.TryGetValue(number, out volumes))
                        {
                            volumes = new List<DiskVolume>();
                            volumesByDisk.Add(number, volumes);
                        }
                        volumes.Add(volume);
                    }
            }

            foreach (var identity in identities.Values)
                identity.AccessPaths = new string[0];
            foreach (var entry in volumesByDisk)
            {
                DiskIdentity identity;
                if (!identities.TryGetValue(entry.Key, out identity))
                {
                    identity = new DiskIdentity(null, "Windows disk");
                    identities.Add(entry.Key, identity);
                }
                identity.Volumes = entry.Value.ToArray();
                identity.AccessPaths = entry.Value.SelectMany(volume => volume.Paths)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (COMException) { }

        return identities;
    }

    private static string NormalizeAccessPath(string path)
    {
        if (path == null)
            return null;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.Substring(4);
        return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\' ? path : null;
    }

    private static string DiskKind(int busType)
    {
        // MSFT_Disk reports values from STORAGE_BUS_TYPE.
        switch (busType)
        {
            case StorageSpacesBusType: return "Storage Spaces virtual disk";
            case 14:
            case 15: return "Virtual disk";
            case 17: return "NVMe drive";
            case 7: return "USB drive";
            default: return "Windows disk";
        }
    }

    private DiskSummary[] ReadDiskSummaries(double now)
    {
        var latest = history.LastOrDefault();
        if (latest == null || latest.Disks == null)
            return new DiskSummary[0];

        var summaries = latest.Disks
            .ToDictionary(entry => entry.Key, entry => new DiskSummary { Id = entry.Key, Latest = entry.Value },
                StringComparer.OrdinalIgnoreCase);
        double cutoff = now - DiskActivityWindow;
        for (int i = history.Count - 1; i >= 0 && history[i].Time > cutoff; i--)
        {
            Sample sample = history[i];
            if (sample.Disks == null || sample.StartsNewSegment)
                continue;
            double seconds = sample.Time - Math.Max(sample.StartTime, cutoff);
            foreach (var entry in sample.Disks)
            {
                DiskSummary summary;
                if (!summaries.TryGetValue(entry.Key, out summary))
                    continue;
                summary.Coverage += seconds;
                summary.ActiveSum += seconds * entry.Value.Active;
                summary.ReadSum += seconds * entry.Value.ReadMiB;
                summary.WriteSum += seconds * entry.Value.WriteMiB;
            }
        }

        return summaries.Values.ToArray();
    }

    private bool MeetsDiskFilters(DiskSummary disk) =>
        IncludesDisk(SelectedDiskScope, disk.Latest.Number) && disk.Coverage >= MinimumDiskCoverage &&
        disk.AverageActive >= (int)diskCutoff.SelectedItem &&
        disk.AverageTransfer(SelectedTransferLimit.WritesOnly) >= SelectedTransferLimit.MinimumMiB;

    private double DiskRankValue(DiskSummary disk) => diskRank.SelectedIndex switch
    {
        1 => disk.VirtualDisk == null ? disk.ReadSum / disk.Coverage : disk.Latest.ReadMiB,
        2 => disk.Rate(writesOnly: true),
        3 => disk.VirtualDisk == null ? disk.AverageActive : -1,
        _ => disk.Rate()
    };

    private static string DiskScopeLabel(DiskScope scope) => scope switch
    {
        DiskScope.MountedAndSpaces => "Mounts + Spaces",
        DiskScope.MountedOnly => "Mounted only",
        _ => "All disks"
    };

    private DiskScope ActiveDiskScope => diskIdentities.Count == 0 ? DiskScope.All : SelectedDiskScope;

    private bool IncludesDisk(DiskScope scope, int diskNumber)
    {
        if (scope == DiskScope.All)
            return true;
        DiskIdentity identity;
        if (!diskIdentities.TryGetValue(diskNumber, out identity))
            return false;
        return (scope == DiskScope.MountedAndSpaces && identity.IsStorageSpaces) ||
            (identity.AccessPaths != null && identity.AccessPaths.Length > 0);
    }

    private void RefreshDiskCards(double now, Sample sample = null)
    {
        var scope = ActiveDiskScope;
        diskIoDetails.SetDiskScope(scope == DiskScope.All ? null :
            diskIdentities.Keys.Where(number => IncludesDisk(scope, number)).ToHashSet(), DiskScopeLabel(scope));
        var summaries = ReadDiskSummaries(now);
        var physical = summaries.Where(MeetsDiskFilters).ToArray();
        var active = physical.Concat(FindVirtualDisks(summaries, sample))
            .OrderByDescending(DiskRankValue).ThenBy(disk => disk.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (selectedDisk != null && !active.Any(disk => disk.Id == selectedDisk))
        {
            var pinned = selectedVirtualDisk ?? summaries.FirstOrDefault(disk => disk.Id == selectedDisk);
            if (pinned != null)
            {
                pinned.BelowFilter = true;
                active = active.Prepend(pinned).ToArray();
            }
        }
        bool grouped = groupByMachine.Checked && active.Any(disk => disk.VirtualDisk != null);
        if (grouped)
            active = active.GroupBy(DiskCardGroup).SelectMany(group => group).ToArray();
        if (selectedVirtualDisk != null)
            selectedVirtualDisk = active.FirstOrDefault(disk => disk.Id == selectedDisk) ?? selectedVirtualDisk;

        var cards = diskGrid.Controls.OfType<Button>().ToArray();
        var layout = active.Select(disk => grouped ? DiskCardGroup(disk) + "\0" + disk.Id : disk.Id).ToArray();
        if (!diskCardLayout.SequenceEqual(layout))
        {
            var existing = cards.ToDictionary(card => ((DiskSummary)card.Tag).Id);
            var activeIds = new HashSet<string>(active.Select(disk => disk.Id));
            diskGrid.SuspendLayout();
            try
            {
                foreach (var heading in diskGrid.Controls.OfType<Label>().ToArray())
                    heading.Dispose();
                foreach (var entry in existing)
                    if (!activeIds.Contains(entry.Key))
                    {
                        diskCardTips.SetToolTip(entry.Value, null);
                        entry.Value.Dispose();
                    }
                cards = active.Select(disk =>
                {
                    Button card;
                    if (existing.TryGetValue(disk.Id, out card))
                        return card;

                    card = new Button
                    {
                        Tag = disk,
                        Margin = new Padding(0, 0, CardGap, CardGap),
                        FlatStyle = FlatStyle.Flat,
                        Height = 112
                    };
                    card.Paint += DrawDiskCard;
                    card.Click += (_, _) => SelectDiskCard((DiskSummary)card.Tag);
                    diskGrid.Controls.Add(card);
                    return card;
                }).ToArray();
                int index = 0;
                string previous = null;
                for (int i = 0; i < cards.Length; i++)
                {
                    string section = grouped ? DiskCardGroup(active[i]) : null;
                    if (section != previous)
                    {
                        var heading = new Label
                        {
                            Text = section, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
                        };
                        diskGrid.Controls.Add(heading);
                        diskGrid.Controls.SetChildIndex(heading, index++);
                        diskGrid.SetFlowBreak(heading, true);
                        previous = section;
                    }
                    diskGrid.Controls.SetChildIndex(cards[i], index++);
                }
                ResizeDiskCards();
                diskCardLayout = layout;
            }
            finally
            {
                diskGrid.ResumeLayout();
            }
        }

        diskGrid.Visible = active.Length > 0;
        for (int i = 0; i < cards.Length; i++)
        {
            DiskSummary disk = active[i];
            cards[i].Tag = disk;
            if (disk.VirtualDisk != null)
            {
                cards[i].AccessibleName = System.IO.Path.GetFileName(disk.VirtualDisk.Path) + ", " + VirtualDiskLabel(disk);
                string tip = disk.VirtualDisk.Path + Environment.NewLine + VirtualDiskLabel(disk);
                if (diskCardTips.GetToolTip(cards[i]) != tip)
                    diskCardTips.SetToolTip(cards[i], tip);
                cards[i].Invalidate();
                continue;
            }
            string label = DiskLabel(disk.Id);
            DiskIdentity identity;
            if (diskIdentities.TryGetValue(disk.Latest.Number, out identity))
            {
                var lines = new List<string>();
                if (identity.AccessPaths != null)
                {
                    lines.AddRange(identity.AccessPaths);
                    if (identity.AccessPaths.Length == 0)
                        lines.Add("No drive letter or folder mount");
                }
                if (!string.IsNullOrWhiteSpace(identity.Name))
                    lines.Add(identity.Name);
                lines.Add(identity.Kind + " | " + label);
                cards[i].AccessibleName = string.Join(", ", lines);
                string tip = string.Join(Environment.NewLine, lines);
                if (diskCardTips.GetToolTip(cards[i]) != tip)
                    diskCardTips.SetToolTip(cards[i], tip);
            }
            else
            {
                cards[i].AccessibleName = label;
                diskCardTips.SetToolTip(cards[i], null);
            }
            cards[i].Invalidate();
        }
    }

    private static string DiskCardGroup(DiskSummary disk) => disk.VirtualDisk == null
        ? Environment.MachineName + " disks"
        : disk.VirtualOwner?.Label is string owner ? owner + " VHDX files" : "VHDX files";

    private void SelectDiskCard(DiskSummary picked)
    {
        selectedDisk = picked.Id;
        selectedVirtualDisk = picked.VirtualDisk == null ? null : picked;
        if (picked.VirtualDisk != null)
            diskIoDetails.SelectVirtualDisk(picked.Latest.Number, picked.VirtualDisk.Path, volumePaths);
        else
        {
            diskIdentities.TryGetValue(picked.Latest.Number, out var identity);
            string label = identity?.AccessPaths?.FirstOrDefault() ??
                (string.IsNullOrWhiteSpace(identity?.Name) ? DiskLabel(picked.Id) :
                    identity.Name + "  |  " + DiskLabel(picked.Id));
            diskIoDetails.SelectDisk(picked.Latest.Number, label, volumePaths);
        }
        SetDetails(true);
        diskGrid.Invalidate(true);
        Invalidate();
    }

    private DiskSummary[] FindVirtualDisks(DiskSummary[] disks, Sample sample)
    {
        int[] numbers = showVirtualDisks.Checked
            ? disks.Where(disk => IncludesDisk(SelectedDiskScope, disk.Latest.Number))
                .Select(disk => disk.Latest.Number).Distinct().Order().ToArray()
            : Array.Empty<int>();
        diskIoDetails.TrackVirtualDisks(numbers, volumePaths);
        virtualDiskStatus = null;
        if (numbers.Length == 0)
            return Array.Empty<DiskSummary>();

        var snapshot = diskIoDetails.ReadVirtualDisks();
        virtualDiskStatus = snapshot?.Status ??
            (snapshot is { Entries.Length: > 0 } ? null : "Waiting for VHDX I/O");
        if (snapshot is { EventsLost: > 0 })
            virtualDiskStatus = (virtualDiskStatus == null ? "" : virtualDiskStatus + "  |  ") + "VHDX trace events dropped";
        if (snapshot is not { Seconds: > 0 })
            return Array.Empty<DiskSummary>();

        var owners = diskIoDetails.VirtualDiskOwners;
        var limit = SelectedTransferLimit;
        var virtualDisks = snapshot.Entries.GroupBy(entry => VmDiskIo.Normalize(entry.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var largest = group.OrderByDescending(entry => entry.Totals.TotalBytes).First();
                var total = new FileIoTotals();
                foreach (var entry in group)
                    total.Add(entry.Totals);
                return new DiskSummary
                {
                    Id = "vhdx:" + group.Key.ToUpperInvariant(),
                    VirtualDisk = largest with { Totals = total },
                    VirtualOwner = owners?.GetValueOrDefault(group.Key),
                    Coverage = snapshot.Seconds,
                    Latest = new DiskReading
                    {
                        Number = largest.DiskNumber,
                        ReadMiB = total.ReadBytes / (1048576 * snapshot.Seconds),
                        WriteMiB = total.WriteBytes / (1048576 * snapshot.Seconds)
                    }
                };
            }).ToArray();
        if (sample != null)
            sample.VirtualDisks = virtualDisks.ToDictionary(disk => disk.Id, StringComparer.OrdinalIgnoreCase);
        return virtualDisks.Where(disk => disk.Rate(limit.WritesOnly) >= limit.MinimumMiB)
            .OrderByDescending(DiskRankValue)
            .ThenBy(disk => disk.Id, StringComparer.Ordinal).Take(12).ToArray();
    }

    private static string VirtualDiskLabel(DiskSummary disk) =>
        (disk.VirtualOwner?.Label ?? "VHDX") + "  |  Host disk " + disk.Latest.Number;

    private void ResizeDiskCards()
    {
        int width = diskGrid.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        int columns = Math.Max(1, width / (232 + CardGap));
        foreach (Control card in diskGrid.Controls)
            card.Width = Math.Max(1, card is Label ? width : width / columns - CardGap);
    }

    private void LayoutDetailsRegion()
    {
        int available = Math.Max(0, ClientSize.Height - CardsTop - PaddingSize);
        bool showCpuDetails = detailsOpen && !showingDisks;
        bool showDiskDetails = detailsOpen && showingDisks;
        Control details = showCpuDetails ? processDetails : showDiskDetails ? diskIoDetails : null;
        if (details != null)
        {
            int height = Math.Min(available, showCpuDetails ? Math.Min(345, available / 2) : diskIoDetails.HeightFor(available));
            details.SetBounds(PaddingSize, CardsTop + available - height,
                ClientSize.Width - 2 * PaddingSize, height);
            diskGrid.Height = Math.Max(0, available - height - CardGap);
        }
        else
            diskGrid.Height = available;
        processDetails.Visible = showCpuDetails;
        diskIoDetails.Visible = showDiskDetails;
    }

    private static string DiskLabel(string id)
    {
        return "Disk " + id.ToUpperInvariant();
    }

    private static string TransferRates(DiskReading disk)
    {
        return "Read " + disk.ReadMiB.ToString("0.##") + " / Write " + disk.WriteMiB.ToString("0.##") + " MiB/s";
    }

    private HistoryRange VisibleHistory()
    {
        int seconds = HistoryIntervals * UpdateSeconds;
        double start = clock.Elapsed.TotalSeconds - seconds;
        int first = history.FindIndex(sample => sample.Time >= start);
        return new HistoryRange { Start = start, Seconds = seconds, FirstSample = first < 0 ? history.Count : first };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        double end = clock.Elapsed.TotalSeconds;
        HistoryRange range = VisibleHistory();
        Sample latest = history.LastOrDefault();
        if (end > nextSampleAt + SamplingGraceSeconds)
            latest = null;
        var physical = showingDisks ? null : history.LastOrDefault(sample => sample.HostCpu != null)?.HostCpu;

        using (var muted = new SolidBrush(Blend(ForeColor, BackColor, 0.3)))
        using (var card = new SolidBrush(idleTile))
        using (var grid = new Pen(ForeColor))
        using (var line = new Pen(lineColor, 1.5f))
        {
            var selected = latest?.GetDisk(selectedDisk);
            string name = selectedVirtualDisk != null ? System.IO.Path.GetFileName(selectedVirtualDisk.VirtualDisk.Path) :
                selected is { } disk && diskIdentities.TryGetValue(disk.Number, out var identity)
                    ? identity.AccessPaths?.FirstOrDefault() ?? DiskLabel(selectedDisk)
                    : selectedDisk == null ? DiskScopeLabel(ActiveDiskScope) : DiskLabel(selectedDisk);
            var diskCards = diskGrid.Controls.OfType<Button>().Select(button => (DiskSummary)button.Tag).ToArray();
            int virtualCount = diskCards.Count(disk => disk.VirtualDisk != null);
            string detail = showingDisks
                ? diskError ?? (name + (selected.HasValue ? "  ·  " + Percent(selected.Value.Active) + " active" : "") +
                    "  |  " + (diskCards.Length - virtualCount) + " disk cards  |  " + DiskActivityWindow + " s average" +
                    (virtualCount > 0 ? "  |  " + virtualCount + " VHDX files" : "") +
                    (diskCards.Length == 0 ? "  |  No cards meet the filters yet" : "") +
                    (virtualDiskStatus == null ? "" : "  |  " + virtualDiskStatus))
                : error ?? (processors.Length == 0 ? "Collecting the first sample..." :
                    "Overall " + Percent(latest?.HostCpu?.Overall.Total ?? latest?.Total) + "  |  " +
                    (physical?.Processors.Count ?? processors.Length) + " logical processors  |  Last " + range.Duration +
                    (hostCountersFailure == null ? "" : "  |  Hyper-V CPU split unavailable: " + hostCountersFailure));
            if (description.Text != detail)
                description.Text = detail;

            int width = ClientSize.Width - 2 * PaddingSize;
            g.FillRectangle(card, PaddingSize, ChartTop, width, OverallPanelHeight);
            grid.Color = Blend(card.Color, ForeColor, 0.22);
            if (showingDisks)
            {
                DrawDiskDetail(g, latest, muted, grid, line, width, range);
                return;
            }

            using var vmLine = new Pen(writeLineColor, line.Width);
            DrawCpuOverview(g, latest, range, width, physical != null, muted, grid, line, vmLine);
            DrawCpuTiles(g, latest, range, width, physical, card, grid, line, vmLine);
        }
    }

    private void DrawCpuOverview(Graphics g, Sample latest, HistoryRange range, int width, bool split,
        SolidBrush muted, Pen grid, Pen line, Pen vmLine)
    {
        var current = latest?.HostCpu?.Overall;
        float statsWidth = split
            ? DrawOverviewStats(g, muted, ("Windows CPU", Percent(current?.Windows), "100%", line.Color),
                ("All Guests", Percent(current?.VirtualMachines), "100%", vmLine.Color),
                ("Hyper-V overhead", Percent(current?.Hypervisor), "100%", null))
            : DrawOverviewStats(g, muted, ("Windows CPU", Percent(latest?.Total), "100%", line.Color));
        var chart = OverviewChart(width, statsWidth);
        DrawChartGrid(g, chart, grid);
        if (split)
        {
            using var overhead = new Pen(muted.Color) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            DrawHistory(g, chart, line, range, sample => sample.HostCpu?.Overall.Windows);
            DrawHistory(g, chart, vmLine, range, sample => sample.HostCpu?.Overall.VirtualMachines);
            DrawHistory(g, chart, overhead, range, sample => sample.HostCpu?.Overall.Hypervisor);
        }
        else
            DrawHistory(g, chart, line, range, sample => sample.Total);
        DrawAxes(g, chart, muted, range.Duration, 100, "%");
    }

    private float DrawOverviewStats(Graphics g, SolidBrush muted,
        params (string Label, string Value, string MaxValue, Color? Color)[] stats)
    {
        using var primary = new Font(Font.FontFamily, 18, FontStyle.Bold);
        using var secondary = new Font(Font.FontFamily, 15, FontStyle.Bold);
        Font[] fonts = { primary, secondary, Font };
        const int inset = 12;
        const int valueGap = 12;
        float contentWidth = stats.Select((stat, i) => g.MeasureString(stat.Label, fonts[i]).Width + valueGap +
            Math.Max(g.MeasureString(stat.Value, fonts[i]).Width, g.MeasureString(stat.MaxValue, fonts[i]).Width)).Max();
        float rowHeight = (OverallPanelHeight - 12f) / fonts.Length;
        float left = PaddingSize + inset;
        float right = left + contentWidth;
        for (int i = 0; i < stats.Length; i++)
        {
            var stat = stats[i];
            var font = fonts[i];
            using var seriesBrush = stat.Color is Color color ? new SolidBrush(color) : null;
            var brush = seriesBrush ?? muted;
            float y = ChartTop + 6 + (i + 0.5f) * rowHeight - font.GetHeight(g) / 2;
            g.DrawString(stat.Label, font, brush, left, y);
            g.DrawString(stat.Value, font, brush, right - g.MeasureString(stat.Value, font).Width, y);
        }
        return contentWidth + inset * 2;
    }

    private static RectangleF OverviewChart(int width, float statsWidth, float leftAxis = 48, float rightAxis = 12) =>
        new(PaddingSize + statsWidth + leftAxis, ChartTop + 12,
            Math.Max(1, width - statsWidth - leftAxis - rightAxis), OverallPanelHeight - 34);

    private sealed record CpuTile(string Machine, string Processor, string GuestCounter = null, int? LogicalProcessor = null)
    {
        public double? Read(Sample sample) => LogicalProcessor is int logical
            ? sample?.GetHostCpu(logical)?.Windows
            : GuestCounter == null ? sample?.GetUtilization(Processor) : sample?.GetGuestUtilization(GuestCounter);

        public double? ReadVmTime(Sample sample) =>
            LogicalProcessor is int logical ? sample?.GetHostCpu(logical)?.VirtualMachines : null;
    }

    private void DrawCpuTiles(Graphics g, Sample latest, HistoryRange range, int width, HyperVHostSample physical,
        SolidBrush card, Pen grid, Pen line, Pen vmLine)
    {
        int height = diskGrid.Height;
        if (processors.Length == 0 || height <= 0)
            return;
        var source = history.LastOrDefault(sample => sample.GuestValues != null)?.GuestValues;
        var hostTiles = physical == null ? processors.Select(id => new CpuTile(Environment.MachineName, id)).ToArray()
            : physical.Processors.Keys.Order().Select(index => new CpuTile(Environment.MachineName,
                index.ToString(), LogicalProcessor: index)).ToArray();
        var sections = new List<(string Title, CpuTile[] Tiles)>
        {
            (Environment.MachineName + " logical processors", hostTiles)
        };
        foreach (var guest in availableGuests.Where(selectedGuests.Contains))
        {
            string prefix = guest.CounterPrefix;
            var tiles = source == null || prefix == null ? Array.Empty<CpuTile>() :
                source.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(key.AsSpan(prefix.Length), out _))
                    .OrderBy(key => int.Parse(key.AsSpan(prefix.Length)))
                    .Select(key => new CpuTile(guest.Name, key[prefix.Length..], key)).ToArray();
            sections.Add((guest.Name + " virtual processors" + (tiles.Length == 0 ? "  |  Waiting for counters" : ""), tiles));
        }

        bool grouped = groupByMachine.Checked && sections.Count > 1;
        bool showSource = !grouped && sections.Count > 1;
        if (!grouped)
            sections = new() { (null, sections.SelectMany(section => section.Tiles).ToArray()) };
        int count = sections.Sum(section => section.Tiles.Length);
        int header = grouped ? 24 : 0;
        int minHeight = showSource || physical != null ? 52 : 38;
        int columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count * width / (height * 1.65))),
            1, Math.Max(1, width / (76 + CardGap)));
        int rows = sections.Sum(section => (section.Tiles.Length + columns - 1) / columns);
        int extent = rows * (minHeight + CardGap) + sections.Count * header - CardGap;
        bool scroll = extent > height;
        if (scroll)
        {
            width -= SystemInformation.VerticalScrollBarWidth + CardGap;
            columns = Math.Min(columns, Math.Max(1, width / (76 + CardGap)));
            rows = sections.Sum(section => (section.Tiles.Length + columns - 1) / columns);
            extent = rows * (minHeight + CardGap) + sections.Count * header - CardGap;
            cpuScroll.SetBounds(PaddingSize + width + CardGap, CardsTop, SystemInformation.VerticalScrollBarWidth, height);
            cpuScroll.LargeChange = height;
            cpuScroll.SmallChange = minHeight + CardGap;
            cpuScroll.Maximum = extent - 1;
            if (cpuScroll.Value > extent - height)
                cpuScroll.Value = extent - height;
        }
        else if (cpuScroll.Value != 0)
            cpuScroll.Value = 0;
        cpuScroll.Visible = scroll;

        float cellWidth = (width - (columns - 1) * CardGap) / (float)columns;
        float cellHeight = Math.Max(minHeight, (height - sections.Count * header - (rows - 1) * CardGap) / (float)rows);
        var saved = g.Save();
        g.SetClip(new Rectangle(PaddingSize, CardsTop, width, height), System.Drawing.Drawing2D.CombineMode.Intersect);
        float y = CardsTop - cpuScroll.Value;
        using var heading = new Font(Font, FontStyle.Bold);
        foreach (var section in sections)
        {
            if (grouped)
            {
                TextRenderer.DrawText(g, section.Title, heading, new Point(PaddingSize + 2, (int)y + 3), ForeColor,
                    TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
                y += header;
            }
            for (int i = 0; i < section.Tiles.Length; i++)
            {
                float top = y + i / columns * (cellHeight + CardGap);
                if (top + cellHeight >= CardsTop && top < CardsTop + height)
                    DrawCpuTile(g, section.Tiles[i], latest, range, card, grid, line, vmLine,
                        new RectangleF(PaddingSize + i % columns * (cellWidth + CardGap), top, cellWidth, cellHeight), showSource);
            }
            y += ((section.Tiles.Length + columns - 1) / columns) * (cellHeight + CardGap);
        }
        g.Restore(saved);
    }

    private void DrawCpuTile(Graphics g, CpuTile tile, Sample latest, HistoryRange range,
        SolidBrush card, Pen grid, Pen line, Pen vmLine, RectangleF bounds, bool showSource)
    {
        double? utilization = tile.Read(latest);
        card.Color = Blend(idleTile, busyTile, Math.Clamp(utilization.GetValueOrDefault(), 0, 100) / 100);
        grid.Color = Blend(card.Color, ForeColor, 0.22);
        g.FillRectangle(card, bounds);
        string reading = Percent(utilization);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix;
        int readingWidth = TextRenderer.MeasureText(g, reading, Font, Size.Empty, flags).Width;
        var label = new Rectangle((int)bounds.X + 4, (int)bounds.Y + 2, Math.Max(0, (int)bounds.Width - readingWidth - 11), Font.Height);
        string name = (tile.LogicalProcessor.HasValue ? "LP " : tile.GuestCounter == null ? "CPU " : "VP ") + tile.Processor;
        if (TextRenderer.MeasureText(g, name, Font, Size.Empty, flags).Width > label.Width)
            name = tile.Processor;
        TextRenderer.DrawText(g, name, Font, label, ForeColor, flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, reading, Font,
            new Rectangle((int)bounds.Right - readingWidth - 4, label.Y, readingWidth, Font.Height), ForeColor, flags);
        int plotOffset = Font.Height + 5;
        if (tile.LogicalProcessor.HasValue || showSource)
        {
            string secondary = tile.LogicalProcessor.HasValue ? "VMs " + Percent(tile.ReadVmTime(latest)) : tile.Machine;
            TextRenderer.DrawText(g, secondary, Font, new Rectangle(label.X, label.Bottom, (int)bounds.Width - 8, Font.Height),
                tile.LogicalProcessor.HasValue ? vmLine.Color : ForeColor, flags | TextFormatFlags.EndEllipsis);
            plotOffset += Font.Height;
        }
        var chart = new RectangleF(bounds.X + 5, bounds.Y + plotOffset, bounds.Width - 10, bounds.Height - plotOffset - 5);
        DrawChartGrid(g, chart, grid);
        DrawHistory(g, chart, line, range, tile.Read);
        if (tile.LogicalProcessor.HasValue)
            DrawHistory(g, chart, vmLine, range, tile.ReadVmTime);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (cpuScroll.Visible && e.Y >= CardsTop && e.Y < CardsTop + diskGrid.Height)
            cpuScroll.Value = Math.Clamp(cpuScroll.Value - Math.Sign(e.Delta) * cpuScroll.SmallChange * 3,
                0, Math.Max(0, cpuScroll.Maximum - cpuScroll.LargeChange + 1));
        else
            base.OnMouseWheel(e);
    }

    private void DrawDiskDetail(Graphics g, Sample latest, SolidBrush muted, Pen grid, Pen line, int width, HistoryRange range)
    {
        bool virtualDisk = selectedVirtualDisk != null;
        var historyRows = history.Skip(range.FirstSample).Select(ReadOverviewDisk).ToArray();
        var current = ReadOverviewDisk(latest);
        if (virtualDisk && latest != null && current == null && !selectedVirtualDisk.BelowFilter)
            current = VirtualReading(selectedVirtualDisk);
        double maxRate = Math.Max(1, Math.Ceiling(Math.Max(Math.Max(current?.Read ?? 0, current?.Write ?? 0),
            historyRows.Select(row => Math.Max(row?.Read ?? 0, row?.Write ?? 0)).DefaultIfEmpty().Max())));
        double maxSecondary = virtualDisk ? Math.Max(1, Math.Ceiling(Math.Max(current?.Secondary ?? 0,
            historyRows.Select(row => row?.Secondary ?? 0).DefaultIfEmpty().Max()))) : 100;
        string secondaryLabel = virtualDisk ? "Operations/s" : selectedDisk == null ? "Peak active time" : "Active time";
        string secondaryUnit = virtualDisk ? " ops/s" : "%";
        static string Number(double? value) => value?.ToString("0.#") ?? "—";
        string secondary = Number(current?.Secondary) + (virtualDisk || current == null ? "" : "%");
        using var writeLine = new Pen(writeLineColor, line.Width);
        using var secondaryLine = new Pen(muted.Color) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        float statsWidth = DrawOverviewStats(g, muted, ("Read MiB/s", Number(current?.Read), Number(maxRate), line.Color),
            ("Write MiB/s", Number(current?.Write), Number(maxRate), writeLine.Color),
            (secondaryLabel, secondary, Number(maxSecondary) + (virtualDisk ? "" : "%"), null));
        var chart = OverviewChart(width, statsWidth, g.MeasureString(Number(maxRate), Font).Width + 16,
            g.MeasureString(Number(maxSecondary) + secondaryUnit, Font).Width + 14);
        DrawChartGrid(g, chart, grid);
        DrawHistory(g, chart, line, range, sample => ReadOverviewDisk(sample)?.Read, maxRate);
        DrawHistory(g, chart, writeLine, range, sample => ReadOverviewDisk(sample)?.Write, maxRate);
        DrawHistory(g, chart, secondaryLine, range, sample => ReadOverviewDisk(sample)?.Secondary, maxSecondary);
        DrawAxes(g, chart, muted, range.Duration, maxRate, "");
        DrawAxisValues(g, chart, muted, maxSecondary, secondaryUnit, right: true);
    }

    private (double Read, double Write, double Secondary)? ReadOverviewDisk(Sample sample)
    {
        if (sample == null)
            return null;
        if (selectedVirtualDisk != null)
            return sample.VirtualDisks?.GetValueOrDefault(selectedDisk) is { } disk ? VirtualReading(disk) : null;
        var reading = ReadChartDisk(sample);
        return reading is { } physical ? (physical.ReadMiB, physical.WriteMiB, physical.Active) : null;
    }

    private static (double Read, double Write, double Secondary) VirtualReading(DiskSummary disk) =>
        (disk.Latest.ReadMiB, disk.Latest.WriteMiB, disk.VirtualDisk.Totals.Operations / disk.Coverage);

    private DiskReading? ReadChartDisk(Sample sample)
    {
        if (selectedDisk != null)
            return sample.GetDisk(selectedDisk);
        if (sample.Disks is not { Count: > 0 } disks)
            return null;
        var scope = ActiveDiskScope;
        var included = disks.Values.Where(disk => IncludesDisk(scope, disk.Number)).ToArray();
        if (included.Length == 0)
            return null;
        return new DiskReading
        {
            Active = included.Max(disk => disk.Active),
            ReadMiB = included.Sum(disk => disk.ReadMiB),
            WriteMiB = included.Sum(disk => disk.WriteMiB)
        };
    }

    private void DrawDiskCard(object sender, PaintEventArgs e)
    {
        var card = (Button)sender;
        var disk = (DiskSummary)card.Tag;
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (disk.VirtualDisk != null)
        {
            DrawVirtualDiskCard(card, disk, g);
            return;
        }
        Color background = Blend(idleTile, busyTile, disk.Latest.Active / 100);
        using (var fill = new SolidBrush(background))
        using (var text = new SolidBrush(ForeColor))
        using (var nameFont = new Font(Font, FontStyle.Bold))
        using (var grid = new Pen(Blend(background, ForeColor, 0.22)))
        using (var line = new Pen(lineColor, 1.5f))
        {
            g.FillRectangle(fill, card.ClientRectangle);
            string number = DiskLabel(disk.Id);
            DiskIdentity identity;
            diskIdentities.TryGetValue(disk.Latest.Number, out identity);
            string[] paths = identity == null ? null : identity.AccessPaths;
            bool mounted = paths != null && paths.Length > 0;
            bool named = identity != null && !string.IsNullOrWhiteSpace(identity.Name);
            string kind = identity == null ? "Windows disk" : identity.Kind;
            string heading = number;
            string subtitle = kind;
            if (mounted)
            {
                heading = paths[0];
                subtitle = kind + "  |  " + number;
            }
            else if (paths != null)
            {
                if (named && identity.IsStorageSpaces)
                {
                    heading = identity.Name;
                    subtitle = number + "  |  No mount";
                }
                else
                    subtitle = kind + "  |  No mount";
            }
            else if (named)
            {
                heading = identity.Name;
                subtitle = kind + "  |  " + number;
            }

            if (disk.BelowFilter)
                subtitle = "Below card filters  |  " + subtitle;
            var lineFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var textFlags = lineFlags | TextFormatFlags.EndEllipsis;
            int headingWidth = card.Width - 16;
            if (mounted && paths.Length > 1)
            {
                string additional = "+" + (paths.Length - 1);
                int additionalWidth = TextRenderer.MeasureText(additional, Font).Width;
                TextRenderer.DrawText(g, additional, Font, new Rectangle(card.Width - 8 - additionalWidth, 6,
                    additionalWidth, 17), ForeColor, lineFlags | TextFormatFlags.Right);
                headingWidth -= additionalWidth + 6;
            }
            var headingFlags = lineFlags | (mounted ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, heading, nameFont, new Rectangle(8, 6, headingWidth, 17), ForeColor, headingFlags);
            TextRenderer.DrawText(g, subtitle, Font, new Rectangle(8, 23, card.Width - 16, 17), ForeColor, textFlags);

            string rate = diskRank.SelectedIndex switch
            {
                1 => $"{disk.ReadSum / disk.Coverage:0.##} MiB/s read",
                2 => $"{disk.Rate(writesOnly: true):0.##} MiB/s write",
                _ => $"{disk.Rate():0.##} MiB/s total"
            };
            string average = $"Avg {disk.AverageActive:0.#}% active  |  {rate}";
            TextRenderer.DrawText(g, average, Font, new Rectangle(8, 41, card.Width - 16, 17), ForeColor, textFlags);
            var chart = new RectangleF(8, 61, card.Width - 16, 25);
            DrawChartGrid(g, chart, grid);
            DrawHistory(g, chart, line, VisibleHistory(), sample => sample.GetDiskValue(disk.Id, reading => reading.Active));
            g.DrawString(TransferRates(disk.Latest), Font, text, 8, 91);
            if (disk.Id == selectedDisk)
                g.DrawRectangle(line, 1, 1, card.Width - 3, card.Height - 3);
            if (card.Focused)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(card.ClientRectangle, -4, -4));
        }
    }

    private void DrawVirtualDiskCard(Button card, DiskSummary disk, Graphics g)
    {
        using var fill = new SolidBrush(idleTile);
        using var nameFont = new Font(Font, FontStyle.Bold);
        using var outline = new Pen(disk.Id == selectedDisk ? lineColor : Blend(idleTile, ForeColor, 0.22), 1.5f);
        g.FillRectangle(fill, card.ClientRectangle);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        var row = new Rectangle(8, 6, card.Width - 16, 18);
        TextRenderer.DrawText(g, System.IO.Path.GetFileName(disk.VirtualDisk.Path), nameFont, row, ForeColor, flags);
        row.Offset(0, 18);
        string subtitle = disk.BelowFilter ? "Outside card ranking  |  " : "";
        TextRenderer.DrawText(g, subtitle + VirtualDiskLabel(disk), Font, row, ForeColor, flags);
        row.Offset(0, 22);
        TextRenderer.DrawText(g, $"Avg {disk.Latest.ReadMiB + disk.Latest.WriteMiB:0.##} MiB/s total",
            Font, row, ForeColor, flags);
        row.Offset(0, 18);
        TextRenderer.DrawText(g, TransferRates(disk.Latest), Font, row, ForeColor, flags);
        row.Offset(0, 18);
        TextRenderer.DrawText(g, $"{disk.VirtualDisk.Totals.Operations:N0} operations in {disk.Coverage:0} s",
            Font, row, ForeColor, flags);
        g.DrawRectangle(outline, 1, 1, card.Width - 3, card.Height - 3);
        if (card.Focused)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(card.ClientRectangle, -4, -4));
    }

    private void DrawAxes(Graphics g, RectangleF chart, Brush text, string duration, double maximum, string unit)
    {
        DrawAxisValues(g, chart, text, maximum, unit);
        g.DrawString(duration + " ago", Font, text, chart.Left, chart.Bottom + 4);
        g.DrawString("Now", Font, text, chart.Right - g.MeasureString("Now", Font).Width, chart.Bottom + 4);
    }

    private void DrawAxisValues(Graphics g, RectangleF chart, Brush text, double maximum, string unit, bool right = false)
    {
        float labelOffset = Font.GetHeight(g) / 2;
        for (int level = 0; level <= 2; level++)
        {
            string label = (maximum * level / 2).ToString("0.#") + unit;
            float y = chart.Bottom - chart.Height * level / 2 - labelOffset;
            float x = right ? chart.Right + 5 : chart.Left - 5 - g.MeasureString(label, Font).Width;
            g.DrawString(label, Font, text, x, y);
        }
    }

    private static void DrawChartGrid(Graphics g, RectangleF chart, Pen grid)
    {
        g.DrawLine(grid, chart.Left, chart.Top, chart.Right, chart.Top);
        g.DrawLine(grid, chart.Left, chart.Top + chart.Height / 2, chart.Right, chart.Top + chart.Height / 2);
        g.DrawLine(grid, chart.Left, chart.Bottom, chart.Right, chart.Bottom);
    }

    private void DrawHistory(Graphics g, RectangleF chart, Pen line, HistoryRange range,
        Func<Sample, double?> readValue, double maximum = 100)
    {
        PointF? previous = null;
        for (int i = range.FirstSample; i < history.Count; i++)
        {
            Sample sample = history[i];
            double? utilization = readValue(sample);
            if (!utilization.HasValue)
            {
                previous = null;
                continue;
            }

            var point = new PointF(chart.Left + (float)((sample.Time - range.Start) / range.Seconds) * chart.Width,
                chart.Bottom - (float)(utilization.Value / maximum) * chart.Height);
            if (previous.HasValue && !sample.StartsNewSegment)
                g.DrawLine(line, previous.Value, point);
            previous = point;
        }
    }

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        try
        {
            using (var counters = new CpuCounters())
            using (var window = new Timeline(counters))
                Application.Run(window);
        }
        catch (InvalidOperationException e)
        {
            MessageBox.Show(e.Message, "AI Fleet Cluster Performance Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
