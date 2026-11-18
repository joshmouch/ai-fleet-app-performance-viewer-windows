# AI Fleet Cluster Performance Viewer

A Windows desktop viewer for finding the processes behind CPU and disk activity across the Windows host, Hyper-V guests, and WSL.

### Read live samples without opening the desktop

Run the installed executable with `--collect` and redirect standard output to a file or pipe. Each UTF-8 JSON line contains a host CPU process sample and a physical disk path sample from the same collectors used by the desktop. The default interval is ten seconds. Use `--interval 5 --count 3 --disk 0` for three five-second CPU samples with Disk 0 path detail. Omitting `--disk` includes every observed device layer, including potentially overlapping Storage Spaces layers. Windows tracing permissions still apply. The disk status field carries any trace error.

Frames carry the host, boot time when available, capture identity, sequence, separate measurement windows and path coverage. Disk paths use a rolling window and a blank path retains unmatched reads, writes and operation counts. Replace the previous disk frame when refreshing a ranking. Add `--guests` to include running Hyper-V and WSL CPU samples and guest files behind active VHDX paths. Each client can independently expand a guest for both resources. CPU shares apply to the measured host budget. Missing samples retain the host rows. Disk totals conserve each direction and operation count, including backing-file traffic without a guest weight. Windows samples continue while guest discovery or tracing is pending.

### Export file windows to history

The Windows executable accepts `--export [windows.ndjson]`. The portable .NET entry point is `Export/PerformanceViewer.Export.csproj`. Both read collector windows from the supplied file or standard input and send host physical file intervals to an OTLP/HTTP logs receiver. Set `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` to its full logs URL. Optional request headers use `OTEL_EXPORTER_OTLP_LOGS_HEADERS` or the shared `OTEL_EXPORTER_OTLP_HEADERS` setting.

The exporter preserves each window identity and includes unresolved files in the read/write byte and operation totals. It checks coverage before sending, compresses requests and retries transient failures. A failed input line is reported on standard error and the command exits unsuccessfully. Replaying the original file sends the same identities. The local viewer runs independently of export.

### Local web viewer

Run `dotnet run --project Web/PerformanceViewer.Web.csproj` with .NET 10 and open `http://127.0.0.1:5217`. The Blazor server owns one reader per configured source across browser tabs. The CPU and Disk lists rank both Windows hosts together. Disk groups by process or filename, with separate read, write and total rates. Source status identifies old samples and offline collectors. A replay remains selectable after the last frame.

In `Web/appsettings.json`, configure a local command, SSH command or replay file for each source. For example:

```json
{
  "Sources": [
    { "Name": "Local", "Command": "aifleet-performance-viewer.exe", "Arguments": ["--collect", "--guests", "--disk", "0"] },
    { "Name": "Remote", "Command": "ssh", "Arguments": ["-o", "BatchMode=yes", "windows-host", "aifleet-performance-viewer --collect --guests --disk 0"] },
    { "Name": "Recorded", "ReplayFile": "/path/to/windows.ndjson" }
  ]
}
```

The local command can be an absolute path to the published executable. The remote command runs in the configured SSH account and needs the same tracing permissions as a local viewer. If two healthy sources report the same host, their configuration order selects the active one. Stale live sources drop out of the ranking after `StaleAfterSeconds` (default 30).

### Optional native desktop window

`Native/PerformanceViewer.Native.csproj` hosts the same Razor rankings in a .NET MAUI BlazorWebView on Windows and macOS. The running local web service owns all sampling. Browser tabs and the native window share its capture identities and keep their own filters. The shell connects to loopback port 5217 or the port specified by the `ViewerPort` environment variable. It reconnects automatically after a service restart. Unchanged snapshots return HTTP 304.

