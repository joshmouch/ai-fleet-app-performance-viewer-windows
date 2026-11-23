[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Package', 'Source')][string]$Mode,
    [Parameter(Mandatory)][string]$Package,
    [string]$SourceCheckout
)

$ErrorActionPreference = 'Stop'
$packageFile = (Get-Item -LiteralPath $Package).FullName
if ($Mode -eq 'Source') {
    if (!$SourceCheckout) { throw 'Source mode needs the viewer checkout.' }
    $SourceCheckout = (Resolve-Path -LiteralPath $SourceCheckout).Path
    if (!(Test-Path -LiteralPath (Join-Path $SourceCheckout 'Web/PerformanceViewer.Web.csproj') -PathType Leaf)) {
        throw "The web project was not found in $SourceCheckout."
    }
}

$identity = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash.ToLowerInvariant()
$installRoot = (New-Item -Path (Join-Path $env:LOCALAPPDATA 'Programs/AIFleetPerformanceViewerShell') -ItemType Directory -Force).FullName
$packages = (New-Item -Path (Join-Path $installRoot 'Releases') -ItemType Directory -Force).FullName
$shells = (New-Item -Path (Join-Path $installRoot 'Shells') -ItemType Directory -Force).FullName
$release = Join-Path $packages $identity
$launcherName = 'PerformanceViewer.Launcher.exe'
$launcher = Join-Path $installRoot $launcherName
$installationLock = [IO.File]::Open((Join-Path $installRoot 'installation.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    if (!(Test-Path -LiteralPath $release -PathType Container)) {
        $stage = Join-Path $installRoot ('.install-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -Path $stage -ItemType Directory -Force
        [IO.Compression.ZipFile]::ExtractToDirectory($packageFile, $stage)
        foreach ($item in @('Shell/PerformanceViewer.Native.exe', 'Collector/AI Fleet Cluster Performance Viewer.exe',
                'Server/PerformanceViewer.Web.dll', "Launcher/$launcherName")) {
            if (!(Test-Path -LiteralPath (Join-Path $stage $item) -PathType Leaf)) {
                throw "The viewer package lacks $item. Unpublished extraction remains at $stage."
            }
        }
        $shellFolder = Join-Path $stage 'Shell'
        $shellVersion = (Get-Item -LiteralPath (Join-Path $shellFolder 'PerformanceViewer.Native.exe')).VersionInfo.ProductVersion
        if ($shellVersion -notmatch '^\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$') { throw "Invalid native shell version in $packageFile." }
        $shellFiles = Get-ChildItem -LiteralPath $shellFolder -Recurse -File | Sort-Object FullName | ForEach-Object {
            [IO.Path]::GetRelativePath($shellFolder, $_.FullName) + ' ' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
        $shellHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($shellFiles -join "`n"))))
        $shellSlot = Join-Path $shells ($shellVersion + '-' + $shellHash.ToLowerInvariant().Substring(0, 16))
        if (!(Test-Path -LiteralPath $shellSlot -PathType Container)) {
            [IO.Directory]::Move($shellFolder, $shellSlot)
        } else {
            $resolvedShell = (Resolve-Path -LiteralPath $shellFolder).Path
            if ($resolvedShell -ne (Join-Path $stage 'Shell') -or
                !$resolvedShell.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The extracted shell directory is outside the installation.'
            }
            Remove-Item -LiteralPath $resolvedShell -Recurse
        }
        [IO.File]::WriteAllText((Join-Path $stage 'ShellPath.txt'), (Join-Path $shellSlot 'PerformanceViewer.Native.exe'))
        [IO.Directory]::Move($stage, $release)
    }

    $releaseLauncher = Join-Path $release "Launcher/$launcherName"
    if (!(Test-Path -LiteralPath $launcher -PathType Leaf) -or
        (Get-FileHash -LiteralPath $launcher).Hash -ne (Get-FileHash -LiteralPath $releaseLauncher).Hash) {
        $pending = Join-Path $installRoot ('.launcher-' + [Guid]::NewGuid().ToString('N'))
        [IO.File]::Copy($releaseLauncher, $pending)
        [IO.File]::Move($pending, $launcher, $true)
    }

    $shellPath = [IO.File]::ReadAllText((Join-Path $release 'ShellPath.txt'))
    $content = if ($Mode -eq 'Source') { $SourceCheckout } else { $release }
    $record = @{Shell = $shellPath; Content = $content} | ConvertTo-Json -Compress
    $modeSettings = Join-Path $installRoot "$Mode.json"
    $pendingSettings = Join-Path $installRoot ('.settings-' + [Guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($pendingSettings, $record)
    [IO.File]::Move($pendingSettings, $modeSettings, $true)

    $programs = [Environment]::GetFolderPath('Programs')
    $linkName = "Performance Viewer ($Mode).lnk"
    $link = Join-Path $programs $linkName
    $desktop = New-Object -ComObject WScript.Shell
    $identityType = 'PerformanceViewer.Native.ViewerWindowsIdentity' -as [type]
    if (!$identityType) {
        Add-Type -Path (Join-Path $PSScriptRoot 'Platforms/Windows/ViewerWindowsIdentity.cs')
    }
    function Set-ViewerShortcut([string]$Path, [bool]$VerifyOwner = $false) {
        $shortcut = $desktop.CreateShortcut($Path)
        $oldModeRoot = Join-Path $installRoot $Mode
        if ($VerifyOwner -and $shortcut.TargetPath -ne $launcher -and
            !$shortcut.TargetPath.StartsWith(($oldModeRoot + '\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "The existing pin $Path belongs to another installation."
        }
        if ($shortcut.TargetPath -eq $launcher -and $shortcut.Arguments -eq $Mode -and
            $shortcut.WorkingDirectory -eq $installRoot) { return }
        $shortcut.TargetPath = $launcher
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.Arguments = $Mode
        $shortcut.IconLocation = $shortcut.TargetPath + ',0'
        $shortcut.Description = "AI Fleet Cluster Performance Viewer ($Mode)"
        $shortcut.Save()
        [PerformanceViewer.Native.ViewerWindowsIdentity]::IdentifyShortcut($Path, $Mode -eq 'Source')
    }
    Set-ViewerShortcut $link
    $pinned = Join-Path $env:APPDATA "Microsoft/Internet Explorer/Quick Launch/User Pinned/TaskBar/$linkName"
    if (Test-Path -LiteralPath $pinned -PathType Leaf) { Set-ViewerShortcut $pinned $true }
    Get-Item -LiteralPath $link
}
finally { $installationLock.Dispose() }
