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
    /// <summary>The VM disk inventory. Its WSL registry and process reads are synchronous, so the caller runs it on the
    /// thread pool.</summary>
    public static async Task<Dictionary<string, VmDiskOwners>> ReadInventory(CancellationToken cancellation, VolumePathIndex paths)
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

    private static async Task<T> ReadHyperV<T>(string script, CancellationToken cancellation) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        string executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await Run(executable, new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }, null, deadline.Token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Hyper-V inventory unavailable.");
        return Deserialize<T>(result.Output, "Hyper-V inventory unavailable.");
    }

    /// <summary>The one reader of the JSON the guest and Hyper-V scripts print: text that is not the promised JSON, or is
    /// JSON null, fails with <paramref name="failure"/> instead of a parser or null-reference exception.</summary>
    private static T Deserialize<T>(string json, string failure) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException(failure); }
        catch (JsonException) { throw new InvalidOperationException(failure); }
    }

    private static GuestIoSample ParseGuestIo(string json, string failure)
    {
        var sample = Deserialize<GuestIoSample>(json, failure);
        if (sample.Rows == null || Array.IndexOf(sample.Rows, null) >= 0)
            throw new InvalidOperationException(failure);
        return sample;
    }

    public static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GuestDiskSession> DiskSessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The next guest I/O sample of one disk. A Hyper-V guest's disks share one long-lived ssh session per VM, in
    /// which one Python loop runs one bpftrace at a time over every disk being watched (GuestDiskIo.py loop): a sample costs
    /// no ssh login, sudo or Python start, and the guest runs one bpftrace, not one per disk. Each watched disk used to open
    /// its own login and bpftrace back to back, about 20 at once on the cluster nodes (2026-11-12), and every login ran the
    /// guest's whole login message of the day.</summary>
    public static async Task<GuestIoSample> Sample(VmDisk disk, CancellationToken cancellation)
    {
        if (disk.WslDistro != null)
            return await ReadWslDisk(disk.WslDistro, cancellation).ConfigureAwait(false);
        if (disk.Controller != "SCSI" || !disk.SingleScsiController)
            throw new InvalidOperationException("Guest disk matching requires a VM with one Hyper-V SCSI controller.");
        if (!IsGuestAddress(disk.VMName) || disk.Location < 0 || disk.Location > 63)
            throw new InvalidOperationException("This VM does not have a supported guest address.");
        var target = GuestSshArguments(disk.VMName);
        while (true)
        {
            var session = DiskSessions.GetOrAdd(disk.VMName, name => new GuestDiskSession(name));
            try { return await session.Sample(target, disk.Location, cancellation).ConfigureAwait(false); }
            catch (GuestDiskSession.EndedException)
            {
                // Its last disk was released while this one joined: drop that instance and start a fresh session.
                DiskSessions.TryRemove(new KeyValuePair<string, GuestDiskSession>(disk.VMName, session));
            }
        }
    }

    /// <summary>A disk no longer watched leaves its VM's session; the session runs without it from its next sample, and the
    /// last disk to leave ends the session and its ssh process tree.</summary>
    public static void ReleaseDisk(VmDisk disk)
    {
        if (disk.WslDistro != null || !DiskSessions.TryGetValue(disk.VMName, out var session))
            return;
        if (session.Release(disk.Location))
            DiskSessions.TryRemove(new KeyValuePair<string, GuestDiskSession>(disk.VMName, session));
    }

    /// <summary>One run of the guest loop: a sample per disk, or why a disk has none (the others still sample).</summary>
    private sealed record GuestDiskLine(Dictionary<string, GuestIoSample> Samples, Dictionary<string, string> Errors);

    private sealed class GuestDiskSession(string vmName)
    {
        public sealed class EndedException() : Exception("The guest disk session has ended.");

        private const int ErrorTailLines = 5;
        private readonly object sync = new();
        private readonly SortedSet<int> wanted = new();
        private readonly Queue<string> errorTail = new();
        private int[] running = Array.Empty<int>();
        private Process process;
        private TaskCompletionSource<GuestDiskLine> next = NewTick();
        private bool ended;

        private static TaskCompletionSource<GuestDiskLine> NewTick() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The next sample of <paramref name="location"/>. The disk joins the session; a session whose loop does not
        /// cover every wanted disk is restarted with all of them, and its waiters take the restarted loop's first line.</summary>
        public async Task<GuestIoSample> Sample(string[] target, int location, CancellationToken cancellation)
        {
            string key = location.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // A line from a loop started before this disk joined does not hold it; the restarted loop's next line does.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Task<GuestDiskLine> tick;
                lock (sync)
                {
                    if (ended) throw new EndedException();
                    cancellation.ThrowIfCancellationRequested();
                    wanted.Add(location);
                    if (process == null || process.HasExited || !running.SequenceEqual(wanted))
                        Open(target);
                    tick = next.Task;
                }
                GuestDiskLine run;
                // A run is 6 s of tracing plus bpftrace's start; the guest script bounds one run at 17 s.
                try { run = await tick.WaitAsync(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    Restart();
                    throw new InvalidOperationException("Guest I/O sampling did not finish.");
                }
                if (run.Errors != null && run.Errors.TryGetValue(key, out string error))
                    throw new InvalidOperationException(error);
                if (run.Samples.TryGetValue(key, out var sample))
                    return sample;
            }
            throw new InvalidOperationException("The VM returned no guest I/O sample for this disk.");
        }

        /// <summary>Removes a disk; true when it was the last one and the session has ended.</summary>
        public bool Release(int location)
        {
            Process old;
            TaskCompletionSource<GuestDiskLine> pending;
            lock (sync)
            {
                wanted.Remove(location);
                if (wanted.Count > 0) return false; // the next sample restarts the loop without it
                ended = true;
                old = process; process = null; pending = next;
            }
            pending.TrySetException(new EndedException());
            Stop(old);
            return true;
        }

        /// <summary>Ends the current loop after a failed or stalled sample; the next sample opens a new one.</summary>
        private void Restart()
        {
            Process old;
            lock (sync) { old = process; process = null; }
            Stop(old);
        }

        private static void Stop(Process old)
        {
            try { if (old != null && !old.HasExited) old.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            old?.Dispose();
        }

        /// <summary>Starts the loop over every wanted disk. Called under <c>sync</c>.</summary>
        private void Open(string[] target)
        {
            Stop(process);
            running = wanted.ToArray();
            var start = new ProcessStartInfo(SshExecutable)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in target.Concat(new[] { "sudo", "-n", "/usr/bin/python3", "-I", "-", "loop" })
                .Concat(running.Select(slot => slot.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                start.ArgumentList.Add(argument);
            var started = Process.Start(start);
            process = started;
            errorTail.Clear();
            started.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (sync)
                {
                    if (!ReferenceEquals(process, started)) return;
                    errorTail.Enqueue(e.Data);
                    while (errorTail.Count > ErrorTailLines) errorTail.Dequeue();
                }
            };
            started.BeginErrorReadLine();
            // Python reads its program from stdin to EOF, then runs it: the script, then a closed stdin.
            started.StandardInput.Write(ReadScript("GuestDiskIo.py"));
            started.StandardInput.Close();
            _ = Task.Run(() => Stream(started));
        }

        private async Task Stream(Process started)
        {
            Exception failure = null;
            try
            {
                while (await started.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    GuestDiskLine parsed;
                    try { parsed = JsonSerializer.Deserialize<GuestDiskLine>(line); }
                    catch (JsonException) { parsed = null; }
                    if (parsed?.Samples == null || parsed.Samples.Values.Any(s => s?.Rows == null || Array.IndexOf(s.Rows, null) >= 0))
                    {
                        failure = new InvalidOperationException("The VM returned no guest I/O sample.");
                        break;
                    }
                    TaskCompletionSource<GuestDiskLine> done;
                    lock (sync)
                    {
                        if (!ReferenceEquals(process, started)) return; // a replaced loop's late line is not this session's
                        done = next; next = NewTick();
                    }
                    done.TrySetResult(parsed);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            // Let ssh exit and its stderr drain before the cause is read.
            try
            {
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await started.WaitForExitAsync(settle.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
            TaskCompletionSource<GuestDiskLine> pending;
            lock (sync)
            {
                if (!ReferenceEquals(process, started)) return;
                process = null; pending = next; next = NewTick();
                if (failure == null)
                {
                    string guest = errorTail.FirstOrDefault(line => line.StartsWith("Guest disk I/O: ", StringComparison.Ordinal));
                    bool unreachable = false;
                    try { unreachable = started.HasExited && started.ExitCode == 255; }
                    catch (InvalidOperationException) { }
                    failure = new InvalidOperationException(guest?.TrimEnd() ?? (unreachable ? GuestSshUnreachable(vmName)
                        : $"The VM {vmName} closed the guest I/O sampling connection." +
                          (errorTail.Count > 0 ? " ssh: " + string.Join(" | ", errorTail) : "")));
                }
            }
            pending.TrySetException(failure);
            Stop(started);
        }
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
        string code = ReadScript("GuestDiskIo.py");
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
        return ParseGuestIo(result.Output, "WSL returned no guest disk sample.");
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
            string output = await session.Sample(target, TimeSpan.FromSeconds(12), cancellation).ConfigureAwait(false);
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
        public async Task<string> Sample(string[] target, TimeSpan limit, CancellationToken cancellation)
        {
            TaskCompletionSource<string> mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync)
            {
                if (ended) throw new OperationCanceledException($"The guest CPU session for {vmName} has ended.");
                cancellation.ThrowIfCancellationRequested(); // a cancelled sampler never opens ssh its owner already closed
                if (process == null || process.HasExited)
                    Open(target);
                waiter ??= mine;
                mine = waiter;
            }
            try { return await mine.Task.WaitAsync(limit, cancellation).ConfigureAwait(false); }
            catch (TimeoutException) { throw new InvalidOperationException("Guest CPU sampling did not finish."); }
        }

        private void Open(string[] target)
        {
            string encoded = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(ReadScript("GuestCpu.sh")));
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

    private static readonly object SettingsSync = new();
    private static (DateTime Written, string Domain, string Problem)? settingsRead;

    /// <summary>The DNS domain appended to a VM name to reach it over SSH, or why it cannot be used; both null means not
    /// configured. The environment variable is read on every call; settings.json is parsed again only when its presence or
    /// last-write time changes, so every sample and render calls this without parsing or exceptions.</summary>
    internal static (string Domain, string Problem) ResolveGuestDomain()
    {
        const string variable = "PERFORMANCE_VIEWER_GUEST_DOMAIN";
        string source = variable;
        string domain = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(domain))
        {
            string problem;
            (domain, problem) = ReadSettingsDomain();
            if (problem != null)
                return (null, problem);
            source = "guestDomain in " + SettingsPath();
        }
        domain = domain?.Trim().Trim('.');
        if (string.IsNullOrEmpty(domain))
            return (null, null);
        return Regex.IsMatch(domain, @"\A[a-zA-Z0-9.-]+\z") ? (domain, null) : (null, source + " is not a valid DNS domain.");
    }

    private static (string Domain, string Problem) ReadSettingsDomain()
    {
        string path = SettingsPath();
        DateTime written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        lock (SettingsSync)
        {
            if (settingsRead is { } cached && cached.Written == written)
                return (cached.Domain, cached.Problem);
            var result = written == DateTime.MinValue ? (Domain: (string)null, Problem: (string)null) : ParseSettings(path);
            settingsRead = (written, result.Domain, result.Problem);
            return result;
        }
    }

    private static (string Domain, string Problem) ParseSettings(string path)
    {
        try
        {
            // The README documents settings.json as hand-edited, so comments and trailing commas are accepted.
            using var settings = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (settings.RootElement.ValueKind != JsonValueKind.Object)
                return (null, path + " must contain a JSON object.");
            if (!settings.RootElement.TryGetProperty("guestDomain", out var value) || value.ValueKind == JsonValueKind.Null)
                return (null, null);
            return value.ValueKind == JsonValueKind.String ? (value.GetString(), null)
                : (null, "guestDomain in " + path + " must be a string.");
        }
        catch (JsonException) { return (null, path + " is not valid JSON."); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (null, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, path + " could not be read: " + e.Message);
        }
    }

    internal static bool IsGuestAddress(string name) =>
        name != null && Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,62}\z");

    private static readonly string SshExecutable = System.IO.Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");

    private static string[] GuestSshArguments(string vmName)
    {
        if (!IsGuestAddress(vmName))
            throw new InvalidOperationException("This VM does not have a supported guest address.");
        var (domain, problem) = ResolveGuestDomain();
        if (problem != null)
            throw new InvalidOperationException(problem);
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
        string[] command, CancellationToken cancellation, Func<string, T> parse) where T : class
    {
        var target = GuestSshArguments(vmName);

        string code = ReadScript(script);
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
        return parse(result.Output)
            ?? throw new InvalidOperationException($"The VM returned no guest {label} sample.");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Scripts = new();

    /// <summary>An embedded script's text, read from the assembly once per resource name.</summary>
    internal static string ReadScript(string script) => Scripts.GetOrAdd(script, name =>
    {
        using var resource = typeof(VmDiskIo).Assembly.GetManifestResourceStream(name);
        using var reader = new StreamReader(resource, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    });

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
