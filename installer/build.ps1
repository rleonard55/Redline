# Builds artifacts\Redline-<version>-x64.msi: Harper (cargo), a self-contained win-x64 publish, then WiX.
# Usage: powershell -ExecutionPolicy Bypass -File installer/build.ps1 [-SkipHarper] [-Version 1.2.3]
# The version defaults to <Version> in Directory.Build.props.
param([switch]$SkipHarper, [string]$Version)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$harperDll = Join-Path $root 'native\harper-ffi\target\release\harper_ffi.dll'
$versionArgs = @()  # assigned separately: an if-expression would unwrap a one-item array into a string
if ($Version) { $versionArgs = @("-p:Version=$Version") }

function Invoke-Step([string]$name, [scriptblock]$block) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
}

if (-not $SkipHarper) {
    Invoke-Step 'cargo build --release (harper-ffi)' {
        Push-Location (Join-Path $root 'native\harper-ffi')
        try { cargo build --release } finally { Pop-Location }
    }
}
if (-not (Test-Path $harperDll)) { throw "harper_ffi.dll not found at $harperDll; run without -SkipHarper." }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Invoke-Step 'dotnet publish (self-contained win-x64)' {
    dotnet publish (Join-Path $root 'src\Redline.App\Redline.App.csproj') -c Release -r win-x64 --self-contained true `
        -p:DebugType=none -o $publish --nologo @versionArgs
}
foreach ($required in 'Redline.exe', 'harper_ffi.dll') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "$required is missing from the publish output." }
}

Invoke-Step 'WiX build' {
    dotnet build (Join-Path $PSScriptRoot 'Redline.Installer.wixproj') -c Release "-p:PublishDir=$publish\" -o $artifacts --nologo @versionArgs
}

$msi = Get-ChildItem $artifacts -Filter 'Redline-*.msi' | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host ("Built {0} ({1:N1} MB)" -f $msi.FullName, ($msi.Length / 1MB)) -ForegroundColor Green
