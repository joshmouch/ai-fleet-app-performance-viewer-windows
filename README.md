# AI Fleet Cluster Performance Viewer

A Windows desktop viewer for CPU and disk activity on an AI Fleet cluster host. Follow disk activity from the host volume to the VHDX, its Hyper-V VM, and the processes and paths inside the guest.

- **Show processes** opens an inline CPU process table. Select the Windows host or a running cluster VM.
- **Show disks** shows the disks that meet the selected activity and transfer thresholds. Select a disk for process and path I/O.
- **VHDX files** adds the busiest virtual disk files on the disks already shown. Select a VM-backed file for its live guest I/O.

Publish with the .NET 10 SDK on Windows.

```powershell
dotnet publish PerformanceViewer.csproj -c Release
```

The Windows x64 executable is under `bin/Release/net10.0-windows/win-x64/publish/` and uses the .NET 10 Desktop Runtime. Run elevated for Windows file I/O tracing and Hyper-V inventory. Guest sampling uses Windows OpenSSH, the configured AI Fleet cluster SSH identity, and Python 3 on the guest. Guest disk I/O also requires noninteractive sudo for the sampler and Linux tracing access.
