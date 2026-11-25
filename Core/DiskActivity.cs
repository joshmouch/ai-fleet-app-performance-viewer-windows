public static class DiskActivity
{
    public const int WindowSeconds = 60;
    public const int DefaultActiveCutoff = 25;
    public const int DefaultTransferCutoff = 1;

    public static double Transfer(ResourceReading reading) => reading.Read.GetValueOrDefault() + reading.Write.GetValueOrDefault();
    public static bool HasActivity(ResourceReading reading) => reading.Active > 0 || Transfer(reading) > 0;
    public static bool MeetsCutoffs(ResourceReading reading, double activeCutoff, double transferCutoff) =>
        HasActivity(reading) && (reading.Active == null || reading.Active >= activeCutoff) && Transfer(reading) >= transferCutoff;

}
