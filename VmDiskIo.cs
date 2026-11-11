using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal sealed record VmDisk(string VMName, string Path, string Controller, int Location, bool SingleScsiController,
    string WslDistro = null)
{
    public string ExpansionTarget => WslDistro == null ? VMName : "WSL";
}

internal sealed record VmDiskOwners(string Label, VmDisk Attachment);

internal sealed record GuestIoRow(int Pid, string Process, string Path, long ReadBytes, long WriteBytes, long Reads, long Writes);

internal sealed record GuestIoSample(string Device, int Seconds, GuestIoRow[] Rows, bool Limited,
    double DiskSeconds, long DiskReadBytes, long DiskWriteBytes);

internal static class VmDiskIo
{
    public static async Task<Dictionary<string, VmDiskOwners>> ReadInventory(CancellationToken cancellation)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $rows = foreach ($vm in (Get-VM)) {
                $one = @(Get-VMScsiController -VM $vm).Count -eq 1
                foreach ($drive in (Get-VMHardDiskDrive -VM $vm)) {
                    if (!$drive.Path) { continue }
                    $path = $drive.Path
                    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                    while ($path -and $seen.Add($path)) {
                        [pscustomobject]@{ VMName = $vm.Name; Path = $path; Controller = [string]$drive.ControllerType;
                            Location = [int]$drive.ControllerLocation; SingleScsiController = $one }
                        if ([IO.Path]::GetExtension($path) -ine '.avhdx') { break }
                        $path = (Get-VHD -Path $path -ErrorAction SilentlyContinue).ParentPath
                    }
                }
            }
            ConvertTo-Json -InputObject @($rows) -Compress
            """;
        var wsl = CpuProcessSampler.ReadRunningWslDistros(cancellation);
        VmDisk[] hyperV;
        try { hyperV = await ReadHyperV<VmDisk[]>(script, cancellation).ConfigureAwait(false); }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            if ((await wsl.ConfigureAwait(false)).Length == 0)
                throw;
            hyperV = Array.Empty<VmDisk>();
        }
        var attachments = hyperV.Concat((await wsl.ConfigureAwait(false)).Where(distro => distro.Vhdx != null)
            .Select(distro => new VmDisk("WSL / " + distro.Name, distro.Vhdx, "", -1, false, distro.Name)));
        return attachments.Where(disk => !string.IsNullOrEmpty(disk?.Path))
            .GroupBy(disk => Normalize(disk.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(
            group => group.Key, group =>
            {
                var disks = group.Distinct().ToArray();
                return new VmDiskOwners(string.Join(", ", disks.Select(x => x.VMName).Distinct().Order()),
                    disks.Length == 1 ? disks[0] : null);
            }, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<T> ReadHyperV<T>(string script, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        string executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await Run(executable, new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }, null, deadline.Token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Hyper-V inventory unavailable.");
        return JsonSerializer.Deserialize<T>(result.Output);
    }

    public static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    public static Task<GuestIoSample> Sample(VmDisk disk, CancellationToken cancellation)
    {
        if (disk.WslDistro != null)
            return ReadWslDisk(disk.WslDistro, cancellation);
        if (disk.Controller != "SCSI" || !disk.SingleScsiController)
            throw new InvalidOperationException("Guest disk matching requires a VM with one Hyper-V SCSI controller.");
        if (!IsGuestAddress(disk.VMName) || disk.Location < 0 || disk.Location > 63)
            throw new InvalidOperationException("This VM does not have a supported guest address.");
        return ReadGuest<GuestIoSample>(disk.VMName, "GuestDiskIo.py", "I/O", 28,
            new[] { "sudo", "-n", "/usr/bin/python3", "-I", "-", disk.Location.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellation);
    }

    private static async Task<GuestIoSample> ReadWslDisk(string distro, CancellationToken cancellation)
    {
        string code = await ReadScript("GuestDiskIo.py", cancellation).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(28));
        var result = await Run(CpuProcessSampler.Wsl,
            new[] { "--distribution", distro, "--user", "root", "--exec", "/usr/bin/python3", "-I", "-", "root" },
            code, deadline.Token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            string error = result.Error.Split('\n').FirstOrDefault(line => line.StartsWith("Guest disk I/O: ", StringComparison.Ordinal));
            throw new InvalidOperationException(error?.TrimEnd() ??
                $"Could not run the WSL disk sampler in {distro}. It requires Python 3 and bpftrace.");
        }
        return JsonSerializer.Deserialize<GuestIoSample>(result.Output)
            ?? throw new InvalidOperationException("WSL returned no guest disk sample.");
    }

    public static Task<CpuProcessSample> SampleCpu(string vmName, CancellationToken cancellation) =>
        ReadGuest(vmName, "GuestCpu.sh", "CPU", 12, new[] { "/bin/sh", "-s" }, cancellation, CpuProcessSampler.ParseGuest);

    private static string SettingsPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFleetPerformanceViewer", "settings.json");

    /// <summary>DNS domain appended to a VM name to reach it over SSH; null when not configured.</summary>
    internal static string GuestDomain()
    {
        string domain = Environment.GetEnvironmentVariable("PERFORMANCE_VIEWER_GUEST_DOMAIN");
        if (string.IsNullOrWhiteSpace(domain))
        {
            try
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath()));
                if (settings.RootElement.TryGetProperty("guestDomain", out var value)) domain = value.GetString();
            }
            catch (Exception) { }
        }
        domain = domain?.Trim().Trim('.');
        return !string.IsNullOrEmpty(domain) && Regex.IsMatch(domain, @"\A[a-zA-Z0-9.-]+\z") ? domain : null;
    }

    internal static bool IsGuestAddress(string name) =>
        name != null && Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,62}\z");

    private static async Task<T> ReadGuest<T>(string vmName, string script, string label, int seconds,
        string[] command, CancellationToken cancellation, Func<string, T> parse = null) where T : class
    {
        if (!IsGuestAddress(vmName))
            throw new InvalidOperationException("This VM does not have a supported guest address.");
        string domain = GuestDomain();
        if (domain == null)
            throw new InvalidOperationException("Guest sampling needs the VMs' DNS domain. Set the PERFORMANCE_VIEWER_GUEST_DOMAIN environment variable, or guestDomain in " + SettingsPath() + ".");
        string key = Environment.GetEnvironmentVariable("NODES_SSH_KEY");
        if (string.IsNullOrWhiteSpace(key))
            key = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ai-fleet", "config", "secrets", "ssh", "fleet-controller");
        if (!File.Exists(key))
            throw new InvalidOperationException("The cluster SSH key is unavailable on this host.");

        string code = await ReadScript(script, cancellation).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        CommandResult result;
        try
        {
            result = await Run(System.IO.Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"), new[]
            {
                "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "IdentitiesOnly=yes",
                "-o", "ConnectTimeout=5", "-i", key, "fleet@" + vmName + "." + domain
            }.Concat(command), code, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Guest {label} sampling did not finish.");
        }
        if (result.ExitCode != 0)
        {
            string guestError = result.Error.Split('\n').FirstOrDefault(line => line.StartsWith("Guest disk I/O: ", StringComparison.Ordinal));
            throw new InvalidOperationException(guestError?.TrimEnd() ??
                (result.ExitCode == 255 ? "Could not connect to the VM with the configured cluster SSH identity."
                    : $"The VM could not run the guest {label} sampler."));
        }
        return (parse == null ? JsonSerializer.Deserialize<T>(result.Output) : parse(result.Output))
            ?? throw new InvalidOperationException($"The VM returned no guest {label} sample.");
    }

    internal static async Task<string> ReadScript(string script, CancellationToken cancellation)
    {
        using var resource = typeof(VmDiskIo).Assembly.GetManifestResourceStream(script);
        using var reader = new StreamReader(resource, Encoding.UTF8);
        return (await reader.ReadToEndAsync(cancellation).ConfigureAwait(false)).Replace("\r\n", "\n");
    }

    internal sealed record CommandResult(int ExitCode, string Output, string Error);

    internal static async Task<CommandResult> Run(string executable, IEnumerable<string> arguments, string input,
        CancellationToken cancellation, Encoding outputEncoding = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input != null, StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (input != null)
            start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        using var registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        if (input != null)
        {
            try { await process.StandardInput.WriteAsync(input.AsMemory(), cancellation).ConfigureAwait(false); }
            catch (IOException) { } // An SSH failure can close stdin before returning its diagnostic.
            finally { process.StandardInput.Close(); }
        }
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
