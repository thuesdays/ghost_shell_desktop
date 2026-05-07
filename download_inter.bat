@echo off
REM ════════════════════════════════════════════════════════════════
REM  download_inter.bat
REM
REM  Pulls the latest Inter variable font from rsms/inter on GitHub
REM  Releases and unpacks the two .ttf masters (upright + italic)
REM  into F:\projects\inter_font\, where the GhostShell.App.csproj's
REM  CopyInterFont MSBuild target picks them up at build time.
REM
REM  Run once after cloning the project (or whenever you want to
REM  refresh to a newer Inter release). Subsequent `dotnet build`
REM  invocations will then bake the .ttfs into the assembly so the
REM  app ships with proper Inter rendering on every machine.
REM
REM  Override INTER_VERSION / INTER_DEST via env if you want a
REM  specific release / destination folder.
REM ════════════════════════════════════════════════════════════════

setlocal

if "%INTER_VERSION%"=="" set INTER_VERSION=4.1
if "%INTER_DEST%"==""    set INTER_DEST=F:\projects\inter_font

set ARCHIVE=Inter-%INTER_VERSION%.zip
set URL=https://github.com/rsms/inter/releases/download/v%INTER_VERSION%/%ARCHIVE%
set TMP_DIR=%TEMP%\inter_dl_%RANDOM%

echo === download_inter.bat ===
echo  Version : %INTER_VERSION%
echo  Source  : %URL%
echo  Dest    : %INTER_DEST%
echo.

if not exist "%INTER_DEST%" (
    echo Creating %INTER_DEST% ...
    mkdir "%INTER_DEST%"
)

mkdir "%TMP_DIR%" 2>nul

echo Downloading %ARCHIVE% ...
where curl.exe >nul 2>&1
if %ERRORLEVEL%==0 (
    curl -L -o "%TMP_DIR%\%ARCHIVE%" "%URL%"
) else (
    powershell -NoProfile -Command "Invoke-WebRequest -Uri '%URL%' -OutFile '%TMP_DIR%\%ARCHIVE%'"
)

if not exist "%TMP_DIR%\%ARCHIVE%" (
    echo Download failed.
    rmdir /S /Q "%TMP_DIR%" 2>nul
    exit /b 1
)

echo Unpacking ...
powershell -NoProfile -Command "Expand-Archive -Force -Path '%TMP_DIR%\%ARCHIVE%' -DestinationPath '%TMP_DIR%\unpacked'"

REM Look for the variable masters under any subfolder inside the zip.
REM Inter releases ship them at  Inter-X.Y/extras/ttf/InterVariable.ttf
REM and Inter-X.Y/extras/ttf/InterVariable-Italic.ttf, but the layout
REM has historically moved between releases; we find them by name.
echo Locating variable masters ...
set FOUND_UPRIGHT=
set FOUND_ITALIC=
for /R "%TMP_DIR%\unpacked" %%F in (InterVariable.ttf Inter-VariableFont*.ttf) do (
    if /I not "%%~nxF"=="InterVariable-Italic.ttf" (
        if not defined FOUND_UPRIGHT set FOUND_UPRIGHT=%%F
    )
)
for /R "%TMP_DIR%\unpacked" %%F in (InterVariable-Italic.ttf Inter-Italic-VariableFont*.ttf) do (
    if not defined FOUND_ITALIC set FOUND_ITALIC=%%F
)

if defined FOUND_UPRIGHT (
    echo  Upright: %FOUND_UPRIGHT%
    copy /Y "%FOUND_UPRIGHT%" "%INTER_DEST%\Inter-VariableFont_opsz,wght.ttf" >nul
) else (
    echo WARN: Couldn't find the upright variable master inside the archive.
)

if defined FOUND_ITALIC (
    echo  Italic : %FOUND_ITALIC%
    copy /Y "%FOUND_ITALIC%" "%INTER_DEST%\Inter-Italic-VariableFont_opsz,wght.ttf" >nul
) else (
    echo WARN: Couldn't find the italic variable master inside the archive.
)

echo Cleaning up ...
rmdir /S /Q "%TMP_DIR%" 2>nul

echo.
echo === Done ===
echo  Drop into:    %INTER_DEST%
echo  Next build of GhostShell.App will bake these into the assembly via
echo  the CopyInterFont MSBuild target. Check the run-time logs for
echo  "FontDiagnostics" entries — '✓ Inter is embedded and loadable'
echo  confirms the wiring works.
echo.

endlocal
