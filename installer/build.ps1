# Builds artifacts\Redline-<version>-x64.msi: Harper (cargo), a self-contained win-x64 publish, then WiX.
# Usage: powershell -ExecutionPolicy Bypass -File installer/build.ps1 [-SkipHarper] [-Version 1.2.3] [-Offline [-ModelPath x.gguf]]
# The version defaults to <Version> in Directory.Build.props.
# -Offline builds Redline-<version>-x64-offline.msi instead, with the GRMR-V3 grammar model (~800 MB) inside. The model
# comes from -ModelPath, else this machine's downloaded copy, else its pinned URL; size and SHA-256 must match the pins
# in GrmrModelStore.cs (the app's own check).
param([switch]$SkipHarper, [string]$Version, [switch]$Offline, [string]$ModelPath)
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

# Licenses of everything bundled (Harper and its crates, .NET); installed next to Redline.exe.
& (Join-Path $PSScriptRoot 'make-notices.ps1') -PublishDir $publish -ModelIncluded:$Offline

$modelArgs = @()
if ($Offline) {
    $store = [IO.File]::ReadAllText((Join-Path $root 'src\Redline.Analysis.Grmr\GrmrModelStore.cs'))
    $fileName = [regex]::Match($store, 'FileName = "([^"]+)"').Groups[1].Value
    $size = [long]([regex]::Match($store, 'Size = ([\d_]+);').Groups[1].Value -replace '_', '')
    $sha = [regex]::Match($store, 'Sha256 = "([0-9a-f]{64})"').Groups[1].Value
    $urlParts = [regex]::Matches([regex]::Match($store, 'Url =\s*([^;]+);').Groups[1].Value, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }
    $url = ($urlParts -join '') -replace 'FileName$', ''
    if (-not $url.EndsWith($fileName)) { $url += $fileName }
    if (-not $fileName -or -not $size -or -not $sha) { throw 'Could not read the model pins from GrmrModelStore.cs.' }

    $stage = Join-Path $artifacts 'model'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory $stage | Out-Null
    $staged = Join-Path $stage $fileName
    if (-not $ModelPath) {
        $local = Join-Path $env:LOCALAPPDATA "Redline\models\$fileName"
        if (Test-Path $local) { $ModelPath = $local }
    }
    if ($ModelPath) {
        Write-Host "==> Model from $ModelPath" -ForegroundColor Cyan
        Copy-Item $ModelPath $staged
    } else {
        Write-Host "==> Downloading the model from $url" -ForegroundColor Cyan
        Invoke-WebRequest -Uri $url -OutFile $staged -UseBasicParsing
    }
    $actualSize = (Get-Item $staged).Length
    $actualSha = (Get-FileHash $staged -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSize -ne $size -or $actualSha -ne $sha) {
        throw "Model check failed: $actualSize bytes, sha256 $actualSha (expected $size, $sha)."
    }
    Write-Host "    verified $fileName ($size bytes, sha256 $sha)"

    # Gemma Terms of Use 3.1: recipients get the terms and a NOTICE file with this exact sentence.
    $licenses = Join-Path $PSScriptRoot 'licenses'
    Copy-Item (Join-Path $licenses 'Gemma-Terms-of-Use.txt'), (Join-Path $licenses 'Gemma-Prohibited-Use-Policy.txt') $stage
    Copy-Item (Join-Path $licenses 'Apache-2.0.txt') (Join-Path $stage 'GRMR-V3-G1B-LICENSE.txt')
    $notice = @(
        ('GRMR-V3-G1B grammar model (' + $fileName + '), installed by the Redline offline installer.'),  # parenthesized: ',' binds tighter than '+'
        '',
        'Gemma is provided under and subject to the Gemma Terms of Use found at ai.google.dev/gemma/terms',
        '',
        'This model is a Model Derivative of Google''s Gemma 3 1B: qingy2024/GRMR-V3-G1B',
        '(https://huggingface.co/qingy2024/GRMR-V3-G1B), licensed under Apache-2.0 (GRMR-V3-G1B-LICENSE.txt),',
        'quantized to GGUF by its author (https://huggingface.co/qingy2024/GRMR-V3-G1B-GGUF). Redline distributes',
        'the file unmodified.',
        '',
        'Use of the model is subject to the use restrictions in Section 3.2 of the Gemma Terms of Use',
        '(Gemma-Terms-of-Use.txt), including the Gemma Prohibited Use Policy (Gemma-Prohibited-Use-Policy.txt).'
    ) -join "`r`n"
    [IO.File]::WriteAllText((Join-Path $stage 'NOTICE.txt'), $notice + "`r`n", (New-Object Text.UTF8Encoding($false)))

    # The license agreement the installer shows (WixUI_Minimal): Redline, then the model's terms.
    function Rtf([string]$text) {
        $sb = New-Object System.Text.StringBuilder
        foreach ($c in $text.ToCharArray()) {
            switch -regex ([string]$c) {
                '\\' { [void]$sb.Append('\\'); continue }
                '\{' { [void]$sb.Append('\{'); continue }
                '\}' { [void]$sb.Append('\}'); continue }
                "`n" { [void]$sb.Append("\par`r`n"); continue }
                "`r" { continue }
                default { if ([int]$c -gt 127) { [void]$sb.Append('\u' + [int]$c + '?') } else { [void]$sb.Append($c) } }
            }
        }
        return $sb.ToString()
    }
    function Heading([string]$text) { return '\pard\sa120\b ' + (Rtf $text) + '\b0\par' + "`r`n" + '\pard\sa60 ' }
    $intro = 'This installer includes the GRMR-V3-G1B grammar model used by Redline''s optional AI grammar check. ' +
        'By installing, you agree to the license terms below: Redline''s license (MIT); the model''s license (Apache-2.0); ' +
        'and, because the model is derived from Google''s Gemma, the Gemma Terms of Use, including the use restrictions in ' +
        'its Section 3.2 and the Gemma Prohibited Use Policy. You must not use the model for the restricted uses in the ' +
        'Prohibited Use Policy or in violation of applicable laws and regulations.' + "`n`n" +
        'Gemma is provided under and subject to the Gemma Terms of Use found at ai.google.dev/gemma/terms'
    $rtf = '{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Segoe UI;}}\fs18' + "`r`n" +
        (Heading 'Redline with the GRMR-V3-G1B grammar model') + (Rtf $intro) + '\par' + "`r`n" +
        (Heading 'Redline (MIT License)') + (Rtf ([IO.File]::ReadAllText((Join-Path $root 'LICENSE')))) + "`r`n" +
        (Heading 'Gemma Terms of Use') + (Rtf ([IO.File]::ReadAllText((Join-Path $licenses 'Gemma-Terms-of-Use.txt')))) + "`r`n" +
        (Heading 'Gemma Prohibited Use Policy') + (Rtf ([IO.File]::ReadAllText((Join-Path $licenses 'Gemma-Prohibited-Use-Policy.txt')))) + "`r`n" +
        (Heading 'GRMR-V3-G1B (Apache License 2.0)') + (Rtf ([IO.File]::ReadAllText((Join-Path $licenses 'Apache-2.0.txt')))) + "`r`n}"
    $licenseRtf = Join-Path $artifacts 'offline-license.rtf'
    [IO.File]::WriteAllText($licenseRtf, $rtf, [Text.Encoding]::ASCII)
    $modelArgs = @("-p:ModelDir=$stage\", "-p:LicenseRtf=$licenseRtf")
}

Invoke-Step 'WiX build' {
    dotnet build (Join-Path $PSScriptRoot 'Redline.Installer.wixproj') -c Release "-p:PublishDir=$publish\" -o $artifacts --nologo @versionArgs @modelArgs
}

if ($Offline) { Remove-Item (Join-Path $artifacts 'model') -Recurse -Force }  # the staged 800 MB copy

$msi = Get-ChildItem $artifacts -Filter 'Redline-*.msi' | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host ("Built {0} ({1:N1} MB)" -f $msi.FullName, ($msi.Length / 1MB)) -ForegroundColor Green
