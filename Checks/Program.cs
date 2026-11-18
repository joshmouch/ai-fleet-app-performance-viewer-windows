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
var payload = FileWindowExport.Encode(decoded);
Check(payload.SequenceEqual(FileWindowExport.Encode(decoded)), "a retried window keeps the same OTLP bytes");
using var envelope = System.Text.Json.JsonDocument.Parse(payload);
var record = envelope.RootElement.GetProperty("resourceLogs")[0].GetProperty("scopeLogs")[0].GetProperty("logRecords")[0];
using var exported = System.Text.Json.JsonDocument.Parse(record.GetProperty("body").GetProperty("stringValue").GetString());
var stored = exported.RootElement;
var files = stored.GetProperty("rows").EnumerateArray().ToArray();
Check(stored.GetProperty("scope").GetString() == "disk:2" && stored.GetProperty("capture").GetGuid() == window.Capture, "stored disk identity");
Check(files.Sum(r => r.GetProperty("readBytes").GetInt64()) == 1054 && files.Sum(r => r.GetProperty("writeBytes").GetInt64()) == 2028 &&
    files.Sum(r => r.GetProperty("readOps").GetInt64()) == 9 && files.Sum(r => r.GetProperty("writeOps").GetInt64()) == 22,
    "stored counters preserve the unresolved row");
try
{
    FileWindowExport.Encode(window with { DiskPaths = window.DiskPaths with { CapturedBytes = 3083 } });
    throw new Exception("Inconsistent coverage was exported.");
}
catch (InvalidDataException) { }
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
var wrapper = new CpuProcessRow(222, "vmmem", .8);
var guestCpu = new ClusterCpuSample(window.Cpu,
    [new("WSL / distro", [new(3, "git", 1, .10, .08)], "WSL")],
    [new("WSL", .8, .08, true, ReplacedRows: [wrapper])]);
var layered = window with {
    Cpu = window.Cpu with { Rows = [..window.Cpu.Rows, wrapper] },
    Guests = new(new(time, guestCpu), [new("WSL / distro", "WSL", "/dev/sda", [backing.Path], 4,
        guests.Select(p => new MeasuredFile(p.Key, p.Value)).ToArray(), false, null)], null)
};
layered = PerformanceWindow.Decode(layered.Encode());
var projected = GuestProjection.CpuRows(layered, _ => true).ToArray();
Check(projected.All(r => r.Row.Pid != 222) && projected.Single(r => r.Machine == "WSL / distro").HostPercent == 1, "10% of 10% host CPU");
Check(GuestProjection.CpuRows(layered, _ => false).Any(r => r.Row.Pid == 222), "toggle restores wrapper");
var layeredDisk = GuestProjection.DiskRows(layered, _ => true);
Check(layeredDisk.Any(r => r.Key.Path == "/repo/a") && DiskIoMerge.Sum(layeredDisk.Select(r => r.Totals)).Equals(window.DiskPaths.Totals()), "guest path replay conserves four totals");
Check(GuestProjection.DiskRows(layered, _ => false).Single(r => r.Key == backing).Totals.Equals(raw[backing]), "toggle restores backing file");
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
