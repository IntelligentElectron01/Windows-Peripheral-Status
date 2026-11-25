<#
  Installs BT Battery Indicator for the current user. No administrator rights needed.
    powershell -ExecutionPolicy Bypass -File install.ps1             install (or update) and start
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  remove the app
    ... -Uninstall -RemoveData                                       also delete settings and history
    ... -Quiet                                                       update in place and restart silently in the tray
    ... -NoDesktopShortcut                                           skip the desktop shortcut

  What it touches, all inside your own user profile:
    %LOCALAPPDATA%\Programs\BTBatteryIndicator      the app files
    Start Menu\Programs\BT Battery Indicator.lnk    a shortcut (search "BT Battery" in Start)
    Desktop\BT Battery Indicator.lnk                a shortcut
    %LOCALAPPDATA%\BTBatteryIndicator               settings + history (only removed with -RemoveData)
    HKCU\...\Run\BTBatteryIndicator                 only if you switch on "Start with Windows" in the app
#>
param([switch]$Uninstall, [switch]$RemoveData, [switch]$Quiet, [switch]$NoDesktopShortcut)

$ErrorActionPreference = 'Stop'
$name = 'BTBatteryIndicator'
$target = Join-Path $env:LOCALAPPDATA "Programs\$name"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BT Battery Indicator.lnk'
$data = Join-Path $env:LOCALAPPDATA $name
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'BT Battery Indicator.lnk'

# Only one copy can run at a time, so stop any running copy (installed or run from a build folder).
Get-Process $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    if (Test-Path $desktop) { Remove-Item $desktop -Force }
    Remove-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue
    if ($RemoveData -and (Test-Path $data)) { Remove-Item $data -Recurse -Force }
    'BT Battery Indicator removed.'
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
    $link.Description = 'Battery levels of your Bluetooth devices'
    $link.Save()
}

# If "Start with Windows" was on for an older copy elsewhere, point it at this one.
$existing = (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue).$name
if ($existing) { Set-ItemProperty -Path $runKey -Name $name -Value ('"{0}" --tray' -f (Join-Path $target "$name.exe")) }

if ($Quiet) { Start-Process (Join-Path $target "$name.exe") -ArgumentList '--tray' } else { Start-Process (Join-Path $target "$name.exe") }
"Installed to $target. Look for the icon near the clock (it may be under the ^ arrow)."
