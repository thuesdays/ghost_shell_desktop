# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>
#
# One-shot helper: copies the Inter font binary into the App project's
# embedded-resource folder so `dotnet build` can pick it up. Run once
# after pulling the repo (or after dropping a fresh font folder).
#
# Usage from repo root:
#     powershell -ExecutionPolicy Bypass -File tools\copy-fonts.ps1
#
# Source path is fixed to F:\projects\inter_font\ — the developer's
# local font drop. Override with -Source if you have it elsewhere.

[CmdletBinding()]
param(
    [string]$Source = 'F:\projects\inter_font',
    [string]$Dest   = "$PSScriptRoot\..\src\GhostShell.App\Assets\Fonts"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) {
    Write-Error "Source font folder not found: $Source"
    exit 1
}

# Make sure the destination exists. We don't `git rm` the directory
# because the .gitkeep is the marker that the build expects this
# folder.
New-Item -ItemType Directory -Force -Path $Dest | Out-Null

# We embed the variable-font master only — one .ttf carries every
# weight + italic axis, so the binary stays compact (~700 KB) and we
# don't ship 15 static cuts.
$files = @(
    @{ Src = 'Inter-VariableFont_opsz,wght.ttf';        Dst = 'Inter-Variable.ttf' },
    @{ Src = 'Inter-Italic-VariableFont_opsz,wght.ttf'; Dst = 'Inter-Italic-Variable.ttf' }
)

foreach ($f in $files) {
    $src = Join-Path $Source $f.Src
    $dst = Join-Path $Dest   $f.Dst
    if (-not (Test-Path $src)) {
        Write-Warning "  · skip (missing): $src"
        continue
    }
    Copy-Item -Path $src -Destination $dst -Force
    Write-Host "  + $($f.Dst)"  -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Run 'dotnet build GhostShell.sln' next." -ForegroundColor Cyan
