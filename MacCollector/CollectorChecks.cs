static class CollectorChecks
{
    public static void Run()
    {
        var w = new PhysicalWindow("host");
        ProcessOwner Owner(ulong tid) => tid == 900001 ? new(123, "public-worker", 42) : null;
        void Accept(string op, string bytes, string path, ulong tid) => w.Accept($"12:00:00.000001 {op}[S] D=0x123 B=0x{bytes} /dev/disk3 {path} 0.000010 W worker.{tid}", Owner);
        Accept("RdData", "1000", "/tmp/lost dropped fixture", 900001);
        Accept("WrData", "2000", "/tmp/lost dropped fixture", 900001);
        Accept("RdMeta", "1000", "", 900001);
        Accept("WrData", "3000", "not-attributed", 123);
        Accept("RdData", "1000", "not-attributed", 124);
        var capture = Guid.NewGuid();
        var f = PerformanceWindow.Decode(w.Frame(1, capture, 1).Encode());
        var known = f.DiskPaths.Files.Single(x => x.File.Pid == 123 && x.File.Path != "").Totals;
        var unknown = f.DiskPaths.Files.Single(x => x.File.Pid == 0 && x.File.Path == "not-attributed").Totals;
        if (known.ReadBytes != 4096 || known.WriteBytes != 8192 || known.Reads != 1 || known.Writes != 1 ||
            unknown.WriteBytes != 12288 || unknown.Writes != 1 || unknown.ReadBytes != 4096 || unknown.Reads != 1 ||
            f.DiskPaths.CapturedBytes != 32768 || f.DiskPaths.KeylessBytes != 4096 ||
            f.Cpu.Rows.Length != 0 || f.CpuStatus == null) throw new Exception("accounting/capability");
        _ = FileWindowExport.Encode(f);
        if (w.Frame(1, capture, 2).DiskPaths.CapturedBytes != f.DiskPaths.CapturedBytes) throw new Exception("exit accounting");
        w.Accept("lost events", Owner); w.Accept("WrData malformed", Owner);
        if (!w.Error || w.Frame(1, capture, 3).DiskPaths.EventsLost != 1) throw new Exception("loss/error");
        var scoped = f with { DiskPaths = f.DiskPaths with { Scope = "process:123" } };
        try { _ = FileWindowExport.Encode(scoped); throw new Exception("process exported as host"); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS: host/process scope, known and unknown four counters, exit retention, CPU unsupported, loss/error, exporter");
    }
}
