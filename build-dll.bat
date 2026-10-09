@echo off
echo ========================================
echo Building ProxyBridgeCore.dll
echo ========================================
echo.
REM Usage: build-dll.bat          build ProxyBridgeCore.dll and copy it to the GUI debug folder
REM        build-dll.bat tests    build and run the core unit tests (output\tests\core_tests.exe)

cd /d "%~dp0"

REM Core sources (see docs\CORE_NOTES.md)
set CORE_SRC=src\ProxyBridge.c src\pb_compat.c src\pb_conntrack.c src\pb_dns.c src\pb_http.c src\pb_process.c src\pb_proxy.c src\pb_relay.c src\pb_rules.c src\pb_socks5.c src\pb_util.c
set CORE_LIBS=-lWinDivert -lws2_32 -liphlpapi -lpsapi

REM Проверяем наличие GCC
where gcc >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Found GCC compiler
    goto :compile_gcc
)
if exist "C:\msys64\mingw64\bin\gcc.exe" (
    set "PATH=C:\msys64\mingw64\bin;%PATH%"
    echo Found GCC compiler in C:\msys64\mingw64\bin
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

if /I "%~1"=="tests" goto :tests

gcc -shared -O2 -Wall -D_WIN32_WINNT=0x0601 -DPROXYBRIDGE_EXPORTS ^
    -I"%WINDIVERT_PATH%\include" ^
    %CORE_SRC% ^
    -L"%WINDIVERT_PATH%\x64" ^
    %CORE_LIBS% ^
    -o ProxyBridgeCore.dll

if %ERRORLEVEL% NEQ 0 (
    echo Compilation failed!
    pause
    exit /b 1
)

goto :copy_dll

:tests
if not exist output\tests mkdir output\tests
gcc -O2 -Wall -D_WIN32_WINNT=0x0601 -DPROXYBRIDGE_EXPORTS ^
    -I"%WINDIVERT_PATH%\include" -Isrc ^
    src\tests\core_tests.c %CORE_SRC% ^
    -L"%WINDIVERT_PATH%\x64" ^
    %CORE_LIBS% ^
    -o output\tests\core_tests.exe
if %ERRORLEVEL% NEQ 0 (
    echo Test build failed!
    exit /b 1
)
copy /Y "%WINDIVERT_PATH%\x64\WinDivert.dll" output\tests\ >nul
REM The tests never start packet interception and need no administrator rights.
output\tests\core_tests.exe
exit /b %ERRORLEVEL%

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
