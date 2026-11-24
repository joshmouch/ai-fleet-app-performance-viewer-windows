[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Package', 'Source', 'Both')][string]$Mode,
    [Parameter(Mandatory)][string]$Package,
    [string]$SourceCheckout
)

$ErrorActionPreference = 'Stop'
$packageFile = (Get-Item -LiteralPath $Package).FullName
if ($Mode -eq 'Both' -and !$SourceCheckout) { $SourceCheckout = Split-Path -Parent $PSScriptRoot }
if ($Mode -ne 'Package') {
    if (!$SourceCheckout) { throw 'Source mode needs the viewer checkout.' }
    $SourceCheckout = (Resolve-Path -LiteralPath $SourceCheckout).Path
    if (!(Test-Path -LiteralPath (Join-Path $SourceCheckout 'Web/PerformanceViewer.Web.csproj') -PathType Leaf)) {
        throw "The web project was not found in $SourceCheckout."
    }
}
$modes = if ($Mode -eq 'Both') { 'Package', 'Source' } else { $Mode }

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
    $programs = [Environment]::GetFolderPath('Programs')
    $taskbar = Join-Path $env:APPDATA 'Microsoft/Internet Explorer/Quick Launch/User Pinned/TaskBar'
    $desktop = New-Object -ComObject WScript.Shell
    $identityType = 'PerformanceViewer.Native.ViewerWindowsIdentity' -as [type]
    if (!$identityType) {
        Add-Type -Path (Join-Path $PSScriptRoot 'Platforms/Windows/ViewerWindowsIdentity.cs')
    }
    function Set-ViewerShortcut([string]$Path, [string]$InstallMode, [bool]$VerifyOwner = $false) {
        $shortcut = $desktop.CreateShortcut($Path)
        $oldModeRoot = Join-Path $installRoot $InstallMode
        if ($VerifyOwner -and $shortcut.TargetPath -ne $launcher -and
            !$shortcut.TargetPath.StartsWith(($oldModeRoot + '\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "The existing pin $Path belongs to another installation."
        }
        if ($shortcut.TargetPath -eq $launcher -and $shortcut.Arguments -eq $InstallMode -and
            $shortcut.WorkingDirectory -eq $installRoot) { return }
        $shortcut.TargetPath = $launcher
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.Arguments = $InstallMode
        $shortcut.IconLocation = $shortcut.TargetPath + ',0'
        $shortcut.Description = "AI Fleet Cluster Performance Viewer ($InstallMode)"
        $shortcut.Save()
        [PerformanceViewer.Native.ViewerWindowsIdentity]::IdentifyShortcut($Path, $InstallMode -eq 'Source')
    }
    $shortcuts = foreach ($installMode in $modes) {
        $content = if ($installMode -eq 'Source') { $SourceCheckout } else { $release }
        $record = @{Shell = $shellPath; Content = $content} | ConvertTo-Json -Compress
        $modeSettings = Join-Path $installRoot "$installMode.json"
        $pendingSettings = Join-Path $installRoot ('.settings-' + [Guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText($pendingSettings, $record)
        [IO.File]::Move($pendingSettings, $modeSettings, $true)

        $linkName = "Performance Viewer ($installMode).lnk"
        $link = Join-Path $programs $linkName
        Set-ViewerShortcut $link $installMode
        [pscustomobject]@{ Mode = $installMode; StartMenu = $link; Taskbar = (Join-Path $taskbar $linkName) }
    }
    foreach ($shortcut in $shortcuts) {
        if ($Mode -eq 'Both' -and !(Test-Path -LiteralPath $shortcut.Taskbar -PathType Leaf)) {
            $pinScript = Join-Path $PSScriptRoot 'Pin-ViewerTaskbar.ps1'
            $windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
            & $windowsPowerShell -NoLogo -NoProfile -NonInteractive -STA -File $pinScript -Shortcut $shortcut.StartMenu -Pin $shortcut.Taskbar
            if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $shortcut.Taskbar -PathType Leaf)) {
                throw "Both modes are installed. Windows could not pin $($shortcut.Mode) to the taskbar."
            }
        }
        if (Test-Path -LiteralPath $shortcut.Taskbar -PathType Leaf) {
            Set-ViewerShortcut $shortcut.Taskbar $shortcut.Mode $true
        }
        Get-Item -LiteralPath $shortcut.StartMenu
    }
}
finally { $installationLock.Dispose() }
