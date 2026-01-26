@echo off
cd /d C:\Users\Asus\Desktop\ProxyBridge\Windows
echo Compiling to object file...
C:\msys64\mingw64\bin\gcc.exe -c -Wall -O2 -DPROXYBRIDGE_EXPORTS src\ProxyBridge.c -IC:\WinDivert-2.2.2-A\include -o ProxyBridge.o > compile_output.txt 2>&1
echo Exit code: %ERRORLEVEL%
echo.
echo === Output from GCC ===
type compile_output.txt
echo.
if exist ProxyBridge.o (
    echo Object file created successfully
    dir ProxyBridge.o
) else (
    echo Object file NOT created
)
pause
