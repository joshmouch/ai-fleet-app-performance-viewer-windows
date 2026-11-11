using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

internal readonly record struct CpuTimeSplit(double Windows, double VirtualMachines, double Hypervisor)
{
    public double Total => Windows + VirtualMachines + Hypervisor;
}

internal sealed record HyperVHostSample(Dictionary<int, CpuTimeSplit> Processors, CpuTimeSplit Overall);

internal sealed class HyperVHostCounters : IDisposable
{
    private readonly Dictionary<int, int> rootByLogicalProcessor;
    private readonly PdhQuery query;

    [DllImport("computecore.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int HcsGetServiceProperties(string query, out IntPtr result);

    public HyperVHostCounters()
    {
        rootByLogicalProcessor = ReadTopology();
        query = new PdhQuery(
            @"\Hyper-V Hypervisor Logical Processor(*)\% Guest Run Time",
            @"\Hyper-V Hypervisor Logical Processor(*)\% Hypervisor Run Time",
            @"\Hyper-V Hypervisor Root Virtual Processor(*)\% Guest Run Time");
    }

    public HyperVHostSample Read()
    {
        query.Collect();
        var osTime = query.Read(0);
        var hypervisorTime = query.Read(1);
        var windowsTime = query.Read(2);
        var processors = new Dictionary<int, CpuTimeSplit>();
        foreach (var entry in rootByLogicalProcessor)
        {
            string logical = $"Hv LP {entry.Key}";
            double root = 0;
            if (!osTime.TryGetValue(logical, out double os) ||
                !hypervisorTime.TryGetValue(logical, out double hypervisor) ||
                (entry.Value >= 0 && !windowsTime.TryGetValue($"Root VP {entry.Value}", out root)))
                return null;

            // Logical-processor guest time includes the root partition.
            // Bound small differences between the root and logical counter samples.
            hypervisor = Math.Clamp(hypervisor, 0, 100);
            os = Math.Clamp(os, 0, 100 - hypervisor);
            root = Math.Clamp(root, 0, os);
            processors.Add(entry.Key, new CpuTimeSplit(root, os - root, hypervisor));
        }
        return new HyperVHostSample(processors, new CpuTimeSplit(
            processors.Values.Average(cpu => cpu.Windows),
            processors.Values.Average(cpu => cpu.VirtualMachines),
            processors.Values.Average(cpu => cpu.Hypervisor)));
    }

    private static Dictionary<int, int> ReadTopology()
    {
        const string properties = """{"PropertyTypes":["ProcessorTopology"]}""";
        int hr = HcsGetServiceProperties(properties, out IntPtr result);
        try
        {
            if (hr < 0 || result == IntPtr.Zero)
                throw new InvalidOperationException("Hyper-V processor topology is unavailable.");

            using var document = JsonDocument.Parse(Marshal.PtrToStringUni(result));
            var topology = document.RootElement.GetProperty("Properties")[0].GetProperty("LogicalProcessors");
            var roots = new Dictionary<int, int>();
            foreach (var cpu in topology.EnumerateArray())
                roots.Add(cpu.GetProperty("LpIndex").GetInt32(), cpu.GetProperty("RootVpIndex").GetInt32());
            if (roots.Count == 0)
                throw new InvalidOperationException("Hyper-V returned no logical processors.");
            return roots;
        }
        finally { Marshal.FreeCoTaskMem(result); }
    }

    public void Dispose() => query.Dispose();
}
