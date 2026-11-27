<#
  Builds, tests, or packages Wireless Device Monitor.
    .\build.ps1            build + run automated tests
    .\build.ps1 run        build and start the app
    .\build.ps1 demo       build and start the app with made-up devices
    .\build.ps1 package    make dist\WirelessDeviceMonitor-win-x64.zip (self-contained); if the app is installed on this PC,
                           the installed copy is updated and restarted automatically
    .\build.ps1 install    package, then install for this user (Start Menu + desktop shortcut) and start it
  Uses the project-local .NET SDK in .tools\dotnet if present, otherwise a system-wide 'dotnet'.
#>
param([ValidateSet('test', 'run', 'demo', 'package', 'dump', 'pods', 'logi', 'links', 'shots', 'install')] [string]$Task = 'test')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$local = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $local) { $local } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'   # the SDK's own usage reporting: off
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Split-Path $dotnet
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'   # keep downloaded runtime packs inside the project

$app = Join-Path $root 'src\App\App.csproj'
$tests = Join-Path $root 'tests\Tests.csproj'
$exe = Join-Path $root 'src\App\bin\Debug\net10.0-windows\WirelessDeviceMonitor.exe'

function Build([string]$proj) {
    & $dotnet build $proj -c Debug -v q --nologo -clp:NoSummary
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
}

switch ($Task) {
    'test' {
        Build $app
        & $dotnet run --project $tests -c Debug -v q --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    'run' { Build $app; Start-Process $exe }
    'demo' { Build $app; Start-Process $exe -ArgumentList '--demo' }
    'shots' { Build $app; Start-Process $exe -ArgumentList '--shot', ('"' + (Join-Path $root 'dist\shots') + '"') -Wait }
    'dump' { Build $tests; & (Join-Path $root 'tests\bin\Debug\net10.0-windows\Tests.exe') dump }
    'links' { Build $tests; & (Join-Path $root 'tests\bin\Debug\net10.0-windows\Tests.exe') links }
    'logi' { Build $tests; & (Join-Path $root 'tests\bin\Debug\net10.0-windows\Tests.exe') logi }
    'pods' { Build $tests; & (Join-Path $root 'tests\bin\Debug\net10.0-windows\Tests.exe') pods }
    { $_ -in 'package', 'install' } {
        $out = Join-Path $root 'dist\WirelessDeviceMonitor'
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        & $dotnet publish $app -c Release -r win-x64 --self-contained true -o $out -v q --nologo `
            -p:DebugType=none -p:PublishReadyToRun=false -p:DisableTransitiveFrameworkReferenceDownloads=true
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $out
        $zip = Join-Path $root 'dist\WirelessDeviceMonitor-win-x64.zip'
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
        "Packaged: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
        "SHA-256:  $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())"
        # Keep the installed copy in step with what was just built.
        # (Also when it is still installed under its earlier name, which the installer replaces.)
        $installed = 'WirelessDeviceMonitor', 'BTBatteryIndicator' | ForEach-Object { Join-Path $env:LOCALAPPDATA "Programs\$_\$_.exe" } | Where-Object { Test-Path $_ }
        $onDesktop = 'Wireless Device Monitor.lnk', 'BT Battery Indicator.lnk' | ForEach-Object { Join-Path ([Environment]::GetFolderPath('Desktop')) $_ } | Where-Object { Test-Path $_ }
        if ($Task -eq 'install') { & (Join-Path $out 'install.ps1') }
        elseif ($installed) { & (Join-Path $out 'install.ps1') -Quiet -NoDesktopShortcut:(-not $onDesktop) | ForEach-Object { "Updated installed copy. $_" } }
    }
}
