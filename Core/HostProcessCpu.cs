using System.ComponentModel;
using System.Diagnostics;

public sealed class HostProcessCpu
{
    private sealed record Reading(long Timestamp, DateTimeOffset End,
        Dictionary<(int Pid, long Started), (string Name, TimeSpan Time)> Processes, int Unavailable);

    private Reading previous = Read();
    public int Unavailable => previous.Unavailable;

    public CpuProcessSample Next(int logicalProcessors)
    {
        var before = previous;
        previous = Read();
        double seconds = Stopwatch.GetElapsedTime(before.Timestamp, previous.Timestamp).TotalSeconds;
        var rows = new List<CpuProcessRow>();
        foreach (var (identity, current) in previous.Processes)
        {
            if (before.Processes.TryGetValue(identity, out var first) && current.Time > first.Time)
                rows.Add(new(identity.Pid, current.Name, (current.Time - first.Time).TotalSeconds / seconds,
                    Started: identity.Started));
        }
        return new(Math.Max(1, logicalProcessors), seconds, rows.ToArray(),
            ProcessCores: rows.Sum(row => row.Cores), End: previous.End);
    }

    private static Reading Read()
    {
        var times = new Dictionary<(int, long), (string, TimeSpan)>();
        int unavailable = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == 0) continue;
                try
                {
                    times[(process.Id, process.StartTime.ToUniversalTime().Ticks)] =
                        (process.ProcessName, process.TotalProcessorTime);
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    unavailable++;
                }
            }
        }
        return new(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, times, unavailable);
    }
}
