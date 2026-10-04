# Publishes a GitHub release that the in-app updater picks up: tag vX.Y.Z with Redline-X.Y.Z-x64.msi
# (plus the third-party notices). The version is <Version> in Directory.Build.props; bump and commit it first.
# Usage: powershell -ExecutionPolicy Bypass -File installer/release.ps1 [-Draft] [-Notes "text"]
# Needs: gh (signed in), cargo, cargo-about, .NET 9 SDK.
param([switch]$Draft, [string]$Notes)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    [xml]$props = Get-Content 'Directory.Build.props'
    $version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props <Version> must be X.Y.Z (found '$version')." }
    $tag = "v$version"

    # Release only what is committed and pushed, so the tag matches the published source.
    if (git status --porcelain) { throw 'The working tree has uncommitted changes.' }
    if ((git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Release from main.' }
    git fetch origin --tags --quiet
    if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw 'main is not in sync with origin/main; push (or pull) first.' }
    if (git tag --list $tag) { throw "Tag $tag already exists; bump <Version> in Directory.Build.props." }

    & (Join-Path $PSScriptRoot 'build.ps1')
    $msi = Join-Path $root "artifacts\Redline-$version-x64.msi"
    $notices = Join-Path $root 'artifacts\publish\THIRD-PARTY-NOTICES.txt'
    if (-not (Test-Path $msi)) { throw "$msi was not built." }
    $sha = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Host "==> Tagging $tag and creating the release" -ForegroundColor Cyan
    git tag -a $tag -m "Redline $version"
    git push origin $tag
    $releaseArgs = @($tag, $msi, $notices, '--title', "Redline $version", '--verify-tag')
    # Notes go through a file: PowerShell 5.1 doesn't escape embedded double quotes in native arguments,
    # so --notes "...like "She go"..." reached gh cut off at the first quote (v0.7.0's notes).
    $notesFile = $null
    if ($Notes) {
        $notesFile = Join-Path ([IO.Path]::GetTempPath()) "redline-notes-$version.md"
        [IO.File]::WriteAllText($notesFile, $Notes, (New-Object Text.UTF8Encoding($false)))
        $releaseArgs += @('--notes-file', $notesFile)
    } else { $releaseArgs += '--generate-notes' }
    if ($Draft) { $releaseArgs += '--draft' }
    gh release create @releaseArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE); tag $tag was pushed." }

    # The updater trusts GitHub's asset digest; make sure it matches what was built here.
    $digest = gh release view $tag --json assets --jq ".assets[] | select(.name == \`"Redline-$version-x64.msi\`") | .digest"
    if ($digest -ne "sha256:$sha") { throw "Uploaded MSI digest '$digest' doesn't match the local build (sha256:$sha)." }
    if ($notesFile) {
        Remove-Item $notesFile -ErrorAction SilentlyContinue
        [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
        $body = (gh release view $tag --json body --jq '.body' | Out-String).Trim()
        if (($body -replace "`r", '') -ne ($Notes.Trim() -replace "`r", '')) {
            Write-Warning "The published release notes differ from -Notes; check the release page."
        }
    }
    Write-Host "Released $tag (sha256 $sha)" -ForegroundColor Green
}
finally {
    Pop-Location
}
