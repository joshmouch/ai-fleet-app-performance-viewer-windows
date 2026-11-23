namespace PerformanceViewer.Components;

public interface IFileHistory
{
    Task<string[]> SourcesAsync(CancellationToken stop);
    Task<StoredFileWindow?> ReadAsync(string source, DateTimeOffset at, CancellationToken stop);
}

public sealed record HistorySelection(bool Active, StoredFileWindow? Window, bool Loading = false, string? Message = null);
