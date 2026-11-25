using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

public readonly record struct FileIoKey(int Pid, string Process, string Path)
{
    [JsonIgnore] public FileIoKey ProcessKey => this with { Path = "" };
    private int LastSeparator => Path.StartsWith('/') ? Path.LastIndexOf('/') :
        Math.Max(Path.LastIndexOf('\\'), Path.LastIndexOf('/'));
    [JsonIgnore] public string Filename => Path[(LastSeparator + 1)..];
    [JsonIgnore] public string Filepath => Path[..(LastSeparator + 1)];
    public static bool IsVirtualDisk(ReadOnlySpan<char> path) =>
        path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase);
}

public struct FileIoTotals
{
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    [JsonIgnore] public long TotalBytes => ReadBytes + WriteBytes;
    [JsonIgnore] public long Operations => Reads + Writes;

    public void Add(FileIoTotals value)
    {
        ReadBytes += value.ReadBytes;
        WriteBytes += value.WriteBytes;
        Reads += value.Reads;
        Writes += value.Writes;
    }

    public readonly FileIoTotals Minus(FileIoTotals value) => new()
    {
        ReadBytes = ReadBytes - value.ReadBytes,
        WriteBytes = WriteBytes - value.WriteBytes,
        Reads = Reads - value.Reads,
        Writes = Writes - value.Writes
    };

    public static void AddTo<TKey>(Dictionary<TKey, FileIoTotals> target, TKey key, FileIoTotals value)
    {
        // One hash and probe per update: the slot is updated in place, starting at default for a new key.
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(target, key, out _);
        slot.Add(value);
    }
}
