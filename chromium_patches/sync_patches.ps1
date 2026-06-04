# SPDX-License-Identifier: MIT
# Regenerate the vendored Chromium stealth patches from the live checkout.
# Keeps F:\projects\ghost_shell_desktop\chromium_patches\ in sync with the
# source of truth (the working tree at F:\projects\chromium\src).
#
# Run after ANY change to the patched Chromium sources:
#     pwsh -File chromium_patches\sync_patches.ps1
#
# What it does:
#   1. Reads the Chromium version from chrome\VERSION.
#   2. Writes ghost-shell-<version>.patch = git diff of all modified
#      source files (binary rebranding assets excluded).
#   3. Re-copies every NEW (untracked) ghost_shell_* core file into
#      new_files\ at its target path.
#   4. Prints the branding-asset changes (not captured by the text patch).

param(
    [string]$ChromiumSrc = 'F:\projects\chromium\src',
    [string]$Dest        = (Join-Path $PSScriptRoot '.')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $ChromiumSrc 'BUILD.gn'))) {
    throw "Not a Chromium tree: $ChromiumSrc"
}

# 1) Version
$verFile = Join-Path $ChromiumSrc 'chrome\VERSION'
$kv = @{}
Get-Content $verFile | ForEach-Object { if ($_ -match '^(\w+)=(.+)$') { $kv[$Matches[1]] = $Matches[2] } }
$ver = "$($kv.MAJOR).$($kv.MINOR).$($kv.BUILD).$($kv.PATCH)"
Write-Host "Chromium version: $ver"

# 2) Code patch (exclude binary assets)
$patch = Join-Path $Dest "ghost-shell-$ver.patch"
Push-Location $ChromiumSrc
try {
    # stderr captured for us; use --no-color for a stable, reviewable diff
    git diff --no-color -- ':!*.png' ':!*.ico' | Out-File -FilePath $patch -Encoding utf8
    $lines = (Get-Content $patch | Measure-Object -Line).Lines
    Write-Host "Wrote $patch ($lines lines)"

    # 3) New (untracked) ghost_shell core files
    $newDir = Join-Path $Dest 'new_files'
    $untracked = git ls-files --others --exclude-standard | Where-Object { $_ -match 'ghost_shell_(config|ua_override)\.(h|cc)$' }
    foreach ($rel in $untracked) {
        $target = Join-Path $newDir ($rel -replace '/', '\')
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -Force (Join-Path $ChromiumSrc $rel) $target
        Write-Host "  new_files\$rel"
    }

    # 4) Branding assets (informational)
    Write-Host "`nBranding/binary changes (re-apply by copying assets, not via the patch):"
    git diff --stat -- '*.png' '*.ico' | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" }
}
finally { Pop-Location }

Write-Host "`nDone. Review + commit chromium_patches\ to keep the repo current."
