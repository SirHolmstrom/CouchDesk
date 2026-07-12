@echo off
setlocal
title CouchDesk quick rebuild and launch
cd /d "%~dp0"

set "PROJECT=src\Core\Core.csproj"
set "EXE=src\Core\bin\Release\net8.0-windows\CouchDesk.exe"
set "PROCESS=CouchDesk.exe"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK not found on PATH.
    pause
    exit /b 1
)

if not exist "%PROJECT%" (
    echo [ERROR] %PROJECT% was not found.
    echo Run this script from the CouchDesk project folder.
    pause
    exit /b 1
)

echo CouchDesk quick rebuild
echo Skipping clean, restore, and icon generation.
echo.

tasklist /FI "IMAGENAME eq %PROCESS%" 2>nul | find /I "%PROCESS%" >nul
if not errorlevel 1 (
    echo Stopping CouchDesk...
    taskkill /IM "%PROCESS%" /T /F >nul 2>nul
    if errorlevel 1 (
        echo [ERROR] Could not stop %PROCESS%.
        echo Close CouchDesk from the tray icon and try again.
        pause
        exit /b 1
    )
)

echo Building changed files...
dotnet build "%PROJECT%" -c Release --no-restore --nologo
if errorlevel 1 goto build_failed

if not exist "%EXE%" (
    echo [ERROR] Build finished, but %EXE% was not found.
    pause
    exit /b 1
)

echo Launching CouchDesk...
start "" "%EXE%"
exit /b 0

:build_failed
echo.
echo [ERROR] Quick build failed.
echo Run rebuild-and-launch.bat once if packages or project dependencies changed.
echo.
pause
exit /b 1
