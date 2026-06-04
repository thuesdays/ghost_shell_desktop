# ================================================================
#  package_chromium.ps1 -- pack chrome_win64\ into a release-ready
#  zip + SHA256 sidecar for upload as a GitHub release asset.
#
#  Why this script exists
#  ----------------------
#  chrome_win64\ is ~600 MB uncompressed and lives outside git
#  (.gitignore'd; chrome.dll alone is 383 MB, way past GitHub's
#  100 MB per-file in-tree limit). Distribution path is:
#
#      developer  ->  package_chromium.ps1  ->  dist\chrome_win64-vX.Y.Z.W.zip
#                                          ->  dist\chrome_win64-vX.Y.Z.W.zip.sha256
#                                          ->  manual upload to a GitHub Release as an asset
#                                          ->  CI / fellow devs run download_chromium.ps1
#
#  The .sha256 sidecar lets download_chromium.ps1 verify integrity
#  before extraction -- important because release assets do get
#  occasionally corrupted in transit, and a half-extracted Chromium
#  fails at startup with cryptic side-by-side errors.
#
#  Why System.IO.Compression and not Compress-Archive
#  --------------------------------------------------
#  PowerShell 5's Compress-Archive uses ZipFile under the hood but
#  silently truncates entries > 2 GB and chokes on chrome.dll-sized
#  files on some hosts. System.IO.Compression.ZipFile via the
#  ZIP64-aware overloads handles big single files cleanly.
#
#  Usage:
#    .\scripts\package_chromium.ps1
#    .\scripts\package_chromium.ps1 -Version "0.2.0.3"
#    .\scripts\package_chromium.ps1 -Version "0.2.0.3" -ChromeVersion "149.0.7805.0"
#    .\scripts\package_chromium.ps1 -SourceDir "F:\chromium\out\GhostShell"
#
#  Versions
#  --------
#  -Version        Ghost Shell release version (e.g. 0.2.0.5). Drives
#                  the zip filename. If not passed, auto-resolved from
#                  installer/.iss + installer/.build_number; if those
#                  also fail, prompted for interactively.
#
#  -ChromeVersion  Chromium build version (e.g. 149.0.7805.0). Embedded
#                  into the zip filename so devs can see at a glance
#                  which Chromium was packaged. If not passed, auto-
#                  detected from chrome.exe's file-version metadata.
#                  Pass "auto" or "" to force auto-detection.
# ================================================================

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$ChromeVersion = "",
    [string]$SourceDir = "",
    [string]$OutDir = "",
    [switch]$NonInteractive   # CI: skip prompts, fail instead
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to repo root (parent of scripts/)
$here     = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $here

if (-not $SourceDir) { $SourceDir = Join-Path $repoRoot "chrome_win64" }
if (-not $OutDir)    { $OutDir    = Join-Path $repoRoot "dist" }

# Auto-resolve release version from installer's build number + .iss
# constants if the caller did not pass one. Falls back to "0.0.0.0"
# when neither source is present, then prompts interactively unless
# -NonInteractive (CI) is set.
function Resolve-Version-FromInstaller {
    $issPath  = Join-Path $repoRoot "installer\ghost_shell_installer.iss"
    $bnPath   = Join-Path $repoRoot "installer\.build_number"
    $major = "0"; $minor = "0"; $patch = "0"; $build = "0"
    if (Test-Path $issPath) {
        $iss = Get-Content -Raw -Path $issPath
        if ($iss -match '#define\s+AppVersionMajor\s+"(\d+)"') { $major = $Matches[1] }
        if ($iss -match '#define\s+AppVersionMinor\s+"(\d+)"') { $minor = $Matches[1] }
        if ($iss -match '#define\s+AppVersionPatch\s+"(\d+)"') { $patch = $Matches[1] }
    }
    if (Test-Path $bnPath) {
        $raw = (Get-Content -Raw -Path $bnPath) -replace '[^0-9]', ''
        if ($raw) { $build = $raw.Trim() }
    }
    return "$major.$minor.$patch.$build"
}

