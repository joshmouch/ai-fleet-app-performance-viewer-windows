using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class WindowsSampleStream
{
    internal static async Task<int> Run(string[] args)
    {
        int interval = 10;
        int? disk = null, count = null;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None,
                CultureInfo.InvariantCulture, out int value))
                return Usage();
            switch (args[i])
            {
                case "--interval" when value is >= 1 and <= 3600: interval = value; break;
                case "--count" when value > 0: count = value; break;
                case "--disk" when value >= 0: disk = value; break;
                default: return Usage();
            }
        }

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var index = new VolumePathIndex(Array.Empty<DiskVolume>());
            try
            {
                var disks = WindowsDiskInventory.LoadDiskIdentities();
                index = new VolumePathIndex(disks.Values.Where(d => d.Volumes != null).SelectMany(d => d.Volumes));
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Volume inventory unavailable. Native paths are retained. " + ex.Message);
            }

            var captureId = Guid.NewGuid();
            DateTimeOffset? boot = ReadBoot();
            using var capture = new VolumeFileIo(true, Array.Empty<int>(), index);
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            for (long sequence = 1; !count.HasValue || sequence <= count; sequence++)
            {
                var cpu = await CpuProcessSampler.ReadHost(stop.Token, TimeSpan.FromSeconds(interval));
                var cpuEnd = DateTimeOffset.UtcNow;
                var paths = capture.Read(true, disk, null, false);
                var pathWindow = new DiskPathWindow(paths.End, paths.Seconds, disk, paths.Status, paths.EventsLost,
                    paths.DiskBytes, paths.NamedBytes, paths.KeylessBytes,
                    paths.Entries.Select(pair => new MeasuredFile(pair.Key, pair.Value)).ToArray());
                var frame = new PerformanceWindow(PerformanceWindow.CurrentSchema, Environment.MachineName, boot,
                    captureId, sequence, cpuEnd, cpu, pathWindow);
                await output.WriteLineAsync(frame.Encode());
                await output.FlushAsync();
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static DateTimeOffset? ReadBoot()
    {
        try
        {
            using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            using var rows = query.Get();
            foreach (ManagementObject row in rows)
                using (row)
                    if (row["LastBootUpTime"] is string boot)
                        return ManagementDateTimeConverter.ToDateTime(boot).ToUniversalTime();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }
        return null;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: --collect [--interval SECONDS] [--disk NUMBER] [--count WINDOWS]");
        return 2;
    }
}
