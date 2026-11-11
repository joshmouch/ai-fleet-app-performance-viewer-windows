using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

internal sealed class RankedTable<TRow> : ListView
{
    private sealed record Column(string Title, Func<TRow, IComparable> Value, bool Numeric);
    private readonly List<Column> columns = new();
    private int sortColumn;
    private bool descending;
    public event EventHandler RankingChanged;

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
        sortColumn = column.Index;
        descending = columns[sortColumn].Numeric;
        MarkSort();
    }

    private void MarkSort()
    {
        for (int i = 0; i < columns.Count; i++)
            Columns[i].Text = columns[i].Title + (i == sortColumn ? descending ? " ▼" : " ▲" : "");
    }

    public void Render(IEnumerable<TRow> rows, Func<TRow, string> machine, bool group, int limit,
        Func<TRow, ListViewGroup, ListViewItem> createItem)
    {
        var column = columns[sortColumn];
        var ranked = rows.Select(row => (Row: row, Value: column.Value(row)));
        var known = ranked.OrderBy(entry => entry.Value == null);
        var ordered = (descending ? known.ThenByDescending(entry => entry.Value) : known.ThenBy(entry => entry.Value))
            .Select(entry => entry.Row).ToArray();
        var groups = ordered.GroupBy(machine, StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = SelectedItems.Count > 0 ? SelectedItems[0].Tag : null;
        var focused = FocusedItem?.Tag;
        var topItem = ShowGroups ? null : TopItem;
        var top = topItem?.Tag;
        int topIndex = topItem?.Index ?? 0;
        BeginUpdate();
        try
        {
            Items.Clear();
            Groups.Clear();
            ShowGroups = group && groups.Length > 1;
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
                Items.AddRange(ordered.Take(limit).Select(row => createItem(row, null)).ToArray());

            foreach (ListViewItem item in Items)
            {
                if (Equals(item.Tag, selected))
                    item.Selected = true;
                if (Equals(item.Tag, focused))
                    item.Focused = true;
            }
            if (!ShowGroups && Items.Count > 0 && topIndex > 0)
                TopItem = Items.Cast<ListViewItem>().FirstOrDefault(item => Equals(item.Tag, top)) ??
                    Items[Math.Min(topIndex, Items.Count - 1)];
        }
        finally { EndUpdate(); }
    }
}

internal static class RowFilter
{
    public static bool Matches(string filter, params string[] fields) => filter.Length == 0 ||
        fields.Any(field => field.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
