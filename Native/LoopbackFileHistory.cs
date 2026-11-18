using System.Net;
using System.Net.Http.Json;
using PerformanceViewer.Components;

namespace PerformanceViewer.Native;

internal sealed class LoopbackFileHistory(HttpClient client) : IFileHistory
{
    public async Task<string[]> SourcesAsync(CancellationToken stop) =>
        await client.GetFromJsonAsync("api/history/sources", NativeJson.Default.StringArray, stop) ?? [];

    public async Task<StoredFileWindow?> ReadAsync(string source, DateTimeOffset at, CancellationToken stop)
    {
        var path = $"api/history/window?source={Uri.EscapeDataString(source)}&at={Uri.EscapeDataString(StoredFileWindow.Utc(at))}";
        using var reply = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, stop);
        if (reply.StatusCode == HttpStatusCode.NoContent) return null;
        reply.EnsureSuccessStatusCode();
        return await reply.Content.ReadFromJsonAsync(NativeJson.Default.StoredFileWindow, stop);
    }
}
