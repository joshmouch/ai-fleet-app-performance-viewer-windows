using System.Globalization;
using System.Threading.Channels;

// Files hold the payloads. The channel wakes one sender whenever work arrives.
public sealed class FileWindowArchive : IAsyncDisposable
{
    private readonly string pending;
    private readonly string delivered;
    private readonly HttpClient receiver;
    private readonly FileStream lease;
    private readonly CancellationTokenSource stop;
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(1);
    private readonly Task sender;
    private volatile bool finishing;

    public FileWindowArchive(string directory, CancellationToken cancellation)
    {
        string root = Path.GetFullPath(directory);
        pending = Directory.CreateDirectory(Path.Combine(root, "pending")).FullName;
        delivered = Path.Combine(root, "delivered");
        lease = new FileStream(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        try
        {
            // Recover a complete write interrupted immediately before its rename.
            foreach (var temp in Directory.EnumerateFiles(pending, "*.writing"))
            {
                var withSize = Path.ChangeExtension(temp, null);
                if (long.TryParse(Path.GetExtension(withSize).TrimStart('.'), CultureInfo.InvariantCulture, out long size)
                    && new FileInfo(temp).Length == size)
                    File.Move(temp, Path.ChangeExtension(withSize, null));
                else Console.Error.WriteLine($"Incomplete archived window retained: {Path.GetFileName(temp)}");
            }
            receiver = FileWindowExport.OpenReceiver();
        }
        catch { lease.Dispose(); throw; }
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        changes.Writer.TryWrite(true);
        sender = Task.Run(Deliver);
    }

    public void Record(PerformanceWindow frame)
    {
        if (FileWindowExport.IsCollectionStatus(frame.DiskPaths)) return;
        byte[] payload = FileWindowExport.Compress(frame);
        var time = frame.DiskPaths.End.UtcDateTime.ToString("yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture);
        var target = Path.Combine(pending, $"{time}-{frame.Capture:N}-{frame.Sequence}.json.gz");
        var temporary = target + "." + payload.Length.ToString(CultureInfo.InvariantCulture) + ".writing";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(payload);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, target);
        changes.Writer.TryWrite(true);
    }

    public async Task FinishAsync()
    {
        finishing = true;
        changes.Writer.TryComplete();
        await sender;
    }

    private async Task Deliver()
    {
        string lastFailure = "";
        await foreach (var _ in changes.Reader.ReadAllAsync(stop.Token))
        {
            while (true)
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(pending, "*.json.gz").Order(StringComparer.Ordinal))
                    {
                        var payload = await File.ReadAllBytesAsync(path, stop.Token);
                        await FileWindowExport.Send(receiver, payload, stop.Token);
                        string name = Path.GetFileName(path);
                        string folder = Directory.CreateDirectory(Path.Combine(delivered, name[..8])).FullName;
                        File.Move(path, Path.Combine(folder, name));
                    }
                    lastFailure = "";
                    break;
                }
                catch (Exception ex) when (!stop.IsCancellationRequested && ex is
                    (IOException or HttpRequestException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException))
                {
                    var message = $"Window delivery pending ({ex.GetType().Name}).";
                    if (lastFailure != message) Console.Error.WriteLine(message);
                    lastFailure = message;
                    if (finishing) throw new IOException("Receiver delivery incomplete. Windows remain in the archive.", ex);
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        changes.Writer.TryComplete();
        try { await sender; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { receiver.Dispose(); stop.Dispose(); lease.Dispose(); }
    }
}
