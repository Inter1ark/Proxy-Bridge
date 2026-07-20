@echo off
cd /d C:\Users\Asus\Desktop\ProxyBridge\Windows
C:\msys64\mingw64\bin\gcc.exe -shared -o ProxyBridgeCore.dll -O2 -DPROXYBRIDGE_EXPORTS src\ProxyBridge.c -IC:\WinDivert-2.2.2-A\include -LC:\WinDivert-2.2.2-A\x64 -lWinDivert -lws2_32 -liphlpapi
echo Build completed with code: %ERRORLEVEL%
pause