if (-not $Version) {
    $autoVer = Resolve-Version-FromInstaller
    if ($autoVer -ne "0.0.0.0") {
        $Version = $autoVer
        Write-Host "[package] auto-resolved release version: $Version" `
                   -ForegroundColor DarkGray
    } elseif (-not $NonInteractive) {
        # Last-resort interactive prompt — common when packaging from
        # a fresh clone where the build_number file isn't initialised
        # yet. Validate the entered value at least matches X.Y.Z.W.
        Write-Host ""
        Write-Host "Could not auto-detect Ghost Shell release version."
        Write-Host "Expected format: X.Y.Z.W (e.g. 0.2.0.5)" -ForegroundColor DarkGray
        do {
            $entered = Read-Host "[package] Enter release version"
            $entered = $entered.Trim()
        } while (-not ($entered -match '^\d+\.\d+\.\d+\.\d+$'))
        $Version = $entered
    } else {
        Write-Error "[package] -Version not given and auto-detect failed; -NonInteractive set"
        exit 1
    }
}

if (-not (Test-Path $SourceDir)) {
    Write-Error "[package] source not found: $SourceDir"
    exit 1
}

# Auto-detect Chromium build version from chrome.exe metadata.
# Triggered when -ChromeVersion is not provided OR is "auto"/"detect".
# We probe chrome.exe's PE FileVersion field — that's the same value
# you see when right-click -> Properties -> Details on the file.
function Get-ChromeVersionFromBinary($binDir) {
    $exe = Join-Path $binDir "chrome.exe"
    if (-not (Test-Path $exe)) { return $null }
    try {
        $info = (Get-Item $exe).VersionInfo
        $v = $info.ProductVersion
        if (-not $v) { $v = $info.FileVersion }
        if ($v) { return $v.Trim() }
    } catch {
        return $null
    }
    return $null
}

if (-not $ChromeVersion -or
    $ChromeVersion -eq "auto" -or $ChromeVersion -eq "detect") {
    $detected = Get-ChromeVersionFromBinary $SourceDir
    if ($detected) {
        $ChromeVersion = $detected
        Write-Host "[package] auto-detected Chromium version from chrome.exe: $ChromeVersion" `
                   -ForegroundColor DarkGray
    } elseif (-not $NonInteractive) {
        Write-Host ""
        Write-Host "Could not auto-detect Chromium build version from chrome.exe."
        Write-Host "Expected format: X.Y.Z.W (e.g. 149.0.7805.0)" -ForegroundColor DarkGray
        Write-Host "Press Enter to skip embedding it in the zip name." -ForegroundColor DarkGray
        $entered = (Read-Host "[package] Enter Chromium version (or blank to skip)").Trim()
        if ($entered) { $ChromeVersion = $entered }
    }
}

# Sanity-check it really IS a Chromium build dir, not a half-empty
# leftover from a failed sync. Without chrome.exe + chrome.dll +
# resources.pak the zip would ship a non-functional package.
$required = @("chrome.exe", "chrome.dll", "resources.pak")
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $SourceDir $f))) {
        Write-Error "[package] $f missing in $SourceDir - refusing to package incomplete dir"
        exit 1
    }
}

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

# Filename embeds release version; if a Chromium build version is
# known (auto-detected or explicitly passed) it gets embedded too,
# producing names like:
#   chrome_win64-v0.2.0.5-chromium-149.0.7805.0.zip
# When ChromeVersion is empty (skipped at prompt), keep the legacy
# layout for backwards-compat with existing release-asset URLs.
if ($ChromeVersion) {
    $zipName = "chrome_win64-v$Version-chromium-$ChromeVersion.zip"
} else {
    $zipName = "chrome_win64-v$Version.zip"
}
$zipPath    = Join-Path $OutDir $zipName
$shaPath    = "$zipPath.sha256"

# Wipe any previous zip with the same name -- ZipFile.CreateFromDirectory
# refuses to write over an existing file.
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (Test-Path $shaPath) { Remove-Item $shaPath -Force }

Write-Host "[package] source:    $SourceDir"
Write-Host "[package] target:    $zipPath"
Write-Host "[package] release:   $Version"
if ($ChromeVersion) {
    Write-Host "[package] chromium:  $ChromeVersion"
} else {
    Write-Host "[package] chromium:  (not embedded — pass -ChromeVersion to include)" `
               -ForegroundColor DarkGray
}
Write-Host "[package] zipping... (takes ~30-60s for ~600 MB at Optimal level)"

Add-Type -AssemblyName System.IO.Compression.FileSystem

# Optimal compression. Chromium binaries do not compress hard
# (already-optimized bytes), so ~50% reduction at most. Optimal vs
# Fastest is a marginal time trade for ~5-10% smaller asset.
$compression = [System.IO.Compression.CompressionLevel]::Optimal
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $SourceDir,
    $zipPath,
    $compression,
    $false
)

$zipBytes = (Get-Item $zipPath).Length
Write-Host ("[package] zipped: {0:N0} bytes ({1:N1} MB)" -f $zipBytes, ($zipBytes / 1MB))

# SHA256 sidecar -- formatted "<HEX_HASH>  <filename>" to mimic the
# layout produced by `sha256sum` so users can verify with the same
# tool on Linux/macOS:
#     sha256sum -c chrome_win64-v0.2.0.3.zip.sha256
Write-Host "[package] hashing..."
$sha = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLower()
"$sha  $zipName" | Set-Content -Path $shaPath -Encoding ASCII -NoNewline

Write-Host "[package] sha256:  $sha"
Write-Host "[package] sidecar: $shaPath"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Open https://github.com/thuesdays/ghost_shell_browser/releases/edit/v$Version"
Write-Host "     (or create a new release with tag v$Version)"
Write-Host "  2. Drag-and-drop both files into the 'Attach binaries' area:"
Write-Host "       $zipPath"
Write-Host "       $shaPath"
Write-Host "  3. Publish."
Write-Host ""
Write-Host "After release is live, fellow devs can fetch chrome_win64\ via:"
Write-Host "  .\scripts\download_chromium.ps1"
exit 0
