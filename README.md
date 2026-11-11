# AI Fleet Cluster Performance Viewer

A Windows desktop viewer for CPU and disk activity on an AI Fleet cluster host. Follow disk activity from the host volume to the VHDX, its Hyper-V VM, and the processes and paths inside the guest.

- **Show processes** opens an inline CPU process table. Check a Hyper-V VM or **WSL** on the left to replace its Windows CPU accounting row with processes from inside it. Set the number of processes and choose **Top per machine** or **Merged top**. **Guest CPU %** is the process share of the guest's measured CPU usage. **Host CPU %** is the process share of Windows CPU capacity and determines rank. WSL 2 distros share one guest CPU budget.
- **Show disks** shows the disks that meet the selected activity and transfer thresholds. Select a disk for process and path I/O.
- **VHDX files** adds the busiest virtual disk files on the disks already shown. Select a VM-backed file for its live guest I/O.

Publish with the .NET 10 SDK on Windows.

```powershell
dotnet publish PerformanceViewer.csproj -c Release
```

The Windows x64 executable is under `bin/Release/net10.0-windows/win-x64/publish/` and uses the .NET 10 Desktop Runtime. Run elevated for Windows file I/O tracing and Hyper-V inventory. Hyper-V guest sampling uses Windows OpenSSH and the configured AI Fleet cluster SSH identity. Linux CPU sampling uses a POSIX shell, `awk`, and `getconf`. Guest disk I/O requires Python 3, noninteractive sudo for the sampler, and Linux tracing access.
