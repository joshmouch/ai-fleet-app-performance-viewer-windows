[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Shortcut,
    [Parameter(Mandatory)][string]$Pin
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Pin -PathType Leaf) { return }
if (![Environment]::UserInteractive) { throw 'Run the installer on the signed-in Windows desktop to create missing taskbar pins.' }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ViewerTaskbarInput
{
    [DllImport("user32.dll")] public static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
'@

$element = [Windows.Automation.AutomationElement]
$desktop = $element::RootElement
$trayClass = [Windows.Automation.PropertyCondition]::new($element::ClassNameProperty, 'Shell_TrayWnd')
$tray = $desktop.FindFirst('Children', $trayClass)
if (!$tray) { throw 'The Windows Explorer taskbar is unavailable.' }
$buttonClass = [Windows.Automation.PropertyCondition]::new($element::ClassNameProperty, 'Taskbar.TaskListButtonAutomationPeer')
$title = [IO.Path]::GetFileNameWithoutExtension($Shortcut)
$session = (Get-Process -Id $PID).SessionId

function Get-ViewerWindows {
    Get-Process -Name PerformanceViewer.Native -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $session
}

function Send-TaskbarKey([byte]$Key) {
    [ViewerTaskbarInput]::keybd_event($Key, 0, 0, [UIntPtr]::Zero)
    [ViewerTaskbarInput]::keybd_event($Key, 0, 2, [UIntPtr]::Zero)
}

$existing = @(Get-ViewerWindows)
$viewer = $existing | Where-Object MainWindowTitle -eq $title | Select-Object -First 1
$launched = !$viewer
try {
    if ($launched) {
        $link = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut)
        $launcherVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($link.TargetPath)
        $version = [version]::new($launcherVersion.FileMajorPart, $launcherVersion.FileMinorPart, $launcherVersion.FileBuildPart)
        if ($version -ge [version]'1.0.1') {
            Start-Process -FilePath $link.TargetPath -ArgumentList $link.Arguments, '--pin-preview' -WindowStyle Hidden
        } else {
            Start-Process -FilePath $Shortcut
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (!$viewer) {
            $viewer = Get-ViewerWindows | Where-Object MainWindowTitle -eq $title | Select-Object -First 1
        }
        $button = $tray.FindAll('Descendants', $buttonClass) |
            Where-Object { $_.Current.Name.StartsWith("$title - ", [StringComparison]::Ordinal) } |
            Select-Object -First 1
        if ($viewer -and $button) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$viewer -or !$button) { throw "The Windows taskbar did not show $title." }

    # Win+T brings Explorer's taskbar in front of windows covering it.
    [ViewerTaskbarInput]::keybd_event(91, 0, 0, [UIntPtr]::Zero)
    try { Send-TaskbarKey 84 }
    finally { [ViewerTaskbarInput]::keybd_event(91, 0, 2, [UIntPtr]::Zero) }

    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        $rect = $button.Current.BoundingRectangle
        $hit = $null
        if (!$rect.IsEmpty) {
            $point = [Windows.Point]::new($rect.Left + $rect.Width / 2, $rect.Top + $rect.Height / 2)
            $hit = $element::FromPoint($point)
            while ($hit -and $hit.Current.ClassName -ne 'Shell_TrayWnd') { $hit = $walker.GetParent($hit) }
        }
        if ($hit) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$hit -or ![ViewerTaskbarInput]::SetPhysicalCursorPos([int]$point.X, [int]$point.Y)) {
        throw "The taskbar button for $title is covered or outside the interactive desktop."
    }
    [ViewerTaskbarInput]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero)
    [ViewerTaskbarInput]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        $focused = $element::FocusedElement.Current
        if ($focused.Name -eq $title -and $focused.AutomationId -eq 'Open') { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($focused.Name -ne $title -or $focused.AutomationId -ne 'Open') {
        throw "Explorer did not open the taskbar menu for $title."
    }

    Send-TaskbarKey 40 # Down from the app name to the pin action.
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $focused = $element::FocusedElement.Current
        if ($focused.AutomationId -eq 'TaskbarPin') { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($focused.AutomationId -ne 'TaskbarPin') { throw "Explorer did not offer Pin to taskbar for $title." }
    Send-TaskbarKey 13

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (!(Test-Path -LiteralPath $Pin -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 150
    }
    if (!(Test-Path -LiteralPath $Pin -PathType Leaf)) { throw "Explorer did not create the taskbar pin for $title." }
}
finally {
    if ($launched -and $viewer -and $viewer.Id -notin $existing.Id -and !$viewer.HasExited) {
        [void]$viewer.CloseMainWindow()
        [void]$viewer.WaitForExit(10000)
    }
}
