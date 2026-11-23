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
Check(ResourceTree.Build(true, [measured], (_, _) => true, true).Reading.Write == 36, "explicit all-disks scope");
var processor = ResourceTree.Build(false, [measured], (_, _) => true, false).Children.Single().Children.Single();
Check(processor.Reading.Active == 42 && processor.HostShare == 42.0 / (100 * measured.Cpu.LogicalProcessors),
    "a logical processor shows its own busy percentage and colors by its contribution to host capacity");
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

var otherBacking = new FileIoKey(4, "System", @"E:\vm\buildkit.vhdx");
var otherHost = new FileIoTotals { ReadBytes = 661, WriteBytes = 997, Reads = 2, Writes = 11 };
var twoDisks = layered with
{
    DiskPaths = layered.DiskPaths with { Files = [..layered.DiskPaths.Files, new(otherBacking, otherHost)] },
    Guests = layered.Guests with
    {
        Disks =
        [
            ..layered.Guests.Disks,
            new("WSL / distro", "WSL", "/dev/sdb", [otherBacking.Path], 4,
                [new(new(35, "buildkitd", "/cache/layers"), new() { ReadBytes = 200, WriteBytes = 900, Reads = 2, Writes = 5 })],
                false, null)
        ]
    },
    Resources = measured.Resources with
    {
        Disks = [..measured.Resources.Disks, new(3, "Second volume", "Volume", [@"E:\"], false, 5, 11, 55)],
        Files = [..measured.Resources.Files, new(3, otherBacking, otherHost)],
        VirtualDisks = [ new(2, backing.Path, "WSL / distro", "WSL"), new(3, otherBacking.Path, "WSL / distro", "WSL") ]
    }
};
var storageLayout = ResourceTree.Build(true, [twoDisks], (_, _) => true, false, DiskGrouping.Storage);
var workloadLayout = ResourceTree.Build(true, [twoDisks], (_, _) => true, false, DiskGrouping.Workloads);
var flatLayout = ResourceTree.Build(true, [twoDisks], (_, _) => true, false, DiskGrouping.Vhdx);
Check(storageLayout.Reading == workloadLayout.Reading && storageLayout.Reading == flatLayout.Reading &&
    storageLayout.Children.All(disk => disk.Kind == ResourceKind.Disk) &&
    flatLayout.Children.All(disk => disk.Kind == ResourceKind.VirtualDisk) && flatLayout.Children.Length == 2,
    "disk layout changes tree shape without changing the system measurements");
var workload = workloadLayout.Children.Single(node => node.Kind == ResourceKind.VirtualMachine);
var windowsWorkload = workloadLayout.Children.Single(node => node.Kind == ResourceKind.WindowsIo);
Check(workload.Children.Length == 2 && workload.Type == "WSL distribution" &&
    workload.Children.Select(node => node.Id).ToHashSet().SetEquals(flatLayout.Children.Select(node => node.Id)),
    "two volumes backing one workload share one parent while keeping their VHDX identities");
var narrowed = workload.Children.Single(node => node.BackingPath == backing.Path);
var captureBytes = twoDisks.DiskPaths.Totals().TotalBytes;
var vhdxShare = (double)raw[backing].TotalBytes / captureBytes;
var storedVhdx = ResourceTree.Walk(storageLayout).Single(node => node.Id == narrowed.Id);
var flatVhdx = flatLayout.Children.Single(node => node.Id == narrowed.Id);
Check(narrowed.HostShare == vhdxShare && storedVhdx.HostShare == vhdxShare && flatVhdx.HostShare == vhdxShare &&
    narrowed.Children.Single().HostShare == vhdxShare && windowsWorkload.HostShare == (double)raw[unknown].TotalBytes / captureBytes,
    "card heat uses one host byte denominator across all groupings and uses backing I/O for guest devices");
Check(Math.Abs(workload.HostShare.GetValueOrDefault() + windowsWorkload.HostShare.GetValueOrDefault() - 1) < 1e-12,
    "workload heat includes the whole host budget, including unresolved Windows traffic");
var sharedBacking = twoDisks with { Guests = twoDisks.Guests with
    { Disks = [..twoDisks.Guests.Disks, new("guest-two", "guest-two", "/dev/sdc", [backing.Path], 4, [], false, null)] } };
var visibleDevice = ResourceTree.Build(true, [sharedBacking], (_, target) => target != "guest-two", false, DiskGrouping.Vhdx)
    .Children.Single(node => node.Id == narrowed.Id).Children.Single();
Check(visibleDevice.HostShare == null,
    "hiding a guest device with a shared backing does not turn the other device into the owner of the full host budget");
foreach (var node in new[] { narrowed, storageLayout.Children.Single(node => node.Disk == 2) })
{
    var rows = ResourceDiskScope.Rows(twoDisks, node, _ => true, false);
    var expected = node.Kind == ResourceKind.VirtualDisk ? raw[backing] : window.DiskPaths.Totals();
    Check(rows.Any(row => row.Key.Path == "/repo/a") && rows.All(row => row.Key.Path != "/cache/layers") &&
        DiskIoMerge.Sum(rows.Select(row => row.Totals)).Equals(expected),
        "volume and VHDX exclude sibling guest disk paths and preserve each directional total");
}
var workloadRows = ResourceDiskScope.Rows(twoDisks, workload, _ => true, false);
Check(workloadRows.Any(row => row.Key.Path == "/cache/layers") &&
    DiskIoMerge.Sum(workloadRows.Select(row => row.Totals)).Equals(DiskIoMerge.Sum([raw[backing], otherHost])),
    "workload includes both backing volumes but excludes native Windows activity");
Check(ResourceDiskScope.Rows(twoDisks, windowsWorkload, _ => true, false).Single().Totals.Equals(raw[unknown]) &&
    ResourceDiskScope.Rows(twoDisks, narrowed, _ => false, false).Single().Totals.Equals(raw[backing]),
    "native workload keeps unresolved host IO and disabling expansion restores the full backing file budget");
Check(ResourceDiskScope.DeviceWindows(twoDisks, narrowed.Children.Single()).Single().Device == "/dev/sda",
    "guest device details match the selected device and backing path");

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
