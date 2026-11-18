// Exercise the same public contract and reconciliation the clients consume.
var unknown = new FileIoKey(4, "System", "");
var backing = new FileIoKey(4, "System", @"D:\vm\disk.vhdx");
var raw = new Dictionary<FileIoKey, FileIoTotals>
{
    [unknown] = new() { ReadBytes = 53, WriteBytes = 25, Reads = 2, Writes = 3 },
    [backing] = new() { ReadBytes = 1001, WriteBytes = 2003, Reads = 7, Writes = 19 }
};
var time = DateTimeOffset.UtcNow;
var host = "fixture-windows";
var window = new PerformanceWindow(PerformanceWindow.CurrentSchema, host, time.AddDays(-1), Guid.NewGuid(),
    1, time, new CpuProcessSample(8, 10, [new(12, "old", .1, Started: 4), new(12, "new", .3, Started: 99)]),
    new DiskPathWindow(time, 58.5, 2, null, 0, 3082, 3004, 78,
        raw.Select(pair => new MeasuredFile(pair.Key, pair.Value)).ToArray()));
var encoded = window.Encode();
var decoded = PerformanceWindow.Decode(encoded);
Check(decoded.Encode() == encoded && decoded.DiskPaths.Files.Length == 2, "wire round-trip");
Check(decoded.Cpu.Rows[0].Started != decoded.Cpu.Rows[1].Started, "process incarnation");
Check(decoded.DiskPaths.Totals().TotalBytes == decoded.DiskPaths.CapturedBytes, "physical bytes");
var guests = new Dictionary<FileIoKey, FileIoTotals>
{
    [new(32, "git", "/repo/a")] = new() { WriteBytes = 7, Writes = 2 },
    [new(32, "git", "/repo/b")] = new() { WriteBytes = 8, Writes = 2 }
};
var expansion = new DiskIoExpansion("linux", [new(backing, raw[backing])], guests, 4);
var expanded = DiskIoMerge.Expand(host, raw, [expansion], true);
Check(DiskIoMerge.Sum(expanded.Select(row => row.Totals)).Equals(decoded.DiskPaths.Totals()), "all four budgets");
Check(expanded.Single(row => row.Key == backing).Totals.ReadBytes == 1001, "uncovered direction");
Check(expanded.Single(row => row.Key == unknown).Machine == host, "source machine on replay");
Check(expanded.Where(row => row.Guest != null).Sum(row => row.Totals.WriteBytes) == 2003, "one backing budget");
try
{
    PerformanceWindow.Decode(encoded.Replace(PerformanceWindow.CurrentSchema, "incompatible"));
    throw new Exception("Unsupported window accepted.");
}
catch (InvalidDataException) { }
Console.WriteLine("Passed sample replay, process identities, scope and directional guest accounting.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Failed " + name);
}
