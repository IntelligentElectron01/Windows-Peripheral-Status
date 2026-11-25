<#
  Builds, tests, or packages BT Battery Indicator.
    .\build.ps1            build + run automated tests
    .\build.ps1 run        build and start the app
    .\build.ps1 demo       build and start the app with made-up devices
    .\build.ps1 package    make dist\BTBatteryIndicator-win-x64.zip (self-contained)
  Uses the project-local .NET SDK in .tools\dotnet if present, otherwise a system-wide 'dotnet'.
#>
param([ValidateSet('test', 'run', 'demo', 'package', 'dump', 'shots')] [string]$Task = 'test')

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
$exe = Join-Path $root 'src\App\bin\Debug\net10.0-windows\BTBatteryIndicator.exe'

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
    'package' {
        $out = Join-Path $root 'dist\BTBatteryIndicator'
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        & $dotnet publish $app -c Release -r win-x64 --self-contained true -o $out -v q --nologo `
            -p:DebugType=none -p:PublishReadyToRun=false
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $out
        $zip = Join-Path $root 'dist\BTBatteryIndicator-win-x64.zip'
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
        "Packaged: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
        "SHA-256:  $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())"
    }
}
