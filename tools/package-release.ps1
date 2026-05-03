# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>
#
# One-shot wrapper that produces ALL release artifacts:
#   1. Builds + packages the portable distribution
#       (delegates to package-portable.ps1)
#   2. Packages the patched Chromium folder
#       (delegates to package-chromium.ps1)
#
# After running, you'll have FOUR files ready to upload to the GitHub
# release:
#     publish\GhostShell-portable-<ver>.zip
#     publish\GhostShell-portable-<ver>.zip.sha256
#     dist\ghost_shell_chromium_win64_<chrome-ver>.zip
#     dist\ghost_shell_chromium_win64_<chrome-ver>.zip.sha256
#
# Each child script copies its hash to the clipboard at the end, so the
# LAST hash on the clipboard after this script finishes belongs to the
# Chromium archive. The portable hash is also printed to the console
# above so you can copy-paste it from there.
#
# Usage from repo root:
#     powershell -ExecutionPolicy Bypass -File tools\package-release.ps1
#
# Pass-through flags:
#     -SkipBuild        Don't rebuild .NET; just zip the existing
#                       publish\desktop\ folder. Forwarded to
#                       package-portable.ps1.
#     -Force            Overwrite existing archives without asking.
#                       Forwarded to BOTH child scripts.
#     -SkipChromium     Build + package only the portable; don't touch
#                       the Chromium folder. Useful when only the .NET
#                       app changed.
#     -SkipPortable     Skip the .NET portable; only refresh Chromium
#                       artifacts.

[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Force,
    [switch]$SkipChromium,
    [switch]$SkipPortable
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    Write-Error "Script root is empty -- run via 'powershell -File' not piped from stdin."
    exit 1
}

$portableScript = Join-Path $PSScriptRoot 'package-portable.ps1'
$chromiumScript = Join-Path $PSScriptRoot 'package-chromium.ps1'

if (-not $SkipPortable -and -not (Test-Path -LiteralPath $portableScript)) {
    Write-Error ("package-portable.ps1 not found: {0}" -f $portableScript)
    exit 1
}
if (-not $SkipChromium -and -not (Test-Path -LiteralPath $chromiumScript)) {
    Write-Error ("package-chromium.ps1 not found: {0}" -f $chromiumScript)
    exit 1
}

# --- Portable -------------------------------------------------------

if (-not $SkipPortable) {
    Write-Host ""
    Write-Host "###########################################################"
    Write-Host "# 1/2  PORTABLE  (.NET app)"
    Write-Host "###########################################################"

    $portableArgs = @{}
    if ($SkipBuild) { $portableArgs['SkipBuild'] = $true }
    if ($Force)     { $portableArgs['Force']     = $true }

    & $portableScript @portableArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error ("package-portable.ps1 failed with exit code {0}" -f $LASTEXITCODE)
        exit $LASTEXITCODE
    }
} else {
    Write-Host "Skipping portable (per -SkipPortable)."
}

# --- Chromium -------------------------------------------------------

if (-not $SkipChromium) {
    Write-Host ""
    Write-Host "###########################################################"
    Write-Host "# 2/2  CHROMIUM  (patched browser)"
    Write-Host "###########################################################"

    $chromiumArgs = @{}
    if ($Force) { $chromiumArgs['Force'] = $true }

    & $chromiumScript @chromiumArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error ("package-chromium.ps1 failed with exit code {0}" -f $LASTEXITCODE)
        exit $LASTEXITCODE
    }
} else {
    Write-Host "Skipping Chromium (per -SkipChromium)."
}

# --- Done -----------------------------------------------------------

Write-Host ""
Write-Host "==========================================================="
Write-Host "  RELEASE CUT COMPLETE"
Write-Host "==========================================================="
Write-Host "  Look in publish\ and dist\ for the four .zip + .sha256"
Write-Host "  files. Upload them all to the GitHub release."
Write-Host "==========================================================="
