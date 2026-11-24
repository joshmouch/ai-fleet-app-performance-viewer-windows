using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class WindowsResourceCounters : IDisposable
{
    private readonly Dictionary<int, DiskIdentity> inventory;
    private readonly CpuCounters cpu = TryOpen(() => new CpuCounters());
    private readonly DiskCounters disks = TryOpen(() => new DiskCounters());
    private readonly HyperVHostCounters hypervisor = TryOpen(() => new HyperVHostCounters());

    public WindowsResourceCounters(Dictionary<int, DiskIdentity> inventory) => this.inventory = inventory;

    public ResourceMeasurements Read(DiskFileMeasurement[] files, WindowsGuestSamples guests)
    {
        double? busy = null;
        ProcessorMeasurement[] processors = [];
        try
        {
            if (cpu != null)
                processors = cpu.Read(out busy).Select(row => new ProcessorMeasurement(row.Key, row.Value)).ToArray();
        }
        catch (InvalidOperationException) { }
        HyperVHostSample split = null;
        try { split = hypervisor?.Read(); }
        catch (InvalidOperationException) { }
        var cpuSample = new CpuResourceMeasurements(split?.Overall.Total ?? busy, processors,
            split?.Overall.Windows, split?.Overall.VirtualMachines, split?.Overall.Hypervisor);

        DiskReading[] rates = [];
        try
        {
            if (disks != null) rates = disks.Read().Values.ToArray();
        }
        catch (InvalidOperationException) { }
        var byNumber = rates.GroupBy(rate => rate.Number).ToDictionary(group => group.Key, group => group.First());
        var measured = byNumber.Keys.Concat(files.Select(row => row.Number)).Distinct().Select(number =>
        {
            var rate = byNumber.TryGetValue(number, out var available) ? (DiskReading?)available : null;
            var identity = inventory.GetValueOrDefault(number);
            return new DiskResourceMeasurement(number, identity?.Name ?? $"Disk {number}", identity?.Kind,
                identity?.AccessPaths ?? [], identity?.IsStorageSpaces ?? false, rate?.ReadMiB, rate?.WriteMiB, rate?.Active);
        }).ToArray();
        var backing = files.Where(row => FileIoKey.IsVirtualDisk(row.File.Path))
            .Select(row => (row.Number, Path: VmDiskIo.Normalize(row.File.Path))).Distinct()
            .Select(row =>
            {
                var owner = guests?.FindOwner(row.Path);
                return new VirtualDiskMeasurement(row.Number, row.Path, owner?.Label, owner?.Attachment?.ExpansionTarget);
            }).ToArray();
        return new ResourceMeasurements(cpuSample, measured, files, backing);
    }

    private static T TryOpen<T>(Func<T> open) where T : class
    {
        try { return open(); }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { return null; }
    }

    public void Dispose()
    {
        cpu?.Dispose();
        disks?.Dispose();
        hypervisor?.Dispose();
    }
}
