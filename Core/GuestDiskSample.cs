public sealed record GuestIoRow(int Pid, string Process, string Path, long ReadBytes, long WriteBytes, long Reads, long Writes)
{
    public void AddTo(Dictionary<FileIoKey, FileIoTotals> files) =>
        FileIoTotals.AddTo(files, new(Pid, Process, Path),
            new() { ReadBytes = ReadBytes, WriteBytes = WriteBytes, Reads = Reads, Writes = Writes });
}

public sealed record GuestIoSample(string Device, int Seconds, GuestIoRow[] Rows, bool Limited,
    double DiskSeconds, long DiskReadBytes, long DiskWriteBytes);

public sealed record GuestDiskLine(Dictionary<string, GuestIoSample> Samples, Dictionary<string, string> Errors, DateTimeOffset? End = null);
