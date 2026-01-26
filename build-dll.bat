@echo off
echo ========================================
echo Building ProxyBridgeCore.dll
echo ========================================
echo.

cd /d "%~dp0"

REM Проверяем наличие GCC
where gcc >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Found GCC compiler
    goto :compile_gcc
)

REM Проверяем наличие MSVC
where cl >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Found MSVC compiler
    goto :compile_msvc
)

echo ERROR: No compiler found!
echo Please install MinGW-w64 GCC or Visual Studio
echo.
echo Install MinGW-w64: https://www.mingw-w64.org/downloads/
pause
exit /b 1

:compile_gcc
echo Compiling with GCC...
set WINDIVERT_PATH=C:\WinDivert-2.2.2-A

if not exist "%WINDIVERT_PATH%" (
    echo ERROR: WinDivert not found at %WINDIVERT_PATH%
    echo Please download from https://reqrypt.org/windivert.html
    pause
    exit /b 1
)

gcc -shared -O2 -Wall -D_WIN32_WINNT=0x0601 ^
    -I"%WINDIVERT_PATH%\include" ^
    src\ProxyBridge.c ^
    -L"%WINDIVERT_PATH%\x64" ^
    -lWinDivert -lws2_32 -liphlpapi ^
    -o ProxyBridgeCore.dll

if %ERRORLEVEL% NEQ 0 (
    echo Compilation failed!
    pause
    exit /b 1
)

goto :copy_dll

:compile_msvc
echo ERROR: MSVC compilation not implemented yet
echo Please use GCC or run compile.ps1
pause
exit /b 1

:copy_dll
echo.
echo Copying DLL to GUI project...
copy /Y ProxyBridgeCore.dll gui\bin\Debug\net9.0-windows\
copy /Y "%WINDIVERT_PATH%\x64\WinDivert.dll" gui\bin\Debug\net9.0-windows\
copy /Y "%WINDIVERT_PATH%\x64\WinDivert64.sys" gui\bin\Debug\net9.0-windows\

echo.
echo ========================================
echo Build completed successfully!
echo ========================================
pause
