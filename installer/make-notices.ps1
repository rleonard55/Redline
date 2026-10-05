# Writes THIRD-PARTY-NOTICES.txt for a publish folder: Redline's license, Harper and every Rust crate
# compiled into harper_ffi.dll (via cargo-about), LLamaSharp + llama.cpp (installer/licenses, fetched from
# upstream; their NuGet packages carry only a license expression), and the bundled .NET runtime / Microsoft.Extensions.
# Usage: powershell -ExecutionPolicy Bypass -File installer/make-notices.ps1 -PublishDir artifacts\publish
# Needs cargo-about: cargo install cargo-about --locked --features cli
# -ModelIncluded: the offline installer ships the GRMR-V3 model; its section then carries the full license texts.
param([Parameter(Mandatory)][string]$PublishDir, [switch]$ModelIncluded)
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
# UTF-8 without BOM: Get-Content -Raw would read it as ANSI in PowerShell 5.1.
function LicenseFile([string]$name) { [System.IO.File]::ReadAllText((Join-Path $root "installer\licenses\$name")) }
function PackageVersion([string]$id) {
    $lib = $deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like "$id/*" } | Select-Object -First 1
    if (-not $lib) { throw "$id not found in Redline.deps.json" }
    return $lib.Split('/')[1]
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
[void]$sb.AppendLine("LLamaSharp $(PackageVersion 'LLamaSharp') (LLamaSharp.dll), runtime for the optional AI grammar model")
[void]$sb.AppendLine('https://github.com/SciSharp/LLamaSharp')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine((LicenseFile 'LLamaSharp.txt'))
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine('llama.cpp / ggml (runtimes\win-x64\native\*\llama.dll, ggml*.dll), shipped in LLamaSharp.Backend.Cpu and LLamaSharp.Backend.Vulkan.Windows')
[void]$sb.AppendLine('(the Vulkan build uses the Vulkan loader, vulkan-1.dll, installed with the graphics driver; it is not shipped)')
[void]$sb.AppendLine('https://github.com/ggml-org/llama.cpp')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine((LicenseFile 'llama.cpp.txt'))
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine("CommunityToolkit.HighPerformance $(PackageVersion 'CommunityToolkit.HighPerformance') (used by LLamaSharp)")
[void]$sb.AppendLine('https://github.com/CommunityToolkit/dotnet')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine((LicenseFile 'CommunityToolkit.txt'))
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine("Microsoft.Extensions.AI.Abstractions $(PackageVersion 'Microsoft.Extensions.AI.Abstractions') (used by LLamaSharp)")
[void]$sb.AppendLine('https://github.com/dotnet/extensions')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine((LicenseFile 'dotnet-extensions.txt'))
[void]$sb.AppendLine($rule)
if ($ModelIncluded) {
    [void]$sb.AppendLine('GRMR-V3-G1B grammar model (included; installed to %LOCALAPPDATA%\Redline\models)')
} else {
    [void]$sb.AppendLine('GRMR-V3-G1B grammar model (not included; downloaded only if you turn on AI grammar)')
}
[void]$sb.AppendLine('https://huggingface.co/qingy2024/GRMR-V3-G1B')
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine('The model is licensed under Apache-2.0 by its author. It is fine-tuned from Google''s Gemma 3 1B,')
[void]$sb.AppendLine('so its use is also subject to the Gemma Terms of Use and Prohibited Use Policy:')
[void]$sb.AppendLine('https://ai.google.dev/gemma/terms')
[void]$sb.AppendLine()
if ($ModelIncluded) {
    [void]$sb.AppendLine('Gemma is provided under and subject to the Gemma Terms of Use found at ai.google.dev/gemma/terms')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('You must not use the model for the restricted uses in the Gemma Prohibited Use Policy or in')
    [void]$sb.AppendLine('violation of applicable laws and regulations (Gemma Terms of Use, Section 3.2).')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine((LicenseFile 'Apache-2.0.txt'))
    [void]$sb.AppendLine($rule)
    [void]$sb.AppendLine((LicenseFile 'Gemma-Terms-of-Use.txt'))
    [void]$sb.AppendLine($rule)
    [void]$sb.AppendLine((LicenseFile 'Gemma-Prohibited-Use-Policy.txt'))
}
[void]$sb.AppendLine($rule)
[void]$sb.AppendLine(".NET runtime $core (Microsoft.NETCore.App), Microsoft.Extensions and System.* libraries")
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
