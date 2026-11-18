using System.Globalization;
using System.Text.RegularExpressions;

sealed class PhysicalWindow(string scope)
{
    static readonly Regex Row = new(@"^\s*\S+\s+(?<op>RdData|WrData|RdMeta|WrMeta)\[[^\]]+\]\s+D=0x[0-9a-fA-F]+\s+B=0x(?<bytes>[0-9a-fA-F]+)\s+(?<device>/dev/\S+)\s+(?:(?<path>.*?)\s+)?[0-9]+\.[0-9]+\s+(?:W\s+)?(?<command>.+)\.(?<tid>[0-9]+)\s*$", RegexOptions.CultureInvariant);
    readonly Dictionary<FileIoKey, FileIoTotals> files = new();
    long captured, named, keyless;
    int lossNotices, malformed;
    public bool Error;
    public void Accept(string line, Func<ulong, ProcessOwner> owner)
    {
        var m = Row.Match(line);
        if (!m.Success)
        {
            if (Regex.IsMatch(line, @"\b(?:lost|dropped|overflow)\b", RegexOptions.IgnoreCase)) lossNotices++;
            else if (line.Contains("RdData") || line.Contains("WrData") || line.Contains("RdMeta") || line.Contains("WrMeta")) { Error = true; malformed++; }
            return;
        }
        if (!long.TryParse(m.Groups["bytes"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bytes) || bytes < 0)
        { Error = true; malformed++; return; }
        var identity = ulong.TryParse(m.Groups["tid"].Value, out var tid) ? owner(tid) : null;
        var path = m.Groups["path"].Value.Trim();
        if (!m.Groups["op"].Value.EndsWith("Data") || path.StartsWith("/dev/")) path = "";
        var read = m.Groups["op"].Value.StartsWith("Rd");
        captured += bytes;
        if (path.Length == 0) keyless += bytes; else named += bytes;
        FileIoTotals.AddTo(files, new FileIoKey(identity?.Pid ?? 0, identity?.Name ?? "Unknown process (includes metadata)", path),
            new FileIoTotals { ReadBytes = read ? bytes : 0, WriteBytes = read ? 0 : bytes, Reads = read ? 1 : 0, Writes = read ? 0 : 1 });
    }
    public PerformanceWindow Frame(double seconds, Guid capture, long sequence, int censusFailures = 0, bool final = false,
        CpuProcessSample cpu = null, string cpuStatus = null)
    {
        var end = DateTimeOffset.UtcNow;
        return new(PerformanceWindow.CurrentSchema, Environment.MachineName, null, capture, sequence, cpu?.End ?? end,
            cpu ?? new CpuProcessSample(Environment.ProcessorCount, seconds, []),
            new DiskPathWindow(end, seconds, null,
                $"mac fs_usage diskio; scope={scope}; named rows=data; empty paths include metadata/unresolved; coverage partial; censusUnavailable={censusFailures}; lossNotices={lossNotices} (lost event count unknown); malformed={malformed}; error={Error}; final={final}",
                lossNotices, captured, named, keyless, files.Select(x => new MeasuredFile(x.Key, x.Value)).ToArray(), scope),
            CpuStatus: cpu == null ? "CPU is unavailable in this disk sample." : cpuStatus);
    }
}
