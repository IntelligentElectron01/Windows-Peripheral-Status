<#
  Installs Wireless Device Monitor for the current user. No administrator rights needed.
    powershell -ExecutionPolicy Bypass -File install.ps1             install (or update) and start
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  remove the app
    ... -Uninstall -RemoveData                                       also delete settings and history
    ... -Quiet                                                       update in place and restart silently in the tray
    ... -NoDesktopShortcut                                           skip the desktop shortcut

  What it touches, all inside your own user profile:
    %LOCALAPPDATA%\Programs\WirelessDeviceMonitor      the app files
    Start Menu\Programs\Wireless Device Monitor.lnk    a shortcut (search "Wireless Device" in Start)
    Desktop\Wireless Device Monitor.lnk                a shortcut
    %LOCALAPPDATA%\WirelessDeviceMonitor               settings + history (only removed with -RemoveData)
    HKCU\...\Run\WirelessDeviceMonitor                 only if you switch on "Start with Windows" in the app
#>
param([switch]$Uninstall, [switch]$RemoveData, [switch]$Quiet, [switch]$NoDesktopShortcut)

$ErrorActionPreference = 'Stop'
$name = 'WirelessDeviceMonitor'
$target = Join-Path $env:LOCALAPPDATA "Programs\$name"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Wireless Device Monitor.lnk'
$data = Join-Path $env:LOCALAPPDATA $name
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Wireless Device Monitor.lnk'

# The app was called "BT Battery Indicator" before version 1.0. A copy installed under that name is replaced,
# not left beside the new one, and its settings and history are carried over.
$oldName = 'BTBatteryIndicator'
$oldTarget = Join-Path $env:LOCALAPPDATA "Programs\$oldName"
$oldData = Join-Path $env:LOCALAPPDATA $oldName
$oldShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BT Battery Indicator.lnk'
$oldDesktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'BT Battery Indicator.lnk'

# Only one copy can run at a time, so stop any running copy (installed or run from a build folder).
Get-Process $name, $oldName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$hadOldAutostart = [bool](Get-ItemProperty -Path $runKey -Name $oldName -ErrorAction SilentlyContinue)
if (Test-Path $oldTarget) { Remove-Item $oldTarget -Recurse -Force }
foreach ($old in $oldShortcut, $oldDesktop) { if (Test-Path $old) { Remove-Item $old -Force } }
Remove-ItemProperty -Path $runKey -Name $oldName -ErrorAction SilentlyContinue
if ((Test-Path $oldData) -and -not (Test-Path $data) -and -not $Uninstall) { Move-Item $oldData $data }

if ($Uninstall) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    if (Test-Path $desktop) { Remove-Item $desktop -Force }
    Remove-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue
    if ($RemoveData) { foreach ($d in $data, $oldData) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } } }
    'Wireless Device Monitor removed.'
    return
}

$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source "$name.exe"))) {
    throw "Run this script from the unzipped app folder (the one containing $name.exe)."
}
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $source '*') $target -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$links = @($shortcut); if (-not $NoDesktopShortcut) { $links += $desktop }
foreach ($path in $links) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = Join-Path $target "$name.exe"
    $link.WorkingDirectory = $target
    $link.Description = 'Battery and connection status of your wireless devices'
    $link.Save()
}

# If "Start with Windows" was on for an older copy elsewhere, point it at this one.
$existing = (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue).$name
if ($existing -or $hadOldAutostart) { Set-ItemProperty -Path $runKey -Name $name -Value ('"{0}" --tray' -f (Join-Path $target "$name.exe")) }

if ($Quiet) { Start-Process (Join-Path $target "$name.exe") -ArgumentList '--tray' } else { Start-Process (Join-Path $target "$name.exe") }
"Installed to $target. Look for the icon near the clock (it may be under the ^ arrow)."