- **CPU** and **Disk** share a bottom details pane, a search field, guest selection, and ranking options. Details start open and all discovered guests start checked. Search filters the sampled rows before ranking them.
- CPU and Disk use the same overview layout, with current readings at left and their history at right. Keep formatting changes aligned across both views.
- **Settings → Usage band %** starts at 10. Enter a whole percent from 1 to 100, or clear it to return to **Top rows** and optional machine grouping. CPU bands use sampled host-equivalent CPU time. Disk bands use captured bytes in the selected scope. Each row stays in the band where its cumulative share starts, so a large row can span several bands. The denominator includes unattributed usage. Band headers sum the displayed rows' numeric metrics, excluding PIDs. Guest busy percentages add the individual guest shares in that band.
- Disk lists keep attribution gaps in the totals and show their rates above the rankings. **Disk filters → Show disk attribution gaps** includes those rows in the lists. Named paths can still show Windows kernel activity.
- Process tables fetch command lines for displayed rows. Hover over the command line to read it in full. Exited or protected processes can have no command line available.
- Right-click a process or path row and choose **Copy row** to copy its displayed values with their column labels.
- On **CPU**, **CPU impact %** ranks Windows processes and guests against host CPU capacity. Guest impact is the measured VM CPU budget apportioned by the guest process's share of busy CPU. **Guest busy %** shows that share on its own. WSL distros share one VM CPU budget. A guest with no usable host budget can still show guest CPU usage. Guest CPU that could not be attributed to a process appears in the coverage summary above the ranking. CPU chart segments show Windows, all guests, and hypervisor overhead when Hyper-V counters are available.
- On **Disk**, the default ranking spans all Windows disks. Select a disk or VHDX card to narrow it, or select **All disks** to return to the host-wide ranking. **I/O impact MiB/s** ranks completed Windows I/O and apportions the VHDX portion among available guest processes and paths. **Guest MiB/s** shows sampled guest file I/O, including cached I/O. Bytes and operation counts remain available and unmatched host I/O stays in the ranking. Double-click a VHDX path for the guest-only sample. Paths tabs show the filename and its folder in separate columns. Select a row and press **Ctrl+C** to copy the full path.
- **Disk filters** control which cards are shown and how they are ranked. VHDX cards are on by default. VHDX cards use the transfer threshold because active time is available only for Windows disk counters. Card thresholds apply to the cards. Disk scope applies to the overview and unselected file tables. Mounts + Spaces covers mounted disks and Storage Spaces virtual disks. All disks includes backing-drive requests, which can overlap virtual disk traffic and may have no file key. The status tooltip breaks down unresolved bytes and reports traffic outside the scope.

Publish with the .NET 10 SDK on Windows.

```powershell
dotnet publish PerformanceViewer.csproj -c Release
```

The Windows x64 executable is under `bin/Release/net10.0-windows/win-x64/publish/` and uses the .NET 10 Desktop Runtime. Run elevated for Windows file I/O tracing and Hyper-V inventory. Hyper-V guests use Windows OpenSSH and the configured AI Fleet cluster SSH identity. Linux CPU sampling uses a POSIX shell, `awk`, and `getconf`. Guest disk attribution uses Python 3, bpftrace and Linux tracing access. Hyper-V SCSI slots support ext4. The guest root sampler supports ext4 and Btrfs. Hyper-V samples use noninteractive sudo and the guest's installed bpftrace. WSL disk attribution targets the root VHDX of a running distro. On its first sample, the viewer verifies and caches a pinned bpftrace release as the distro root user. If Python 3 is missing, it installs it automatically on distros with apt-get.

## Configuration

On a Linux guest with a Btrfs root, run `python3 GuestDiskIo.py root` with tracing privileges for one six-second sample. `python3 GuestDiskIo.py loop root` streams successive samples. On OrbStack the same entry point can run through `orb -m <machine> -u root python3 - root < GuestDiskIo.py` on the Mac. It resolves the guest-visible path under the root subvolume and reports logical read/write bytes and completed operations per guest PID. These include cache hits. Shared virtual block-device traffic can span several guests and is excluded from this sample.

Guest sampling connects to each VM as `fleet@<vm-name>.<domain>`. The domain has no default. Set it with the `PERFORMANCE_VIEWER_GUEST_DOMAIN` environment variable, or with `guestDomain` in `%LOCALAPPDATA%\AIFleetPerformanceViewer\settings.json`:

```json
{ "guestDomain": "your-tailnet.ts.net" }
```

Without it the viewer still shows host CPU and disk activity, and guest views report that the domain is not configured. `NODES_SSH_KEY` overrides the SSH identity path.

## Install

```powershell
winget install OpenAgency.AIFleet.PerformanceViewer
```

Released under the MIT License.

### Linux guest file activity in the web viewer

Add a guest to the web host's existing `Sources` array alongside its Mac or Windows collector. Set `GuestOf` to the host name emitted by that collector, and `Name` to the guest name. For example, for an OrbStack guest:

```json
{
  "Name": "kube-worker-3",
  "GuestOf": "josh-mac",
  "Command": "orb",
  "Arguments": ["-m", "kube-worker-3", "-u", "root", "python3", "-", "loop", "root"]
}
```

The server sends its bundled Linux sampler to the command over stdin. The same source can run over SSH with a remote Python interpreter that can attach the filesystem probes. In Disk, select **File** and sort by **Guest total**, **Guest read**, or **Guest write**. Host impact and guest file activity have separate totals. A guest using a shared backing device has no individually measured host share, shown as a dash. Deselect the guest to hide its rows.
