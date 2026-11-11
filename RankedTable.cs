using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class RankedTable<TRow> : ListView
{
    private sealed record Column(string Title, Func<TRow, IComparable> Value, bool Numeric,
        Func<TRow, double?> Total = null, Func<double, double, string> FormatTotal = null);
    private sealed record BandDivider(int Band);
    private readonly record struct BandRow(TRow Row, int Divider = -1, double?[] Totals = null);

    private readonly List<Column> columns = new();
    private readonly ContextMenuStrip rowMenu = new();
    private string rowToCopy;
    private BandRow[] virtualRows = Array.Empty<BandRow>();
    private Func<TRow, ListViewGroup, ListViewItem> createVirtualItem;
    private int bandWidth;
    private string bandUnit;
    private double bandSeconds;
    private Font dividerFont;
    private bool rendering;
    private bool viewportQueued;
    private int sortColumn;
    private int impactColumn;
    private bool descending;
    public event EventHandler RankingChanged;
    public event EventHandler ViewportChanged;

    public RankedTable()
    {
        RetrieveVirtualItem += (_, e) => e.Item = (uint)e.ItemIndex < (uint)virtualRows.Length
            ? CreateBandItem(virtualRows[e.ItemIndex]) : new ListViewItem();
        View = View.Details;
        Dock = DockStyle.Fill;
        FullRowSelect = true;
        HideSelection = false;
        ShowItemToolTips = true;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        rowMenu.Items.Add("Copy row", null, (_, _) =>
        {
            try { Clipboard.SetDataObject(rowToCopy, true, 5, 50); }
            catch (ExternalException) { }
        });
        ColumnClick += (_, e) =>
        {
            if (VirtualMode || columns[e.Column].Value == null)
                return;
            descending = sortColumn == e.Column ? !descending : columns[e.Column].Numeric;
            sortColumn = e.Column;
            MarkSort();
            RankingChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public ColumnHeader AddColumn(string title, int width, Func<TRow, IComparable> value, bool numeric = false)
    {
        columns.Add(new Column(title, value, numeric));
        return Columns.Add(title, width, numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left);
    }

    public ColumnHeader AddMetric(string title, int width, Func<TRow, double?> value, Func<double, double, string> format)
    {
        columns.Add(new Column(title, row => value(row), true, value, format));
        return Columns.Add(title, width, HorizontalAlignment.Right);
    }

    public void SortBy(ColumnHeader column)
    {
        impactColumn = sortColumn = column.Index;
        descending = columns[sortColumn].Numeric;
        MarkSort();
    }

    private void MarkSort(int? usageColumn = null)
    {
        for (int i = 0; i < columns.Count; i++)
            Columns[i].Text = columns[i].Title + (i == (usageColumn ?? sortColumn) ? usageColumn.HasValue || descending ? " ▼" : " ▲" : "");
    }

    public void Render(IEnumerable<TRow> rows, Func<TRow, string> machine, bool group, int limit,
        Func<TRow, ListViewGroup, ListViewItem> createItem,
        (int Width, Func<TRow, double?> Weight, double Total, string Unit)? bands = null,
        Func<TRow, object, bool> matchesKey = null, double seconds = 0)
    {
        if (VirtualMode != bands.HasValue)
            MarkSort(bands.HasValue ? impactColumn : null);

        var selected = SelectedIndices.Count > 0 ? Items[SelectedIndices[0]].Tag : null;
        var focused = FocusedItem?.Tag;
        var topItem = ShowGroups ? null : TopItem;
        var top = topItem?.Tag;
        int topIndex = topItem?.Index ?? 0;
        rendering = true;
        BeginUpdate();
        try
        {
            if (bands is { } usage)
            {
                var ordered = rows.Select(row => (Row: row, Weight: usage.Weight(row)))
                    .OrderByDescending(entry => entry.Weight);
                RenderVirtualBands(CreateBands(ordered, usage.Width, usage.Total),
                    usage.Width, usage.Unit, seconds, createItem, matchesKey, selected, focused, top, topIndex);
                return;
            }

            if (VirtualMode)
            {
                ClearRows();
                VirtualMode = false;
            }
            var column = columns[sortColumn];
            var known = rows.Select(row => (Row: row, Value: column.Value(row))).OrderBy(entry => entry.Value == null);
            var ranked = (descending ? known.ThenByDescending(entry => entry.Value) : known.ThenBy(entry => entry.Value))
                .Select(entry => entry.Row);
            var groups = group ? ranked.GroupBy(machine, StringComparer.OrdinalIgnoreCase).ToArray() : null;
            Items.Clear();
            Groups.Clear();
            ShowGroups = groups is { Length: > 1 };
            if (ShowGroups)
            {
                foreach (var section in groups)
                {
                    var heading = new ListViewGroup(section.Key);
                    Groups.Add(heading);
                    Items.AddRange(section.Take(limit).Select(row => createItem(row, heading)).ToArray());
                }
            }
            else
                Items.AddRange((groups?.FirstOrDefault() ?? ranked).Take(limit).Select(row => createItem(row, null)).ToArray());

            foreach (ListViewItem item in Items)
            {
                if (selected != null && Equals(item.Tag, selected))
                    item.Selected = true;
                if (focused != null && Equals(item.Tag, focused))
                    item.Focused = true;
            }
            if (!ShowGroups && Items.Count > 0 && topIndex > 0)
                TopItem = Items.Cast<ListViewItem>().FirstOrDefault(item => Equals(item.Tag, top)) ??
                    Items[Math.Min(topIndex, Items.Count - 1)];
        }
        finally
        {
            EndUpdate();
            rendering = false;
        }
    }

    public void ClearRows()
    {
        if (!VirtualMode)
        {
            Items.Clear();
            return;
        }
        virtualRows = Array.Empty<BandRow>();
        VirtualListSize = 0;
        createVirtualItem = null;
    }

    private void RenderVirtualBands(BandRow[] rows, int width, string unit, double seconds,
        Func<TRow, ListViewGroup, ListViewItem> createItem, Func<TRow, object, bool> matchesKey,
        object selected, object focused, object top, int topIndex)
    {
        if (!VirtualMode)
        {
            Items.Clear();
            Groups.Clear();
            ShowGroups = false;
            VirtualMode = true;
        }
        bool sameWidth = width == bandWidth;
        virtualRows = rows;
        createVirtualItem = createItem;
        bandWidth = width;
        bandUnit = unit;
        bandSeconds = seconds;
        if (VirtualListSize != rows.Length)
            VirtualListSize = rows.Length;

        int selectedIndex = Find(selected), focusedIndex = Find(focused);
        if (SelectedIndices.Count != 0 && (SelectedIndices.Count != 1 || SelectedIndices[0] != selectedIndex))
            SelectedIndices.Clear();
        if (selectedIndex >= 0 && SelectedIndices.Count == 0)
            SelectedIndices.Add(selectedIndex);
        if (focusedIndex >= 0 && FocusedItem?.Index != focusedIndex)
            FocusedItem = Items[focusedIndex];
        if (rows.Length > 0 && topIndex > 0)
        {
            int index = Find(top);
            int restore = index >= 0 ? index : Math.Min(topIndex, rows.Length - 1);
            if (TopItem?.Index != restore)
                TopItem = Items[restore];
        }
        Invalidate();

        int Find(object key)
        {
            if (key == null)
                return -1;
            if (key is BandDivider divider)
                return sameWidth ? Array.FindIndex(rows, row => row.Divider == divider.Band) : -1;
            return matchesKey == null ? -1 : Array.FindIndex(rows, row => row.Divider < 0 && matchesKey(row.Row, key));
        }
    }

    private BandRow[] CreateBands(IEnumerable<(TRow Row, double? Weight)> rows, int width, double total)
    {
        var result = new List<BandRow>();
        var metrics = columns.Select((column, index) => (column.Total, Index: index))
            .Where(metric => metric.Total != null).ToArray();
        int bandCount = (100 + width - 1) / width;
        double used = 0;
        int previous = -1;
        double?[] totals = null;
        foreach (var (row, weight) in rows)
        {
            double value = weight ?? 0;
            int band = value > 0 && total > 0 ? Math.Min((int)(100 * used / total / width + 1e-9), bandCount - 1) : bandCount;
            if (band != previous)
            {
                totals = new double?[columns.Count];
                result.Add(new BandRow(default, band, totals));
                previous = band;
            }
            foreach (var (metric, index) in metrics)
                if (metric(row) is double amount)
                    totals[index] = (totals[index] ?? 0) + amount;
            if (value > 0)
                used += value;
            result.Add(new BandRow(row));
        }
        return result.ToArray();
    }

    private ListViewItem CreateBandItem(BandRow row)
    {
        if (row.Divider < 0)
            return createVirtualItem(row.Row, null);
        dividerFont ??= new Font(Font, FontStyle.Bold);
        int start = row.Divider * bandWidth;
        bool unmeasured = start >= 100;
        var cells = new string[columns.Count];
        cells[0] = unmeasured ? "No measured impact" : $"{start}–{Math.Min(start + bandWidth, 100)}% of {bandUnit}";
        for (int index = 1; index < cells.Length; index++)
            if (row.Totals[index] is double total)
                cells[index] = columns[index].FormatTotal(total, bandSeconds);
        return new ListViewItem(cells)
        {
            Font = dividerFont,
            BackColor = Color.FromArgb((7 * BackColor.R + ForeColor.R) / 8,
                (7 * BackColor.G + ForeColor.G) / 8, (7 * BackColor.B + ForeColor.B) / 8),
            Tag = new BandDivider(row.Divider),
            ToolTipText = unmeasured ? "Totals for rows with zero or unavailable measured impact." :
                "Totals for rows that start in this band. A large row can span several bands."
        };
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (!VirtualMode || rendering || viewportQueued || !IsHandleCreated ||
            message.Msg is not (0x0115 or 0x020A or 0x0100) || ViewportChanged == null)
            return;
        viewportQueued = true;
        BeginInvoke(() =>
        {
            viewportQueued = false;
            if (!IsDisposed && VirtualMode)
                ViewportChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right || GetItemAt(e.X, e.Y) is not { Tag: not BandDivider } item)
            return;

        // Live refreshes can replace the clicked item while the menu is open.
        rowToCopy = string.Join(Environment.NewLine, Enumerable.Range(0, Math.Min(item.SubItems.Count, columns.Count))
            .Where(index => !string.IsNullOrWhiteSpace(item.SubItems[index].Text))
            .Select(index => $"{columns[index].Title}: {item.SubItems[index].Text}"));
        if (rowToCopy.Length > 0)
            rowMenu.Show(this, e.Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            rowMenu.Dispose();
            dividerFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class RowFilter
{
    public static bool Matches(string filter, params string[] fields) => filter.Length == 0 ||
        fields.Any(field => field.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
