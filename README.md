# AI Fleet Cluster Performance Viewer

A Windows desktop viewer for CPU and disk activity on an AI Fleet cluster host. Follow disk activity from the host volume to the VHDX, its Hyper-V VM, and the processes and paths inside the guest.

- Use **Guests**, **Group by machine**, and **Top** above the charts to control CPU and disk details. Grouping separates CPU tiles, disks and VHDX cards, and ranked rows by machine. Clear it for one grid and one ranking per table.
- In the CPU view, checked guests add virtual processor timelines. In **Show processes**, **Guest CPU %** is the process share of the guest's measured usage. **Host CPU %** is its estimated share of Windows CPU capacity and determines rank. WSL 2 distros share one guest CPU budget.
- With guests checked, Hyper-V counters split each host logical processor into Windows time and combined VM time. The overview also shows Hyper-V overhead. Guest virtual processor timelines show that guest's own processor usage. The host split includes all running VMs.
- **Show disks** shows disks that meet the activity and transfer thresholds. Select a disk for process and path I/O. **VHDX files** adds the busiest virtual disk files on those disks.
- For a checked Hyper-V VM with a matching VHDX and guest file I/O, the disk tables distribute the Windows bytes and operations across guest rows. **Guest MiB** shows guest file I/O, including cached I/O. The **Guest Processes** and **Guest Paths** tabs show the underlying guest sample for a selected VM disk.

Publish with the .NET 10 SDK on Windows.

```powershell
dotnet publish PerformanceViewer.csproj -c Release
```

The Windows x64 executable is under `bin/Release/net10.0-windows/win-x64/publish/` and uses the .NET 10 Desktop Runtime. Run elevated for Windows file I/O tracing and Hyper-V inventory. Hyper-V guest sampling uses Windows OpenSSH and the configured AI Fleet cluster SSH identity. Linux CPU sampling uses a POSIX shell, `awk`, and `getconf`. Guest disk I/O requires Python 3, noninteractive sudo for the sampler, and Linux tracing access.


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
