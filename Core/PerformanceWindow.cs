using System.Text.Json;

public sealed record MeasuredFile(FileIoKey File, FileIoTotals Totals);

public sealed record DiskPathWindow(DateTimeOffset End, double Seconds, int? Disk, string Status,
    int EventsLost, long CapturedBytes, long NamedBytes, long KeylessBytes, MeasuredFile[] Files, string Scope = "host")
{
    public void RequireHostAggregate()
    {
        if (Scope != "host") throw new InvalidDataException("Process-scoped capture is not a host aggregate.");
    }
    public FileIoTotals Totals() => DiskIoMerge.Sum(Files.Select(row => row.Totals));
}

// Disk paths use the native sampler's rolling window. CPU covers the interval between
// process snapshots. Consumers replace the previous frame rather than summing rolling windows.
public sealed record PerformanceWindow(string Schema, string Host, DateTimeOffset? Boot,
    Guid Capture, long Sequence, DateTimeOffset CpuEnd, CpuProcessSample Cpu, DiskPathWindow DiskPaths, GuestWindow Guests = null,
    string CpuStatus = null)
{
    public const string CurrentSchema = "performance-viewer/window/v1";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    public string Encode() => JsonSerializer.Serialize(this, Options);

    public static PerformanceWindow Decode(string line)
    {
        var frame = JsonSerializer.Deserialize<PerformanceWindow>(line, Options);
        if (frame?.Schema != CurrentSchema || string.IsNullOrWhiteSpace(frame.Host) ||
            frame.Capture == Guid.Empty || frame.Sequence < 1 || frame.Cpu?.Rows == null ||
            frame.DiskPaths?.Files == null)
            throw new InvalidDataException("Missing or unsupported performance window.");
        return frame;
    }
}
