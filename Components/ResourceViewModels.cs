namespace PerformanceViewer.Components;

public sealed record ResourcePoint(DateTimeOffset At, ResourceReading Reading, double Seconds);
public sealed record ResourceCardEntry(ResourceNode Node, ResourceReading Reading, double RelativeHeat);
public sealed record ResourceDetailRow(string[] Values, double Weight, double?[] Numbers);
public sealed record ResourceDetailTable(string[] Columns, ResourceDetailRow[] Rows, double TotalWeight = 0,
    string? Note = null, string? Summary = null);
