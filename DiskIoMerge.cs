using System;
using System.Collections.Generic;
using System.Linq;

internal sealed record DiskIoRow(FileIoKey Key, string Machine, FileIoTotals Totals, DiskGuestRate? Guest = null);

internal readonly record struct DiskGuestRate(double Read, double Write)
{
    public double Total => Read + Write;
}

internal sealed record DiskIoExpansion(string Machine, KeyValuePair<FileIoKey, FileIoTotals>[] HostPaths,
    Dictionary<FileIoKey, FileIoTotals> GuestRows, double GuestSeconds);

internal static class DiskIoMerge
{
    public static DiskIoRow[] Expand(Dictionary<FileIoKey, FileIoTotals> entries,
        DiskIoExpansion[] expansions, bool byPath)
    {
        var host = new Dictionary<FileIoKey, FileIoTotals>(entries);
        var guests = new Dictionary<(string Machine, FileIoKey Key), (FileIoTotals Host, double ReadRate, double WriteRate)>();
        foreach (var expansion in expansions)
        {
            var budget = Sum(expansion.HostPaths.Select(entry => entry.Value));
            var weights = Sum(expansion.GuestRows.Values);
            foreach (var entry in expansion.HostPaths)
            {
                var key = byPath ? entry.Key : entry.Key.ProcessKey;
                var remaining = host[key];
                if (weights.ReadBytes > 0) remaining.ReadBytes -= entry.Value.ReadBytes;
                if (weights.WriteBytes > 0) remaining.WriteBytes -= entry.Value.WriteBytes;
                if (weights.Reads > 0) remaining.Reads -= entry.Value.Reads;
                if (weights.Writes > 0) remaining.Writes -= entry.Value.Writes;
                host[key] = remaining;
            }

            FileIoTotals used = default, assigned = default;
            foreach (var entry in expansion.GuestRows)
            {
                used.Add(entry.Value);
                var cumulative = new FileIoTotals
                {
                    ReadBytes = Allocate(used.ReadBytes, weights.ReadBytes, budget.ReadBytes),
                    WriteBytes = Allocate(used.WriteBytes, weights.WriteBytes, budget.WriteBytes),
                    Reads = Allocate(used.Reads, weights.Reads, budget.Reads),
                    Writes = Allocate(used.Writes, weights.Writes, budget.Writes)
                };
                var key = (expansion.Machine, entry.Key);
                var row = guests.GetValueOrDefault(key);
                row.Host.Add(new FileIoTotals
                {
                    ReadBytes = cumulative.ReadBytes - assigned.ReadBytes,
                    WriteBytes = cumulative.WriteBytes - assigned.WriteBytes,
                    Reads = cumulative.Reads - assigned.Reads,
                    Writes = cumulative.Writes - assigned.Writes
                });
                double divisor = 1048576.0 * expansion.GuestSeconds;
                row.ReadRate += entry.Value.ReadBytes / divisor;
                row.WriteRate += entry.Value.WriteBytes / divisor;
                guests[key] = row;
                assigned = cumulative;
            }
        }
        return host.Where(entry => entry.Value.TotalBytes != 0 || entry.Value.Operations != 0)
            .Select(entry => new DiskIoRow(entry.Key, Environment.MachineName, entry.Value))
            .Concat(guests
                .Select(entry => new DiskIoRow(entry.Key.Key, entry.Key.Machine, entry.Value.Host, new DiskGuestRate(entry.Value.ReadRate, entry.Value.WriteRate))))
            .ToArray();
    }

    public static FileIoTotals Sum(IEnumerable<FileIoTotals> values)
    {
        FileIoTotals sum = default;
        foreach (var value in values)
            sum.Add(value);
        return sum;
    }

    // Round cumulative shares so the last row closes the exact byte and operation budget.
    private static long Allocate(long used, long total, long budget) =>
        total == 0 ? 0 : used == total ? budget : (long)((decimal)used / total * budget);
}
