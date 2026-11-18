using System.Globalization;
using System.Text.Json;

public sealed record StoredFileRow(int Pid, string Process, string Path,
    long ReadBytes, long WriteBytes, long ReadOps, long WriteOps)
{
    internal static StoredFileRow From(MeasuredFile m) => new(m.File.Pid, m.File.Process, m.File.Path ?? "",
        m.Totals.ReadBytes, m.Totals.WriteBytes, m.Totals.Reads, m.Totals.Writes);

    public DiskIoRow ToDiskRow(string machine) => new(new(Pid, Process, Path), machine, new()
        { ReadBytes = ReadBytes, WriteBytes = WriteBytes, Reads = ReadOps, Writes = WriteOps });
}

// The OTLP Body is immutable. Retries must reproduce the property order and millisecond timestamps
// used by the original writer, including empty paths for unidentified operations.
public sealed record StoredFileWindow(string Schema, string Host, Guid Capture, long Sequence,
    string Scope, string Layer, string Start, string End, string Status, int EventsLost,
    long CapturedBytes, long NamedBytes, long KeylessBytes, StoredFileRow[] Rows)
{
    public const string CurrentSchema = "performance-viewer/file-window/v1";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true };
    public static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public double DurationSeconds() => (ParseTime(End) - ParseTime(Start)).TotalSeconds;
    public string Encode() => JsonSerializer.Serialize(this, Options);

    public static StoredFileWindow From(PerformanceWindow frame)
    {
        var d = frame.DiskPaths;
        if (!double.IsFinite(d.Seconds) || d.Seconds <= 0) throw new InvalidDataException("Invalid disk interval.");
        return new StoredFileWindow(CurrentSchema, frame.Host, frame.Capture, frame.Sequence,
            d.Disk is int disk ? "disk:" + disk.ToString(CultureInfo.InvariantCulture) : "host", "physical",
            Utc(d.End - TimeSpan.FromSeconds(d.Seconds)), Utc(d.End), d.Status ?? "running", d.EventsLost,
            d.CapturedBytes, d.NamedBytes, d.KeylessBytes, d.Files.Select(StoredFileRow.From).ToArray()).Validate();
    }

    public static StoredFileWindow Decode(string json) =>
        (JsonSerializer.Deserialize<StoredFileWindow>(json, Options) ?? throw new InvalidDataException("Empty file window.")).Validate();

    private StoredFileWindow Validate()
    {
        if (Schema != CurrentSchema || string.IsNullOrWhiteSpace(Host) || Capture == Guid.Empty || Sequence < 1 ||
            string.IsNullOrEmpty(Scope) || Layer is not ("physical" or "guest-logical") || Rows == null ||
            DurationSeconds() <= 0 || EventsLost < 0 || KeylessBytes < 0 || KeylessBytes > CapturedBytes)
            throw new InvalidDataException("Invalid file window identity, interval or coverage.");
        var seen = new HashSet<FileIoKey>();
        long captured = 0, named = 0;
        foreach (var r in Rows)
        {
            if (r == null || r.Path == null || r.Process == null || !seen.Add(new(r.Pid, r.Process, r.Path)) ||
                r.ReadBytes < 0 || r.WriteBytes < 0 || r.ReadOps < 0 || r.WriteOps < 0)
                throw new InvalidDataException("Duplicate or invalid file row.");
            checked
            {
                captured += r.ReadBytes + r.WriteBytes;
                if (r.Path.Length > 0) named += r.ReadBytes + r.WriteBytes;
            }
        }
        if (captured != CapturedBytes || named != NamedBytes)
            throw new InvalidDataException("File rows do not reconcile to the collector's capture and named bytes.");
        return this;
    }
}
