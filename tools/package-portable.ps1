# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>
#
# Build + package the GhostShell portable distribution as a release-
# ready zip + SHA-256 digest. Companion to tools\package-chromium.ps1
# -- the workflow for cutting a release is:
#
#     1. Bump VERSION file.
#     2. Run tools\package-portable.ps1   -> zip + hash of the .NET app.
#     3. Run tools\package-chromium.ps1   -> zip + hash of the Chromium.
#     4. Upload all four files to the GitHub release.
#
# What this script does:
#   1. Reads the current version from the repo-root VERSION file
#      (override with -Version <x.y.z.w>).
#   2. Runs `dotnet publish` against src\GhostShell.App\GhostShell.App.csproj
#      with Release / win-x64 / self-contained = true. Output lands in
#      publish\desktop\ (gitignored). Skip with -SkipBuild if you just
#      want to re-zip the existing publish folder.
#   3. Zips publish\desktop\ into publish\GhostShell-portable-<ver>.zip.
#   4. Computes SHA-256 and writes the sha256sum-compatible sidecar
#      <archive>.sha256.
#   5. Prints a summary + copies the hash to the clipboard.
#
# IMPORTANT: this file is plain ASCII. Windows PowerShell 5.1 reads
# .ps1 files in the system ANSI codepage unless there's a UTF-8 BOM,
# and non-ASCII characters trip the parser with confusing positional-
# argument errors. Keep new comments / strings ASCII.
#
# Usage from repo root:
#     powershell -ExecutionPolicy Bypass -File tools\package-portable.ps1
#
# Common flags:
#     -SkipBuild             Don't run dotnet publish; just zip whatever
#                            is already in publish\desktop\.
#     -Force                 Overwrite an existing zip without asking.
#     -Version 0.0.2.5       Override the version (otherwise read from
#                            VERSION). Affects the archive filename only;
#                            the .exe FileVersion comes from the build.
#     -Configuration Debug   dotnet publish -c flag (default: Release).

[CmdletBinding()]
param(
    # Version label used in the archive filename. Defaults to the repo's
    # VERSION file. Pass an explicit value to override.
    [string]$Version = '',

    # dotnet publish configuration. Release for shipping, Debug only if
    # you specifically want a debuggable portable for diagnostics.
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # Skip dotnet publish and just zip the existing publish\desktop\.
    [switch]$SkipBuild,

    # Overwrite existing archive without erroring out.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# --- Resolve script anchor + paths -----------------------------------

if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    Write-Error "Script root is empty -- run via 'powershell -File' not piped from stdin."
    exit 1
}
$repoRoot    = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $repoRoot 'src\GhostShell.App\GhostShell.App.csproj'
$publishDir  = Join-Path $repoRoot 'publish\desktop'
$outputDir   = Join-Path $repoRoot 'publish'
$versionFile = Join-Path $repoRoot 'VERSION'

# --- Resolve version -------------------------------------------------

if ([string]::IsNullOrWhiteSpace($Version)) {
    if (-not (Test-Path -LiteralPath $versionFile)) {
        Write-Error ("VERSION file not found: {0}`nPass -Version <x.y.z.w> to override." -f $versionFile)
        exit 1
    }
    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Error "Couldn't determine version. Pass -Version <x.y.z.w>."
    exit 1
}

Write-Host ("Repo root       : {0}" -f $repoRoot)
Write-Host ("Project         : {0}" -f $projectFile)
Write-Host ("Publish dir     : {0}" -f $publishDir)
Write-Host ("Version         : {0}" -f $Version)
Write-Host ("Configuration   : {0}" -f $Configuration)
Write-Host ("Skip build      : {0}" -f $SkipBuild.IsPresent)

# --- Validate project ------------------------------------------------

if (-not (Test-Path -LiteralPath $projectFile)) {
    Write-Error ("Project file not found: {0}" -f $projectFile)
    exit 1
}

# --- Build via dotnet publish ----------------------------------------

