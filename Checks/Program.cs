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
Check(decoded.Resources == null, "older sample windows may omit resource measurements");
var measuredWindow = window with
{
    Resources = new(
        new CpuResourceMeasurements(31, [new("0,0", 42)], 20, 9, 2),
        [
            new(2, "Pool volume", "Storage Spaces volume", [@"D:\"], true, 8, 12, 46),
            new(71, "Backing drive", "Physical disk", [], false, 16, 24, 65)
        ],
        raw.Select(pair => new DiskFileMeasurement(2, pair.Key, pair.Value)).ToArray(),
        [new(2, backing.Path, "guest-one", "guest-one")])
};
var measured = PerformanceWindow.Decode(measuredWindow.Encode());
var diskTree = ResourceTree.Build(true, [measured], (_, _) => true, false);
var measuredDisk = diskTree.Children.Single();
var owner = measuredDisk.Children.Single();
var vhdx = owner.Children.Single();
Check(measuredDisk.Disk == 2 && owner.Kind == ResourceKind.VirtualMachine && owner.Name == "guest-one" &&
    vhdx.BackingPath == backing.Path && vhdx.Type == null, "storage volume contains the owning VM and its VHDX");
Check(diskTree.Reading is { Active: 46, Read: 8, Write: 12 } && vhdx.Reading.Active == null,
    "device counters exclude backing layers and VHDX active time stays unavailable");
var average = DiskActivity.Average([(new(60, 3, 9), 20), (new(30, null, 0), 40)]);
Check(average is { Active: 40, Read: 3, Write: 3 }, "disk average weights elapsed time and preserves missing rates");
Check(!DiskActivity.MeetsCutoffs(new(0, 0, 0), 0, 0) &&
    !DiskActivity.MeetsCutoffs(new(24, 8, 1), 25, 1) &&
    !DiskActivity.MeetsCutoffs(new(40, 0, .5), 25, 1) &&
    DiskActivity.MeetsCutoffs(new(null, 0, 2), 25, 1),
    "disk cutoffs omit idle disks, require both measured thresholds, and admit rate-only children");
Check(DiskActivity.RelativeHeat(new(20, 2, 4), 40, 20) == .5 &&
    DiskActivity.RelativeHeat(new(null, 5, 15), 40, 20) == 1,
    "disk heat scales within siblings by active time and transfer separately");
Check(ResourceTree.Build(true, [measured], (_, _) => true, true).Reading.Write == 36, "explicit all-disks scope");
Check(ResourceTree.Build(false, [measured], (_, _) => true, false).Children.Single().Children.Single().Reading.Active == 42,
    "CPU and disk use the same nested measurement shape");
var smallerHost = measured with { Host = "fixture-small", Cpu = measured.Cpu with { LogicalProcessors = 2 },
    Resources = measured.Resources with { Cpu = measured.Resources.Cpu with { Busy = 81 } } };
Check(ResourceTree.Build(false, [measured, smallerHost], (_, _) => true, false).Reading.Active == 41,
    "multi-host CPU is weighted by logical processor capacity");
Check(DiskIoMerge.Sum(measured.Resources.Files.Select(row => row.Totals)).Equals(decoded.DiskPaths.Totals()),
    "resource file measurement conserves read, write and operation counts");
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

var historical = StoredFileWindow.Decode(stored.GetRawText());
Check(historical.Encode() == stored.GetRawText(), "file history keeps the original OTLP body on replay");
Check(historical.DurationSeconds() == 58.5 && historical.Host == host && historical.NamedBytes == 3004 &&
    DiskIoMerge.Sum(historical.Rows.Select(r => r.ToDiskRow(host).Totals)).Equals(window.DiskPaths.Totals()),
    "history preserves the measured interval, coverage and four directional counters");

var starting = new DiskPathWindow(time, 0, 2, "Starting Windows file I/O trace...", 0, 0, 0, 0, []);
Check(FileWindowExport.IsCollectionStatus(starting), "a startup status has no measured disk interval");
Check(!FileWindowExport.IsCollectionStatus(starting with { Seconds = 5 }), "a measured idle window is exportable");
Check(!FileWindowExport.IsCollectionStatus(starting with { EventsLost = 1 }), "event losses cannot be skipped as startup");
Check(!FileWindowExport.IsCollectionStatus(starting with { Files = window.DiskPaths.Files }), "rows cannot be skipped as startup");
Console.WriteLine("Passed sample replay, process identities, scope and directional guest accounting.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Failed " + name);
}
