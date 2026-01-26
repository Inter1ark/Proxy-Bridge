@echo off
cd /d C:\Users\Asus\Desktop\ProxyBridge\Windows
echo Compiling ProxyBridgeCore.dll...
C:\msys64\mingw64\bin\gcc.exe -shared -o ProxyBridgeCore.dll -Wall -O2 -DPROXYBRIDGE_EXPORTS src\ProxyBridge.c -IC:\WinDivert-2.2.2-A\include -LC:\WinDivert-2.2.2-A\x64 -lWinDivert -lws2_32 -liphlpapi
echo.
echo Exit code: %ERRORLEVEL%
if exist ProxyBridgeCore.dll (
    echo SUCCESS: DLL created!
    dir ProxyBridgeCore.dll
) else (
    echo FAILED: DLL not created
)
pause
