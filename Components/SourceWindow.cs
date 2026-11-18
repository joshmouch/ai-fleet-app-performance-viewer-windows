namespace PerformanceViewer.Components;

public sealed record SourceWindow(string Name, PerformanceWindow? Frame, string? Status, bool Replay,
    bool Stale, bool Selected);
