public sealed record CpuProcessRow(int Pid, string Process, double Cores, double? GuestCpuShare = null,
    double? AttributedCores = null, long Started = 0);
public sealed record CpuProcessSample(int LogicalProcessors, double Seconds, CpuProcessRow[] Rows,
    double TotalCores = 0, double ProcessCores = 0, DateTimeOffset? End = null);
public sealed record CpuMachineProcesses(string Name, CpuProcessRow[] Rows, string Target = null);
/// <param name="Failure">Why the guest sample is unavailable, as the session reported it; null when it is available or no cause is known.</param>
public sealed record GuestCpuCoverage(string Machine, double? HostCores, double AttributedCores, bool SampleAvailable, string Failure = null, CpuProcessRow[] ReplacedRows = null);
public sealed record ClusterCpuSample(CpuProcessSample Host, CpuMachineProcesses[] Guests, GuestCpuCoverage[] Coverage);
