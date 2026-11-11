# AI Fleet Cluster Performance Viewer

A Windows desktop viewer for finding the processes behind CPU and disk activity across the Windows host, Hyper-V guests, and WSL.

- **CPU** and **Disk** share a bottom details pane, a search field, guest selection, and grouping. Details start open. All discovered guests start checked and rankings start flat with 20 rows. Search filters the sampled rows before applying the **Top rows** limit.
- Process tables fetch command lines for displayed rows. Hover over the command line to read it in full. Exited or protected processes can have no command line available.
- On **CPU**, **CPU impact %** ranks Windows processes and guests against host CPU capacity. Guest impact is the measured VM CPU budget apportioned by the guest process's share of busy CPU. **Guest busy %** shows that share on its own. WSL distros share one VM CPU budget. A guest with no usable host budget can still show guest CPU usage. Guest CPU that could not be attributed to a process appears in the coverage summary above the ranking. CPU chart segments show Windows, all guests, and hypervisor overhead when Hyper-V counters are available.
- On **Disk**, the default ranking spans all Windows disks. Select a disk or VHDX card to narrow it, or select **All disks** to return to the host-wide ranking. **I/O impact MiB/s** ranks completed Windows I/O and apportions the VHDX portion among available guest processes and paths. **Guest MiB/s** shows sampled guest file I/O, including cached I/O. Bytes and operation counts remain available and unmatched host I/O stays in the ranking. Double-click a VHDX path for the guest-only sample. In a Paths tab, select a row and press **Ctrl+C** to copy the full path.
- **Disk filters** control which cards are shown and how they are ranked. VHDX cards are on by default. VHDX cards use the transfer threshold because active time is available only for Windows disk counters. Card thresholds apply to the cards while the details pane covers the selected disk, VHDX, or all disks.

Publish with the .NET 10 SDK on Windows.

```powershell
dotnet publish PerformanceViewer.csproj -c Release
```

The Windows x64 executable is under `bin/Release/net10.0-windows/win-x64/publish/` and uses the .NET 10 Desktop Runtime. Run elevated for Windows file I/O tracing and Hyper-V inventory. Hyper-V guests use Windows OpenSSH and the configured AI Fleet cluster SSH identity. Linux CPU sampling uses a POSIX shell, `awk`, and `getconf`. Guest disk attribution requires an ext4 filesystem, Python 3, bpftrace, and Linux tracing access. Hyper-V samples use noninteractive sudo. WSL disk attribution targets the distro root VHDX and uses the distro root user.

## Configuration

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
