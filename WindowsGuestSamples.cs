using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed class WindowsGuestSamples : IAsyncDisposable
{
    private readonly VolumePathIndex paths;
    private readonly CancellationTokenSource stop = new();
    private readonly GuestDiskWatches watches = new();
    private Task<(CpuExpansionTarget[] Targets, Dictionary<string, VmDiskOwners> Disks, string Status)> discovery;
    private CpuExpansionTarget[] targets = [];
    private Dictionary<string, VmDiskOwners> disks;
    private string inventoryStatus, cpuStatus;
    private long discovered;
    private Task<GuestCpuWindow> pendingCpu;
    private GuestCpuWindow lastCpu;

    internal VmDiskOwners FindOwner(string path) => disks?.GetValueOrDefault(VmDiskIo.Normalize(path));

    public WindowsGuestSamples(VolumePathIndex paths)
    {
        this.paths = paths;
        RefreshInventory();
    }

    private void RefreshInventory()
    {
        var cancellation = stop.Token;
        discovered = Stopwatch.GetTimestamp();
        discovery = Task.Run(async () =>
        {
            CpuExpansionTarget[] found = [];
            try
            {
                found = CpuProcessSampler.ReadTargets();
                return (found, await VmDiskIo.ReadInventory(cancellation, paths), (string)null);
            }
            catch (Exception e) when (!cancellation.IsCancellationRequested) { return (found, null, e.Message); }
        }, cancellation);
    }

    public async Task Begin(Task<CpuProcessSample> host, TimeSpan interval)
    {
        if (discovery is { IsCompleted: true })
        {
            (targets, disks, inventoryStatus) = await discovery;
            discovery = null;
            VmDiskIo.RetainCpuSessions(targets.Where(t => !t.IsWsl).Select(t => t.Name));
        }
        if (discovery == null && Stopwatch.GetElapsedTime(discovered) >= TimeSpan.FromMinutes(1))
            RefreshInventory();
        await ConsumeCpu();
        if (pendingCpu == null && targets.Length > 0)
        {
            var selected = targets;
            var cancellation = stop.Token;
            pendingCpu = Task.Run(async () =>
            {
                var sample = await CpuProcessSampler.ReadCluster(selected, cancellation, host, interval);
                return new GuestCpuWindow(sample.Host.End.Value, sample);
            }, cancellation);
        }
    }

    private async Task ConsumeCpu()
    {
        if (pendingCpu is { IsCompleted: true })
        {
            try { lastCpu = await pendingCpu; cpuStatus = null; }
            catch (Exception e) when (!stop.IsCancellationRequested) { lastCpu = null; cpuStatus = e.Message; }
            finally { pendingCpu = null; }
        }
    }

    public async Task<GuestWindow> Read(VolumeFileIo.Snapshot snapshot, TimeSpan interval)
    {
        await ConsumeCpu();
        var selected = targets.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = GuestDiskWatches.Candidates(snapshot.Entries, disks, selected);
        watches.Track(groups.Select(g => g.Key), selected);
        var guests = groups.Select(group =>
        {
            var disk = group.Key;
            var sample = watches.Read(disk);
            var files = sample.Paths.Select(p => new MeasuredFile(p.Key, p.Value)).ToArray();
            return new GuestDiskWindow(disk.VMName, disk.ExpansionTarget, sample.Device,
                group.Select(p => p.Key.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                sample.Seconds, files, sample.Limited, sample.Error);
        }).ToArray();
        var cpu = lastCpu != null && snapshot.End - lastCpu.End <= interval + TimeSpan.FromSeconds(10) ? lastCpu : null;
        return new GuestWindow(cpu, guests, inventoryStatus, cpuStatus);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        watches.Dispose();
        try
        {
            await Task.WhenAll((Task)discovery ?? Task.CompletedTask, (Task)pendingCpu ?? Task.CompletedTask);
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        finally
        {
            VmDiskIo.RetainCpuSessions(Array.Empty<string>());
            stop.Dispose();
        }
    }
}
