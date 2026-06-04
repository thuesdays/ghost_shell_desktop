@echo off
REM ============================================================
REM  package_chromium.bat — wrapper for package_chromium.ps1
REM
REM  Why this wrapper exists:
REM    Windows PowerShell's default execution policy refuses to
REM    run .ps1 files directly. Invoking the .ps1 via this .bat
REM    with -ExecutionPolicy Bypass works without altering any
REM    machine-wide settings.
REM
REM  Usage — three call shapes are accepted:
REM
REM    1. Auto-detect everything (release ver from installer .iss,
REM       Chromium build ver from chrome.exe metadata):
REM         .\scripts\package_chromium.bat
REM
REM    2. Positional shortcut for release version (most common):
REM         .\scripts\package_chromium.bat 0.2.0.5
REM         .\scripts\package_chromium.bat 0.2.0.5 149.0.7805.0
REM       First arg = ghost-shell release version (-Version)
REM       Second arg (optional) = Chromium build version
REM       (-ChromeVersion). Embedded into the zip filename so
REM       fellow devs can see at a glance which Chromium was
REM       packaged. Pass "auto" to keep auto-detection from
REM       chrome.exe metadata.
REM
REM    3. Named flags for full control / non-default paths:
REM         .\scripts\package_chromium.bat -Version 0.2.0.5
REM         .\scripts\package_chromium.bat -Version 0.2.0.5 ^
REM                                        -ChromeVersion 149.0.7805.0
REM         .\scripts\package_chromium.bat -SourceDir "F:\out\GhostShell"
REM
REM    4. Interactive (no args at all): the .ps1 will prompt for
REM       both versions if it can't auto-detect either.
REM ============================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0\.."

REM Translate positional shortcuts into named flags BEFORE handing
REM off to the .ps1. We only do this when the FIRST argument doesn't
REM already start with a dash — that's how we know the caller used
REM the friendly shortcut form, not the explicit -Foo Bar form.
set "ARGS=%*"
if "%~1"=="" goto :run
set "FIRST=%~1"
if "!FIRST:~0,1!"=="-" goto :run

REM Positional: %1 = release version, %2 (optional) = chrome version
set "ARGS=-Version %~1"
if not "%~2"=="" if not "%~2"=="auto" set "ARGS=!ARGS! -ChromeVersion %~2"

:run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "scripts\package_chromium.ps1" %ARGS%
exit /b %errorlevel%
