# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>
#
# Package the patched Chromium build into a release-ready zip + SHA-256
# digest. Output is meant to be uploaded as-is to a GitHub release so
# the desktop installer / self-update path can pull it down + verify.
#
# What the script does:
#   1. Locate the patched Chromium folder (default: ..\ghost_shell_browser\chrome_win64).
#   2. Read the version from chrome.exe's FileVersion (falls back to the
#      lone ".manifest" file in the folder, which Chromium drops there
#      named after the version, e.g. "149.0.7805.0.manifest").
#   3. Create a deterministic-named zip:  ghost_shell_chromium_win64_<ver>.zip
#   4. Compute SHA-256 of the zip and write it next to the archive as
#      <archive>.sha256 (one line: "<hex-hash>  <filename>" -- same format
#      as `sha256sum` so anyone can verify with the standard tool).
#   5. Print a summary table (size, hash, output paths) so you can copy
#      the hash straight into release notes.
#
# IMPORTANT: this file is plain ASCII on purpose. Windows PowerShell 5.1
# reads .ps1 in the system ANSI codepage unless there's a UTF-8 BOM, and
# non-ASCII characters (em-dashes, arrows, box-drawing) trip the parser
# with confusing "positional parameter cannot be found" errors. If you
# add new comments, keep them ASCII.
#
# Usage from repo root:
#     powershell -ExecutionPolicy Bypass -File tools\package-chromium.ps1
#
# Override sources / output path with parameters:
#     powershell -ExecutionPolicy Bypass -File tools\package-chromium.ps1 `
#         -ChromeSource 'D:\my\chrome_win64' `
#         -OutputDir   'D:\releases'        `
#         -Version     '149.0.7805.0'
#
# After running, upload BOTH files to the GitHub release:
#     dist\ghost_shell_chromium_win64_<ver>.zip
#     dist\ghost_shell_chromium_win64_<ver>.zip.sha256

[CmdletBinding()]
param(
    # Folder containing the patched Chromium build (chrome.exe + DLLs +
    # locales\). If empty (default), we look in the sibling browser repo.
    # Pass an absolute path to override.
    [string]$ChromeSource = '',

    # Where to drop the zip + .sha256 sidecar. Created if missing.
    [string]$OutputDir = '',

    # Optional version override. Auto-detected from chrome.exe if blank.
    [string]$Version = '',

    # If set, overwrite an existing zip silently. Otherwise we error out
    # so a stale upload can't be hashed by accident.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# --- Resolve script anchor + defaults --------------------------------
#
# We compute defaults INSIDE the script body (not as param() defaults)
# so $PSScriptRoot is reliably populated and we can use deterministic
# .NET path APIs. Resolve-Path was returning "F:\" for some users when
# fed a path with multiple "..\..\" segments -- avoiding it entirely.

if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    Write-Error "Script root is empty -- run via 'powershell -File' not piped from stdin."
    exit 1
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ([string]::IsNullOrWhiteSpace($ChromeSource)) {
    # Sibling repo: <parent-of-repo>\ghost_shell_browser\chrome_win64.
    $parentOfRepo = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '..'))
    $ChromeSource = Join-Path $parentOfRepo 'ghost_shell_browser\chrome_win64'
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'dist'
}

# Canonicalise both paths through .NET so any trailing relative bits
# (e.g. user passed "..\foo") become absolute and consistent.
$ChromeSource = [System.IO.Path]::GetFullPath($ChromeSource)
$OutputDir    = [System.IO.Path]::GetFullPath($OutputDir)

Write-Host ("Script root     : {0}" -f $PSScriptRoot)
Write-Host ("Repo root       : {0}" -f $repoRoot)
Write-Host ("Chromium source : {0}" -f $ChromeSource)
Write-Host ("Output dir      : {0}" -f $OutputDir)

# --- Validate source -------------------------------------------------

if (-not (Test-Path -LiteralPath $ChromeSource -PathType Container)) {
    Write-Error ("Chromium source folder doesn't exist: {0}`nPass an explicit path with -ChromeSource <path>." -f $ChromeSource)
    exit 1
}

