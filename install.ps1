<#
  Installs BT Battery Indicator for the current user. No administrator rights needed.
    powershell -ExecutionPolicy Bypass -File install.ps1             install (or update) and start
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  remove the app
    ... -Uninstall -RemoveData                                       also delete settings and history

  What it touches, all inside your own user profile:
    %LOCALAPPDATA%\Programs\BTBatteryIndicator      the app files
    Start Menu\Programs\BT Battery Indicator.lnk    a shortcut
    %LOCALAPPDATA%\BTBatteryIndicator               settings + history (only removed with -RemoveData)
    HKCU\...\Run\BTBatteryIndicator                 only if you switch on "Start with Windows" in the app
#>
param([switch]$Uninstall, [switch]$RemoveData)

$ErrorActionPreference = 'Stop'
$name = 'BTBatteryIndicator'
$target = Join-Path $env:LOCALAPPDATA "Programs\$name"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BT Battery Indicator.lnk'
$data = Join-Path $env:LOCALAPPDATA $name
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process $name -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
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
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $target "$name.exe"
$link.WorkingDirectory = $target
$link.Description = 'Battery levels of your Bluetooth devices'
$link.Save()

# If "Start with Windows" was on for an older copy elsewhere, point it at this one.
$existing = (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue).$name
if ($existing) { Set-ItemProperty -Path $runKey -Name $name -Value ('"{0}" --tray' -f (Join-Path $target "$name.exe")) }

Start-Process (Join-Path $target "$name.exe")
"Installed to $target. Look for the icon near the clock (it may be under the ^ arrow)."
