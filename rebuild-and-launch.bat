@echo off
setlocal
title CouchDesk rebuild and launch
cd /d "%~dp0"

set "SOLUTION=CouchDesk.sln"
set "EXE=src\Core\bin\Release\net8.0-windows\CouchDesk.exe"
set "PROCESS=CouchDesk.exe"
set "ICON_PROJECT=tools\IconBuilder\IconBuilder.csproj"
set "ICON_DLL=tools\IconBuilder\bin\Release\net8.0\IconBuilder.dll"
set "GRAPHICS=graphics"
set "ASSETS=src\Core\Assets"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK not found on PATH.
    echo Install the .NET 8 SDK from:
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

if not exist "%SOLUTION%" (
    echo [ERROR] %SOLUTION% was not found.
    echo Run this script from the CouchDesk project folder.
    echo.
    pause
    exit /b 1
)

echo ============================================================
echo  CouchDesk - rebuild and launch
echo ============================================================
echo.

tasklist /FI "IMAGENAME eq %PROCESS%" 2>nul | find /I "%PROCESS%" >nul
if not errorlevel 1 (
    echo CouchDesk is already running. The exe must be closed before rebuilding.
    choice /M "Stop the running CouchDesk now"
    if errorlevel 2 (
        echo.
        echo Rebuild cancelled.
        pause
        exit /b 1
    )

    echo.
    echo Stopping CouchDesk...
    taskkill /IM "%PROCESS%" /T /F >nul 2>nul
    if errorlevel 1 (
        echo [ERROR] Could not stop %PROCESS%.
        echo Close CouchDesk from the tray icon and run this script again.
        echo.
        pause
        exit /b 1
    )
    timeout /T 1 /NOBREAK >nul
)

echo Building icon generator...
dotnet build "%ICON_PROJECT%" -c Release --nologo
if errorlevel 1 goto icon_failed

echo.
echo Refreshing icons from %GRAPHICS%...
dotnet "%ICON_DLL%" "%ASSETS%\app.ico" ^
    16="%GRAPHICS%\app-icon-16.png" ^
    24="%GRAPHICS%\app-icon-24.png" ^
    32="%GRAPHICS%\app-icon-32.png" ^
    48="%GRAPHICS%\app-icon-48.png" ^
    64="%GRAPHICS%\app-icon-64.png" ^
    128="%GRAPHICS%\app-icon-128.png" ^
    256="%GRAPHICS%\app-icon-256.png"
if errorlevel 1 goto icon_failed

dotnet "%ICON_DLL%" "%ASSETS%\tray-idle-white.ico" ^
    16="%GRAPHICS%\tray-idle-white-16.png" ^
    20="%GRAPHICS%\tray-idle-white-20.png" ^
    24="%GRAPHICS%\tray-idle-white-24.png" ^
    32="%GRAPHICS%\tray-idle-white-32.png"
if errorlevel 1 goto icon_failed

dotnet "%ICON_DLL%" "%ASSETS%\tray-idle-black.ico" ^
    16="%GRAPHICS%\tray-idle-black-16.png" ^
    20="%GRAPHICS%\tray-idle-black-20.png" ^
    24="%GRAPHICS%\tray-idle-black-24.png" ^
    32="%GRAPHICS%\tray-idle-black-32.png"
if errorlevel 1 goto icon_failed

dotnet "%ICON_DLL%" "%ASSETS%\tray-on-white.ico" ^
    16="%GRAPHICS%\tray-on-white-16.png" ^
    20="%GRAPHICS%\tray-on-white-20.png" ^
    24="%GRAPHICS%\tray-on-white-24.png" ^
    32="%GRAPHICS%\tray-on-white-32.png"
if errorlevel 1 goto icon_failed

dotnet "%ICON_DLL%" "%ASSETS%\tray-on-black.ico" ^
    16="%GRAPHICS%\tray-on-black-16.png" ^
    20="%GRAPHICS%\tray-on-black-20.png" ^
    24="%GRAPHICS%\tray-on-black-24.png" ^
    32="%GRAPHICS%\tray-on-black-32.png"
if errorlevel 1 goto icon_failed

dotnet "%ICON_DLL%" "src\Core\web\favicon.ico" ^
    16="%GRAPHICS%\app-icon-16.png" ^
    32="%GRAPHICS%\app-icon-32.png" ^
    48="%GRAPHICS%\app-icon-48.png"
if errorlevel 1 goto icon_failed

echo Cleaning Release build...
dotnet clean "%SOLUTION%" -c Release
if errorlevel 1 goto build_failed

echo.
echo Building Release...
dotnet build "%SOLUTION%" -c Release
if errorlevel 1 goto build_failed

if not exist "%EXE%" (
    echo [ERROR] Build finished, but %EXE% was not found.
    echo.
    pause
    exit /b 1
)

echo.
echo Launching CouchDesk...
start "" "%EXE%"
echo Done.
echo.
exit /b 0

:build_failed
echo.
echo [ERROR] Build failed.
echo.
pause
exit /b 1

:icon_failed
echo.
echo [ERROR] The icons could not be regenerated from the PNG files in %GRAPHICS%.
echo.
pause
exit /b 1