$chromeExe = Join-Path $ChromeSource 'chrome.exe'
if (-not (Test-Path -LiteralPath $chromeExe)) {
    Write-Error ("chrome.exe not found in {0}. Wrong folder?" -f $ChromeSource)
    exit 1
}

# --- Resolve version -------------------------------------------------

if ([string]::IsNullOrWhiteSpace($Version)) {
    # Primary: read FileVersion off chrome.exe -- that's the source of
    # truth Chromium stamps at build time.
    try {
        $fv = (Get-Item -LiteralPath $chromeExe).VersionInfo.FileVersion
        if (-not [string]::IsNullOrWhiteSpace($fv)) { $Version = $fv.Trim() }
    } catch {
        Write-Verbose ("FileVersion lookup failed: {0}" -f $_)
    }
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    # Fallback: locate the ".manifest" file Chromium ships next to chrome.exe;
    # its name IS the version (e.g. "149.0.7805.0.manifest"). Some redists
    # also drop an "elevation_service.manifest" -- exclude anything that
    # doesn't parse as a 4-part dotted version.
    $manifest = Get-ChildItem -LiteralPath $ChromeSource -Filter '*.manifest' |
        Where-Object { $_.BaseName -match '^\d+(\.\d+){2,3}$' } |
        Select-Object -First 1
    if ($manifest) { $Version = $manifest.BaseName }
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Error "Couldn't auto-detect Chromium version. Pass -Version <x.y.z.w>."
    exit 1
}

Write-Host ("Detected version: {0}" -f $Version)

# --- Prepare output --------------------------------------------------

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}
# Already canonicalised at the top of the script via GetFullPath.

$archiveName = "ghost_shell_chromium_win64_$Version.zip"
$archivePath = Join-Path $OutputDir $archiveName
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
# Compress-Archive is fine but maxes out around 2 GB and is slow on
# folders with many small files (locales\ has ~700). We use the
# .NET ZipFile API directly: it's faster, deterministic, and handles
# multi-GB output cleanly.

Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host ""
Write-Host "Packing archive (this can take 30-60s for ~600 MB)..."
$packStart = Get-Date

# CompressionLevel.Optimal trades a bit of CPU for ~5-10 percent smaller
# output. The whole archive is meant to be re-downloaded only on
# Chromium updates, so a smaller artifact is worth the wait.
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $ChromeSource,                                # source dir
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

# Write `.sha256` sidecar in the standard `sha256sum`-compatible format:
#     <hex-hash>  <filename>
# (Two spaces between hash and name, filename only -- not the full path
# -- so `sha256sum -c file.zip.sha256` works when the user has both in
# the same directory.)
"$hash  $archiveName" | Set-Content -LiteralPath $sha256Path -Encoding ASCII -NoNewline

# --- Summary ---------------------------------------------------------

Write-Host ""
Write-Host "==========================================================="
Write-Host "  RELEASE ARTIFACTS READY"
Write-Host "==========================================================="
Write-Host ("  Version : {0}" -f $Version)
Write-Host ("  Size    : {0} MB" -f $archiveSizeMB)
Write-Host ("  SHA-256 : {0}" -f $hash)
Write-Host ""
Write-Host "  Files (upload BOTH to the GitHub release):"
Write-Host ("    {0}" -f $archivePath)
Write-Host ("    {0}" -f $sha256Path)
Write-Host ""
Write-Host "  Verify locally with:"
Write-Host ("    cd `"{0}`"" -f $OutputDir)
Write-Host ("    Get-FileHash {0} -Algorithm SHA256" -f $archiveName)
Write-Host "==========================================================="

# Convenience: copy the hash to the clipboard so it can be pasted
# straight into the release notes. Best-effort -- Set-Clipboard is
# missing on stripped Server Core installs, and the cmdlet may also
# fail under remoting / non-interactive sessions.
try {
    $hash | Set-Clipboard -ErrorAction Stop
    Write-Host "  (SHA-256 copied to clipboard.)"
} catch {
    Write-Verbose ("Couldn't copy hash to clipboard: {0}" -f $_)
}
