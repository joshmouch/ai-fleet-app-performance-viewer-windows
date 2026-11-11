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
    private Font dividerFont;
    private bool usageBands;
    private bool rendering;
    private int sortColumn;
    private int impactColumn;
    private bool descending;
    public event EventHandler RankingChanged;
    public event EventHandler ViewportChanged;

    public RankedTable()
    {
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
        (Func<TRow, double?> Weight, double Total, string Unit)? bands = null)
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
        var selected = SelectedItems.Count > 0 ? SelectedItems[0].Tag : null;
        var focused = FocusedItem?.Tag;
        var topItem = ShowGroups ? null : TopItem;
        var top = topItem?.Tag;
        int topIndex = topItem?.Index ?? 0;
        rendering = true;
        BeginUpdate();
        try
        {
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
                Items.AddRange(CreateBands(ordered, usage.Weight, usage.Total, usage.Unit, createItem).ToArray());
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
    private IEnumerable<ListViewItem> CreateBands(TRow[] rows, Func<TRow, double?> weight, double total, string unit,
        Func<TRow, ListViewGroup, ListViewItem> createItem)
    {
        if (rows.Length == 0)
            yield break;

        dividerFont ??= new Font(Font, FontStyle.Bold);
        double used = 0;
        int previous = -1;
        foreach (var row in rows)
        {
            double value = weight(row) ?? 0;
            int band = value > 0 && total > 0 ? Math.Clamp((int)(10 * used / total + 1e-9), 0, 9) : 10;
            if (band != previous)
            {
                string caption = band == 10 ? "No measured impact" : $"{band * 10}–{(band + 1) * 10}% of {unit}";
                yield return new ListViewItem(caption)
                {
                    Font = dividerFont,
                    BackColor = Color.FromArgb((7 * BackColor.R + ForeColor.R) / 8,
                        (7 * BackColor.G + ForeColor.G) / 8, (7 * BackColor.B + ForeColor.B) / 8),
                    Tag = new BandDivider(band),
                    ToolTipText = band == 10 ? "These rows have zero or unavailable measured impact." :
                        "Each row belongs to the band where its cumulative usage starts. Large rows can span bands."
                };
                previous = band;
            }
            if (value > 0)
                used += value;
            yield return createItem(row, null);
        }
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
