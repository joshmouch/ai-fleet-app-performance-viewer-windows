using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

internal sealed class RankedTable<TRow> : ListView
{
    private sealed record Column(string Title, Func<TRow, IComparable> Value, bool Numeric);
    private readonly List<Column> columns = new();
    private sealed record BandDivider(int Band);
    private readonly record struct BandRow(TRow Row, int Divider = -1);
    private readonly bool virtualizeBands;
    private BandRow[] virtualRows = Array.Empty<BandRow>();
    private Func<TRow, ListViewGroup, ListViewItem> createVirtualItem;
    private string virtualUnit;
    private Font dividerFont;
    private bool usageBands;
    private bool rendering;
    private int sortColumn;
    private int impactColumn;
    private bool descending;
    public event EventHandler RankingChanged;
    public event EventHandler ViewportChanged;

    public RankedTable(bool virtualizeBands = false)
    {
        this.virtualizeBands = virtualizeBands;
        if (virtualizeBands)
            RetrieveVirtualItem += (_, e) => e.Item = e.ItemIndex < virtualRows.Length
                ? CreateBandItem(virtualRows[e.ItemIndex], virtualUnit, createVirtualItem) : BandHeading("");
        View = View.Details;
        Dock = DockStyle.Fill;
        FullRowSelect = true;
        HideSelection = false;
        ShowItemToolTips = true;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        ColumnClick += (_, e) =>
        {
            if (usageBands || columns[e.Column].Value == null)
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
        (Func<TRow, double?> Weight, double Total, string Unit)? bands = null,
        Func<TRow, object> rowKey = null)
    {
        if (usageBands != bands.HasValue)
        {
            usageBands = bands.HasValue;
            MarkSort(usageBands ? impactColumn : null);
        }
        var column = columns[sortColumn];
        var ranked = rows.Select(row => (Row: row, Value: bands is { } usage ? (IComparable)usage.Weight(row) : column.Value(row)));
        var known = ranked.OrderBy(entry => entry.Value == null);
        var ordered = (usageBands || descending ? known.ThenByDescending(entry => entry.Value) : known.ThenBy(entry => entry.Value))
            .Select(entry => entry.Row).ToArray();
        var groups = group && !usageBands ? ordered.GroupBy(machine, StringComparer.OrdinalIgnoreCase).ToArray() : null;
        var selected = SelectedIndices.Count > 0 ? Items[SelectedIndices[0]].Tag : null;
        var focused = FocusedItem?.Tag;
        var topItem = ShowGroups ? null : TopItem;
        var top = topItem?.Tag;
        int topIndex = topItem?.Index ?? 0;
        rendering = true;
        BeginUpdate();
        try
        {
            if (virtualizeBands && bands is { } virtualBands)
            {
                RenderVirtualBands(CreateBands(ordered, virtualBands.Weight, virtualBands.Total).ToArray(),
                    virtualBands.Unit, createItem, rowKey, selected, focused, top, topIndex);
                return;
            }

            if (VirtualMode)
            {
                ClearRows();
                VirtualMode = false;
            }
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
            else if (bands is { } usage)
                Items.AddRange(CreateBands(ordered, usage.Weight, usage.Total)
                    .Select(row => CreateBandItem(row, usage.Unit, createItem)).ToArray());
            else
                Items.AddRange(ordered.Take(limit).Select(row => createItem(row, null)).ToArray());

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

    private void RenderVirtualBands(BandRow[] rows, string unit, Func<TRow, ListViewGroup, ListViewItem> createItem,
        Func<TRow, object> rowKey, object selected, object focused, object top, int topIndex)
    {
        if (!VirtualMode)
        {
            Items.Clear();
            Groups.Clear();
            ShowGroups = false;
            VirtualMode = true;
        }
        SelectedIndices.Clear();
        virtualRows = rows;
        createVirtualItem = createItem;
        virtualUnit = unit;
        VirtualListSize = rows.Length;
        int selectedIndex = Find(selected), focusedIndex = Find(focused);
        if (selectedIndex >= 0)
            SelectedIndices.Add(selectedIndex);
        if (focusedIndex >= 0)
            FocusedItem = Items[focusedIndex];
        if (rows.Length > 0 && topIndex > 0)
        {
            int index = Find(top);
            TopItem = Items[index >= 0 ? index : Math.Min(topIndex, rows.Length - 1)];
        }
        Invalidate();

        int Find(object key)
        {
            if (key == null)
                return -1;
            if (key is BandDivider divider)
                return Array.FindIndex(rows, row => row.Divider == divider.Band);
            return rowKey == null ? -1 : Array.FindIndex(rows, row => row.Divider < 0 && Equals(rowKey(row.Row), key));
        }
    }

    private static IEnumerable<BandRow> CreateBands(TRow[] rows, Func<TRow, double?> weight, double total)
    {
        double used = 0;
        int previous = -1;
        foreach (var row in rows)
        {
            double value = weight(row) ?? 0;
            int band = value > 0 && total > 0 ? Math.Clamp((int)(10 * used / total + 1e-9), 0, 9) : 10;
            if (band != previous)
            {
                yield return new BandRow(default, band);
                previous = band;
            }
            if (value > 0)
                used += value;
            yield return new BandRow(row);
        }
    }

    private ListViewItem CreateBandItem(BandRow row, string unit, Func<TRow, ListViewGroup, ListViewItem> createItem)
    {
        if (row.Divider < 0)
            return createItem(row.Row, null);
        dividerFont ??= new Font(Font, FontStyle.Bold);
        int band = row.Divider;
        string caption = band == 10 ? "No measured impact" : $"{band * 10}–{(band + 1) * 10}% of {unit}";
        var heading = BandHeading(caption);
        heading.Font = dividerFont;
        heading.BackColor = Color.FromArgb((7 * BackColor.R + ForeColor.R) / 8,
            (7 * BackColor.G + ForeColor.G) / 8, (7 * BackColor.B + ForeColor.B) / 8);
        heading.Tag = new BandDivider(band);
        heading.ToolTipText = band == 10 ? "These rows have zero or unavailable measured impact." :
            "Each row belongs to the band where its cumulative usage starts. Large rows can span bands.";
        return heading;
    }

    private ListViewItem BandHeading(string caption)
    {
        var cells = new string[Columns.Count];
        cells[0] = caption;
        return new ListViewItem(cells);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (usageBands && !rendering && message.Msg is 0x0115 or 0x020A or 0x0100)
            ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            dividerFont?.Dispose();
        base.Dispose(disposing);
    }
}

internal static class RowFilter
{
    public static bool Matches(string filter, params string[] fields) => filter.Length == 0 ||
        fields.Any(field => field.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
