<#
  Measures what the packaged app costs while sitting idle in the tray.
    .\tools\measure.ps1 [-Seconds 60] [-Demo]
  Starts dist\WirelessDeviceMonitor\WirelessDeviceMonitor.exe quietly, waits for it to settle, then samples
  memory, threads, handles and CPU time. Leaves the app running.
#>
param([int]$Seconds = 60, [switch]$Demo)

$exe = Join-Path (Split-Path $PSScriptRoot) 'dist\WirelessDeviceMonitor\WirelessDeviceMonitor.exe'
Get-Process WirelessDeviceMonitor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep 1
$appArgs = @('--tray'); if ($Demo) { $appArgs += '--demo' }
$p = Start-Process $exe -ArgumentList $appArgs -PassThru
Start-Sleep 20
$p.Refresh()
$cpu0 = $p.TotalProcessorTime
Start-Sleep $Seconds
$p.Refresh()
$cpu = ($p.TotalProcessorTime - $cpu0).TotalMilliseconds
$folder = Split-Path $exe
[pscustomobject]@{
    'Working set MB (RAM in use)'  = [math]::Round($p.WorkingSet64 / 1MB, 1)
    'Private MB (committed)'       = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    'Threads'                      = $p.Threads.Count
    'Handles'                      = $p.HandleCount
    "CPU ms over $Seconds s idle"  = [math]::Round($cpu)
    'CPU % of one core'            = [math]::Round($cpu / ($Seconds * 10), 3)
    'Startup CPU ms'               = [math]::Round($cpu0.TotalMilliseconds)
    'Install size MB'              = [math]::Round((Get-ChildItem $folder -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
} | Format-List
