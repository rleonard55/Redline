# Writes THIRD-PARTY-NOTICES.txt for a publish folder: Redline's license, Harper and every Rust crate
# compiled into harper_ffi.dll (via cargo-about), and the bundled .NET runtime / Microsoft.Extensions.
# Usage: powershell -ExecutionPolicy Bypass -File installer/make-notices.ps1 -PublishDir artifacts\publish
# Needs cargo-about: cargo install cargo-about --locked --features cli
param([Parameter(Mandatory)][string]$PublishDir)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$harper = Join-Path $root 'native\harper-ffi'
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$out = Join-Path $PublishDir 'THIRD-PARTY-NOTICES.txt'
$rule = '=' * 80

# The runtime packs actually bundled, from the self-contained publish's deps.json.
$deps = Get-Content (Join-Path $PublishDir 'Redline.deps.json') -Raw | ConvertFrom-Json
$packs = @{}
foreach ($target in $deps.targets.PSObject.Properties) {
    foreach ($lib in $target.Value.PSObject.Properties.Name) {
        if ($lib -match '^runtimepack\.(Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64)/(.+)$') {
            $packs[$Matches[1]] = $Matches[3]
        }
    }
}
foreach ($name in 'Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') {
    if (-not $packs[$name]) { throw "Runtime pack $name not found in Redline.deps.json (is the publish self-contained?)" }
}
function PackFile([string]$pack, [string]$version, [string[]]$names) {
    foreach ($n in $names) {
        $p = Join-Path $nuget (Join-Path $pack.ToLowerInvariant() (Join-Path $version $n))
        if (Test-Path $p) { return (Get-Content $p -Raw) }
    }
    throw "None of $($names -join ', ') found for $pack $version"
}

Write-Host '==> cargo about generate' -ForegroundColor Cyan
$rust = Join-Path $env:TEMP "redline-rust-notices-$PID.txt"
Push-Location $harper
try {
    cargo about generate -c about.toml about.hbs -o $rust
    if ($LASTEXITCODE -ne 0) { throw "cargo about failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

$core = $packs['Microsoft.NETCore.App.Runtime.win-x64']
$desktop = $packs['Microsoft.WindowsDesktop.App.Runtime.win-x64']
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('Redline - third-party notices')
[void]$sb.AppendLine()
[void]$sb.AppendLine('Redline is MIT-licensed (below). It includes the third-party software listed in this file,')
[void]$sb.AppendLine('each under its own license. Source for every Rust crate is available at the crates.io link')
[void]$sb.AppendLine('given next to it; source for .NET is at https://github.com/dotnet.')
[void]$sb.AppendLine()
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine('Redline')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine((Get-Content (Join-Path $root 'LICENSE') -Raw))
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine('Harper grammar checker and its Rust dependencies (harper_ffi.dll)')
[void]$sb.AppendLine('https://github.com/Automattic/harper')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine((Get-Content $rust -Raw))
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine(".NET runtime $core (Microsoft.NETCore.App) and Microsoft.Extensions libraries")
[void]$sb.AppendLine('https://github.com/dotnet/runtime')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine((PackFile 'Microsoft.NETCore.App.Runtime.win-x64' $core 'LICENSE.TXT', 'LICENSE'))
[void]$sb.AppendLine((PackFile 'Microsoft.NETCore.App.Runtime.win-x64' $core 'THIRD-PARTY-NOTICES.TXT'))
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine("Windows Desktop runtime $desktop (WPF, Windows Forms)")
[void]$sb.AppendLine('https://github.com/dotnet/wpf, https://github.com/dotnet/winforms')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine((PackFile 'Microsoft.WindowsDesktop.App.Runtime.win-x64' $desktop 'LICENSE.TXT', 'LICENSE'))

[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding $true))
Remove-Item $rust -ErrorAction SilentlyContinue
Write-Host ("Wrote {0} ({1:N0} KB)" -f $out, ((Get-Item $out).Length / 1KB))
