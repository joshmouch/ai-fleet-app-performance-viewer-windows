[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.-]+)?$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$checkout = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $output ('viewer-' + $Version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$bundle = Join-Path $output ('PerformanceViewer-' + $Version + '-win-x64.zip')
if (Test-Path -LiteralPath $bundle) { throw "A package already exists at $bundle." }
$null = New-Item -Path $stage -ItemType Directory -Force

function Publish-ViewerProject([string]$Project, [string]$Destination, [string[]]$Options = @()) {
    dotnet publish (Join-Path $checkout $Project) -c Release --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false -o $Destination @Options
    if ($LASTEXITCODE -ne 0) { throw "Publishing $Project failed ($LASTEXITCODE)." }
}

Publish-ViewerProject 'PerformanceViewer.csproj' (Join-Path $stage 'Collector') @("-p:Version=$Version")
Publish-ViewerProject 'Web/PerformanceViewer.Web.csproj' (Join-Path $stage 'Server') @("-p:Version=$Version")
Publish-ViewerProject 'Native/PerformanceViewer.Native.csproj' (Join-Path $stage 'Shell') @('-r', 'win-x64', '-p:WindowsAppSDKSelfContained=true')
Publish-ViewerProject 'Native/Launcher/PerformanceViewer.Launcher.csproj' (Join-Path $stage 'Launcher') @(
    '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true')

[IO.Compression.ZipFile]::CreateFromDirectory($stage, $bundle)
Get-Item -LiteralPath $bundle
