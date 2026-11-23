public static class DiskActivity
{
    public const int WindowSeconds = 60;
    public const int DefaultActiveCutoff = 25;
    public const int DefaultTransferCutoff = 1;

    public static ResourceReading Average(IEnumerable<(ResourceReading Reading, double Seconds)> intervals)
    {
        double active = 0, activeSeconds = 0, read = 0, readSeconds = 0, write = 0, writeSeconds = 0;
        foreach (var (sample, seconds) in intervals)
        {
            if (seconds <= 0) continue;
            Accumulate(sample.Active, seconds, ref active, ref activeSeconds);
            Accumulate(sample.Read, seconds, ref read, ref readSeconds);
            Accumulate(sample.Write, seconds, ref write, ref writeSeconds);
        }
        return new(Mean(active, activeSeconds), Mean(read, readSeconds), Mean(write, writeSeconds));
    }

    public static double Transfer(ResourceReading reading) => reading.Read.GetValueOrDefault() + reading.Write.GetValueOrDefault();
    public static bool HasActivity(ResourceReading reading) => reading.Active > 0 || Transfer(reading) > 0;
    public static bool MeetsCutoffs(ResourceReading reading, double activeCutoff, double transferCutoff) =>
        HasActivity(reading) && (reading.Active == null || reading.Active >= activeCutoff) && Transfer(reading) >= transferCutoff;

    public static double RelativeHeat(ResourceReading reading, double peakActive, double peakTransfer) =>
        Math.Max(peakActive > 0 ? reading.Active.GetValueOrDefault() / peakActive : 0,
            peakTransfer > 0 ? Transfer(reading) / peakTransfer : 0);

    private static void Accumulate(double? value, double seconds, ref double total, ref double duration)
    {
        if (value is not double measured || !double.IsFinite(measured)) return;
        total += measured * seconds;
        duration += seconds;
    }

    private static double? Mean(double amount, double duration) => duration > 0 ? amount / duration : null;
}
