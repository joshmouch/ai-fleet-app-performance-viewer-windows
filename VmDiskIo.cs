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
    /// <summary>The VM disk inventory. Runs on the thread pool, so its synchronous process start and WMI reads never run on
    /// the caller's UI thread.</summary>
    public static Task<Dictionary<string, VmDiskOwners>> ReadInventory(CancellationToken cancellation, VolumePathIndex paths) =>
        Task.Run(() => ReadInventoryOnPool(cancellation, paths), cancellation);

    private static async Task<Dictionary<string, VmDiskOwners>> ReadInventoryOnPool(CancellationToken cancellation,
        VolumePathIndex paths)
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
        // Registration owns the VHDX identity. Running state can change while the viewer is open.
        var wsl = CpuProcessSampler.ReadWslDistros().Where(distro => distro.Vhdx != null).ToArray();
        VmDisk[] hyperV;
        try { hyperV = await ReadHyperV<VmDisk[]>(script, cancellation).ConfigureAwait(false); }
        catch (Exception) when (!cancellation.IsCancellationRequested && wsl.Length > 0)
        {
            hyperV = Array.Empty<VmDisk>();
        }
        var attachments = hyperV.Concat(wsl
            .Select(distro => new VmDisk("WSL / " + distro.Name, distro.Vhdx, "", -1, false, distro.Name)));
        return attachments.Where(disk => !string.IsNullOrEmpty(disk?.Path))
            .GroupBy(disk => Normalize(paths.Resolve(disk.Path, null) ?? disk.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(
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
        const string command = """
            if [ ! -x /usr/bin/python3 ]; then
                if ! command -v apt-get >/dev/null; then
                    echo 'Guest disk I/O: Python 3 is required and this distro has no apt-get.' >&2
                    exit 1
                fi
                if ! apt-get -qq update </dev/null >&2 ||
                   ! DEBIAN_FRONTEND=noninteractive apt-get -qq -y --no-install-recommends install python3 ca-certificates </dev/null >&2; then
                    echo 'Guest disk I/O: Could not install Python 3 in WSL.' >&2
                    exit 1
                fi
            fi
            exec /usr/bin/python3 -I - root
            """;
        var running = await CpuProcessSampler.ReadRunningWslNames(cancellation).ConfigureAwait(false);
        if (!running.Contains(distro))
            throw new InvalidOperationException($"WSL distribution {distro} is stopped.");
        string code = await ReadScript("GuestDiskIo.py", cancellation).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var result = await Run(CpuProcessSampler.Wsl,
            new[] { "--distribution", distro, "--user", "root", "--exec", "/bin/sh", "-c", command.Replace("\r\n", "\n") },
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GuestCpuSession> CpuSessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One long-lived windowless ssh per VM serves every CPU sample; a failed session is dropped and reopened.</summary>
    public static async Task<CpuProcessSample> SampleCpu(string vmName, CancellationToken cancellation)
    {
        var target = GuestSshArguments(vmName);
        var session = CpuSessions.GetOrAdd(vmName, name => new GuestCpuSession(name));
        try
        {
            string output = await session.Sample(target, await ReadScript("GuestCpu.sh", cancellation).ConfigureAwait(false),
                TimeSpan.FromSeconds(12), cancellation).ConfigureAwait(false);
            return CpuProcessSampler.ParseGuest(output)
                ?? throw new InvalidOperationException("The VM returned no guest CPU sample.");
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            session.Close();
            throw;
        }
    }

    /// <summary>Ends every guest CPU session not named in <paramref name="activeVmNames"/> (its ssh process tree), for
    /// good: a sample still in flight on an ended session cannot reopen it. The CPU pane calls this after cancelling its
    /// sampler, with no names when it closes or the app exits and with its selected guests when the selection changes,
    /// so no ssh outlives the view that sampled through it.</summary>
    public static void RetainCpuSessions(IEnumerable<string> activeVmNames)
    {
        var active = new HashSet<string>(activeVmNames, StringComparer.OrdinalIgnoreCase);
        foreach (string vmName in CpuSessions.Keys.Where(name => !active.Contains(name)).ToArray())
            if (CpuSessions.TryRemove(vmName, out var session))
                session.End();
    }

    private sealed class GuestCpuSession(string vmName)
    {
        private const int ErrorTailLines = 5;
        private readonly object sync = new();
        private readonly Queue<string> errorTail = new();
        private Process process;
        private TaskCompletionSource<string> waiter;
        private bool ended;

        /// <summary>Resets the session after a failed sample; the next sample reopens it.</summary>
        public void Close() => Close(expected: null);

        /// <summary>Ends the session for good (its owner no longer samples this guest): a later sample is cancelled
        /// instead of reopening ssh.</summary>
        public void End()
        {
            lock (sync) ended = true;
            Close(expected: null, deliberate: true);
        }

        /// <summary>Ends the session's process; with <paramref name="expected"/>, only while that process is still the
        /// session's, checked and cleared under one lock, so a stream ending late cannot close a newer session.</summary>
        private void Close(Process expected, bool deliberate = false)
        {
            Process old;
            TaskCompletionSource<string> pending;
            string cause = null;
            lock (sync)
            {
                if (expected != null && process != expected) return;
                old = process; process = null; pending = waiter; waiter = null;
                if (pending != null && !deliberate) cause = Cause(old, "closed the guest CPU sampling connection");
            }
            // An owner that ended the session cancels its own sample; only a connection that failed reports why.
            if (deliberate) pending?.TrySetCanceled();
            else pending?.TrySetException(new InvalidOperationException(cause));
            Stop(old);
        }

        /// <summary>Why the session failed, for the sample that waited on it: ssh's connect failure (exit 255) as the one-shot
        /// samplers say it, else what the VM did plus the last lines ssh wrote to stderr. Called under <c>sync</c>.</summary>
        private string Cause(Process failed, string what)
        {
            bool unreachable = false;
            try { unreachable = failed != null && failed.HasExited && failed.ExitCode == 255; }
            catch (InvalidOperationException) { }
            if (unreachable) return GuestSshUnreachable(vmName);
            string tail = string.Join(" | ", errorTail);
            return $"The VM {vmName} {what}." + (tail.Length > 0 ? $" ssh: {tail}" : "");
        }

        private static void Stop(Process old)
        {
            try { if (old != null && !old.HasExited) old.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            old?.Dispose();
        }

        /// <summary>The next complete sample the VM streams after this call.</summary>
        public async Task<string> Sample(string[] target, string code, TimeSpan limit, CancellationToken cancellation)
        {
            TaskCompletionSource<string> mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync)
            {
                if (ended) throw new OperationCanceledException($"The guest CPU session for {vmName} has ended.");
                cancellation.ThrowIfCancellationRequested(); // a cancelled sampler never opens ssh its owner already closed
                if (process == null || process.HasExited)
                    Open(target, code);
                waiter ??= mine;
                mine = waiter;
            }
            try { return await mine.Task.WaitAsync(limit, cancellation).ConfigureAwait(false); }
            catch (TimeoutException) { throw new InvalidOperationException("Guest CPU sampling did not finish."); }
        }

        private void Open(string[] target, string code)
        {
            string encoded = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(code));
            var start = new ProcessStartInfo(SshExecutable)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in target)
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add("/bin/sh -c 'eval \"$(printf %s " + encoded + " | base64 -d)\"' sh loop");
            var started = Process.Start(start);
            Stop(process); // the previous, exited process: disposed rather than orphaned
            process = started;
            errorTail.Clear();
            // Drained so ssh never blocks on a full stderr pipe; its last lines are the cause a failed sample reports.
            started.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (sync)
                {
                    if (!ReferenceEquals(process, started)) return; // a stale process's late line is not this session's
                    errorTail.Enqueue(e.Data);
                    while (errorTail.Count > ErrorTailLines) errorTail.Dequeue();
                }
            };
            started.BeginErrorReadLine();
            _ = Task.Run(() => Stream(started));
        }

        private async Task Stream(Process started)
        {
            var text = new StringBuilder();
            try
            {
                while (await started.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("__END__ ", StringComparison.Ordinal))
                    {
                        text.Append(line).Append('\n');
                        continue;
                    }
                    string reply = text.ToString();
                    text.Clear();
                    TaskCompletionSource<string> pending;
                    string cause = null;
                    lock (sync)
                    {
                        pending = waiter; waiter = null;
                        if (line != "__END__ 0" && pending != null) cause = Cause(started, "could not run the guest CPU sampler");
                    }
                    if (line == "__END__ 0") pending?.TrySetResult(reply);
                    else pending?.TrySetException(new InvalidOperationException(cause));
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            // Let ssh exit and its stderr drain before the cause is read, so the exit code and last lines are ssh's final ones.
            try
            {
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await started.WaitForExitAsync(settle.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
            Close(expected: started);
        }
    }

    public static Task<string> ReadGuestCommands(string vmName, string[] pids, CancellationToken cancellation) =>
        ReadGuest(vmName, "GuestCommands.sh", "command line", 8,
            new[] { "/bin/sh", "-s", "--" }.Concat(pids).ToArray(), cancellation, output => output);

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

    private static readonly string SshExecutable = System.IO.Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");

    private static string[] GuestSshArguments(string vmName)
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

        return new[]
        {
            "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "IdentitiesOnly=yes",
            "-o", "ConnectTimeout=5", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3",
            "-i", key, "fleet@" + vmName + "." + domain
        };
    }

    /// <summary>What ssh's exit 255 means for every guest sampler: the VM is not reachable with the cluster identity.</summary>
    private static string GuestSshUnreachable(string vmName) =>
        $"Could not connect to the VM {vmName} with the configured cluster SSH identity.";

    private static async Task<T> ReadGuest<T>(string vmName, string script, string label, int seconds,
        string[] command, CancellationToken cancellation, Func<string, T> parse = null) where T : class
    {
        var target = GuestSshArguments(vmName);

        string code = await ReadScript(script, cancellation).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        CommandResult result;
        try
        {
            result = await Run(SshExecutable, target.Concat(command), code, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Guest {label} sampling did not finish.");
        }
        if (result.ExitCode != 0)
        {
            string guestError = result.Error.Split('\n').FirstOrDefault(line => line.StartsWith("Guest disk I/O: ", StringComparison.Ordinal));
            throw new InvalidOperationException(guestError?.TrimEnd() ??
                (result.ExitCode == 255 ? GuestSshUnreachable(vmName)
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
