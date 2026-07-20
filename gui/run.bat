@echo off
cd /d "%~dp0"
echo Building ProxyBridge...
dotnet build
if %ERRORLEVEL% NEQ 0 (
    echo Build failed!
    pause
    exit /b 1
)
echo.
echo Starting ProxyBridge...
start "" "bin\Debug\net9.0-windows\ProxyBridge.exe"

