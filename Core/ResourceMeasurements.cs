#nullable enable

public sealed record ProcessorMeasurement(string Id, double Busy);

public sealed record CpuResourceMeasurements(double? Busy, ProcessorMeasurement[] Processors,
    double? Windows = null, double? Guests = null, double? Hypervisor = null);

public sealed record DiskResourceMeasurement(int Number, string Name, string Kind, string[] Mounts,
    bool StorageSpaces, double? ReadMiB, double? WriteMiB, double? Active);

public sealed record DiskFileMeasurement(int Number, FileIoKey File, FileIoTotals Totals);

public sealed record VirtualDiskMeasurement(int Number, string Path, string? Machine, string? Target);

public sealed record ResourceMeasurements(CpuResourceMeasurements? Cpu, DiskResourceMeasurement[] Disks,
    DiskFileMeasurement[] Files, VirtualDiskMeasurement[] VirtualDisks);