if (-not $SkipBuild) {
    # Locate dotnet -- assume it's on PATH. We don't pin a specific SDK
    # path because users may have multiple installs; falling through to
    # PATH lookup matches what `dotnet build` from the README expects.
    $dotnet = Get-Command -Name dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Error "dotnet CLI not on PATH. Install .NET 8 SDK or pass -SkipBuild."
        exit 1
    }

    Write-Host ""
    Write-Host "Running dotnet publish..."
    $buildStart = Get-Date

    # Wipe the previous publish output so stale files (e.g. DLLs from a
    # removed package reference) can't sneak into the zip. dotnet publish
    # itself doesn't always delete files that aren't part of the new
    # output set.
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    & $dotnet.Source publish $projectFile `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -o $publishDir
    $buildExit = $LASTEXITCODE
    if ($buildExit -ne 0) {
        Write-Error ("dotnet publish failed with exit code {0}" -f $buildExit)
        exit $buildExit
    }
    $buildDuration = (Get-Date) - $buildStart
    Write-Host ("  -> publish OK in {0:N1}s" -f $buildDuration.TotalSeconds)
} else {
    Write-Host ""
    Write-Host "Skipping build (per -SkipBuild)."
}

# --- Validate publish output -----------------------------------------

if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    Write-Error ("Publish dir doesn't exist: {0}`nRun without -SkipBuild to publish first." -f $publishDir)
    exit 1
}
$mainExe = Join-Path $publishDir 'GhostShell.exe'
if (-not (Test-Path -LiteralPath $mainExe)) {
    Write-Error ("GhostShell.exe not found in publish dir: {0}" -f $publishDir)
    exit 1
}

# Sanity-check the FileVersion stamped on the produced .exe matches the
# requested version. Mismatch usually means VERSION was bumped without
# running an actual rebuild -- catching that here saves a bad release.
try {
    $exeVer = (Get-Item -LiteralPath $mainExe).VersionInfo.FileVersion
    if (-not [string]::IsNullOrWhiteSpace($exeVer)) {
        $exeVer = $exeVer.Trim()
        if ($exeVer -ne $Version) {
            Write-Warning ("FileVersion of GhostShell.exe is '{0}' but archive will be tagged '{1}'. Did you forget to rebuild after bumping VERSION?" -f $exeVer, $Version)
        }
    }
} catch {
    Write-Verbose ("FileVersion read failed: {0}" -f $_)
}

# --- Prepare archive output ------------------------------------------

if (-not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$archiveName = "GhostShell-portable-$Version.zip"
$archivePath = Join-Path $outputDir $archiveName
$sha256Path  = "$archivePath.sha256"

if (Test-Path -LiteralPath $archivePath) {
    if ($Force) {
        Remove-Item -LiteralPath $archivePath -Force
        if (Test-Path -LiteralPath $sha256Path) {
            Remove-Item -LiteralPath $sha256Path -Force
        }
    } else {
        Write-Error ("Archive already exists: {0}`nUse -Force to overwrite." -f $archivePath)
        exit 1
    }
}

# --- Pack the archive ------------------------------------------------
#
# .NET ZipFile API instead of Compress-Archive: faster, no 2 GB cap,
# deterministic ordering. The portable drop is ~80 MB so size isn't an
# issue, but speed matters when you're cutting frequent releases.

Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host ""
Write-Host "Packing portable archive..."
$packStart = Get-Date

[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir,                                  # source dir
    $archivePath,                                 # output zip
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false                                         # don't include base dir
)

$packDuration = (Get-Date) - $packStart
$archiveSize  = (Get-Item -LiteralPath $archivePath).Length
$archiveSizeMB = [math]::Round($archiveSize / 1MB, 1)
Write-Host ("  -> packed in {0:N1}s ({1} MB)" -f $packDuration.TotalSeconds, $archiveSizeMB)

# --- Compute SHA-256 -------------------------------------------------

Write-Host ""
Write-Host "Computing SHA-256..."
$hashStart = Get-Date
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLower()
$hashDuration = (Get-Date) - $hashStart
Write-Host ("  -> done in {0:N1}s" -f $hashDuration.TotalSeconds)

"$hash  $archiveName" | Set-Content -LiteralPath $sha256Path -Encoding ASCII -NoNewline

# --- Summary ---------------------------------------------------------

Write-Host ""
Write-Host "==========================================================="
Write-Host "  PORTABLE RELEASE ARTIFACTS READY"
Write-Host "==========================================================="
Write-Host ("  Version : {0}" -f $Version)
Write-Host ("  Config  : {0}" -f $Configuration)
Write-Host ("  Size    : {0} MB" -f $archiveSizeMB)
Write-Host ("  SHA-256 : {0}" -f $hash)
Write-Host ""
Write-Host "  Files (upload BOTH to the GitHub release):"
Write-Host ("    {0}" -f $archivePath)
Write-Host ("    {0}" -f $sha256Path)
Write-Host ""
Write-Host "  Verify locally with:"
Write-Host ("    cd `"{0}`"" -f $outputDir)
Write-Host ("    Get-FileHash {0} -Algorithm SHA256" -f $archiveName)
Write-Host "==========================================================="

try {
    $hash | Set-Clipboard -ErrorAction Stop
    Write-Host "  (SHA-256 copied to clipboard.)"
} catch {
    Write-Verbose ("Couldn't copy hash to clipboard: {0}" -f $_)
}
