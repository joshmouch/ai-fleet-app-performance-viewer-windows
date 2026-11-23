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
        bool collectGuests = false, collectResources = false;
        string archiveDirectory = null;
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (option == "--guests") { collectGuests = true; continue; }
            if (option == "--resources") { collectResources = true; continue; }
            if (option == "--archive" && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--"))
            {
                archiveDirectory = args[++i];
                continue;
            }
            if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None,
                CultureInfo.InvariantCulture, out int value))
                return Usage();
            switch (option)
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
            await using var archive = archiveDirectory == null ? null : new FileWindowArchive(archiveDirectory, stop.Token);
            var index = new VolumePathIndex(Array.Empty<DiskVolume>());
            var disks = new System.Collections.Generic.Dictionary<int, DiskIdentity>();
            try
            {
                disks = WindowsDiskInventory.LoadDiskIdentities();
                index = new VolumePathIndex(disks.Values.Where(d => d.Volumes != null).SelectMany(d => d.Volumes));
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Volume inventory unavailable. Native paths are retained. " + ex.Message);
            }

            var captureId = Guid.NewGuid();
            DateTimeOffset? boot = ReadBoot();
            await using var guestSamples = collectGuests ? new WindowsGuestSamples(index) : null;
            using var resourceCounters = collectResources ? new WindowsResourceCounters(disks) : null;
            using var capture = new VolumeFileIo(true, Array.Empty<int>(), index);
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            for (long sequence = 1; !count.HasValue || sequence <= count; sequence++)
            {
                var duration = TimeSpan.FromSeconds(interval);
                var host = CpuProcessSampler.ReadHost(stop.Token, duration);
                if (guestSamples != null) await guestSamples.Begin(host, duration);
                var cpu = await host;
                var paths = capture.Read(true, disk, null, false, includeDiskFiles: collectResources);
                var pathWindow = new DiskPathWindow(paths.End, paths.Seconds, disk, paths.Status, paths.EventsLost,
                    paths.DiskBytes, paths.NamedBytes, paths.KeylessBytes,
                    paths.Entries.Select(pair => new MeasuredFile(pair.Key, pair.Value)).ToArray());
                var frame = new PerformanceWindow(PerformanceWindow.CurrentSchema, Environment.MachineName, boot,
                    captureId, sequence, cpu.End.Value, cpu, pathWindow,
                    guestSamples == null ? null : await guestSamples.Read(paths, duration),
                    Resources: resourceCounters?.Read(paths.DiskFiles, guestSamples));
                archive?.Record(frame);
                await output.WriteLineAsync(frame.Encode());
                await output.FlushAsync();
            }
            if (archive != null) await archive.FinishAsync();
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidDataException or FormatException)
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
        Console.Error.WriteLine("Usage: --collect [--guests] [--resources] [--archive DIRECTORY] [--interval SECONDS] [--disk NUMBER] [--count WINDOWS]");
        return 2;
    }
}
