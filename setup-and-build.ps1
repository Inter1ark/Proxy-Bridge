$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "ProxyBridge Build Dependencies Installer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Проверка прав администратора
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "This script requires Administrator privileges" -ForegroundColor Red
    Write-Host "Please run as Administrator" -ForegroundColor Yellow
    pause
    exit 1
}

# Проверяем GCC
Write-Host "Checking for GCC compiler..." -ForegroundColor Yellow
$gccPath = Get-Command gcc -ErrorAction SilentlyContinue
if ($gccPath) {
    Write-Host "✓ GCC found: $($gccPath.Source)" -ForegroundColor Green
    & gcc --version | Select-Object -First 1
} else {
    Write-Host "✗ GCC not found. Installing MinGW-w64..." -ForegroundColor Yellow
    
    # Проверяем winget
    $wingetPath = Get-Command winget -ErrorAction SilentlyContinue
    if ($wingetPath) {
        Write-Host "Installing GCC via winget..." -ForegroundColor Cyan
        winget install -e --id MSYS2.MSYS2 --silent --accept-package-agreements --accept-source-agreements
        
        # Добавляем в PATH
        $mingwPath = "C:\msys64\mingw64\bin"
        if (Test-Path $mingwPath) {
            $env:Path += ";$mingwPath"
            [Environment]::SetEnvironmentVariable("Path", $env:Path + ";$mingwPath", [EnvironmentVariableTarget]::Machine)
            Write-Host "✓ MinGW-w64 installed" -ForegroundColor Green
        }
    } else {
        Write-Host "Downloading MinGW-w64..." -ForegroundColor Cyan
        $mingwUrl = "https://github.com/niXman/mingw-builds-binaries/releases/download/13.2.0-rt_v11-rev1/x86_64-13.2.0-release-posix-seh-msvcrt-rt_v11-rev1.7z"
        $mingwZip = "$env:TEMP\mingw64.7z"
        $mingwDest = "C:\mingw64"
        
        Invoke-WebRequest -Uri $mingwUrl -OutFile $mingwZip -UseBasicParsing
        
        # Распаковываем (требуется 7-Zip)
        if (Test-Path "C:\Program Files\7-Zip\7z.exe") {
            & "C:\Program Files\7-Zip\7z.exe" x $mingwZip "-oC:\" -y
            $env:Path += ";C:\mingw64\bin"
            [Environment]::SetEnvironmentVariable("Path", $env:Path + ";C:\mingw64\bin", [EnvironmentVariableTarget]::Machine)
            Write-Host "✓ MinGW-w64 installed to C:\mingw64" -ForegroundColor Green
        } else {
            Write-Host "ERROR: 7-Zip not found. Please install 7-Zip or use winget" -ForegroundColor Red
            Write-Host "Install 7-Zip: https://www.7-zip.org/" -ForegroundColor Yellow
            pause
            exit 1
        }
    }
}

Write-Host ""
Write-Host "Checking for WinDivert..." -ForegroundColor Yellow
$windivertPath = "C:\WinDivert-2.2.2-A"
if (Test-Path $windivertPath) {
    Write-Host "✓ WinDivert found at $windivertPath" -ForegroundColor Green
} else {
    Write-Host "✗ WinDivert not found. Downloading..." -ForegroundColor Yellow
    
    $windivertUrl = "https://github.com/basil00/Divert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip"
    $windivertZip = "$env:TEMP\WinDivert-2.2.2-A.zip"
    
    Write-Host "Downloading WinDivert 2.2.2-A..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $windivertUrl -OutFile $windivertZip -UseBasicParsing
    
    Write-Host "Extracting WinDivert..." -ForegroundColor Cyan
    Expand-Archive -Path $windivertZip -DestinationPath "C:\" -Force
    
    if (Test-Path $windivertPath) {
        Write-Host "✓ WinDivert installed to $windivertPath" -ForegroundColor Green
    } else {
        Write-Host "ERROR: WinDivert installation failed" -ForegroundColor Red
        pause
        exit 1
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "All dependencies installed successfully!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Press any key to continue with compilation..." -ForegroundColor Yellow
pause

# Компиляция DLL
Write-Host ""
Write-Host "Compiling ProxyBridgeCore.dll..." -ForegroundColor Cyan
Set-Location "$PSScriptRoot"

$compileCmd = "gcc -shared -O2 -Wall -D_WIN32_WINNT=0x0601 -I`"$windivertPath\include`" src\ProxyBridge.c -L`"$windivertPath\x64`" -lWinDivert -lws2_32 -liphlpapi -o ProxyBridgeCore.dll"
Write-Host $compileCmd -ForegroundColor DarkGray
Invoke-Expression $compileCmd

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ ProxyBridgeCore.dll compiled successfully" -ForegroundColor Green
    
    # Копируем файлы
    Write-Host ""
    Write-Host "Copying files to GUI project..." -ForegroundColor Cyan
    $guiPath = "gui\bin\Debug\net9.0-windows"
    
    if (-not (Test-Path $guiPath)) {
        New-Item -ItemType Directory -Path $guiPath -Force | Out-Null
    }
    
    Copy-Item "ProxyBridgeCore.dll" -Destination $guiPath -Force
    Copy-Item "$windivertPath\x64\WinDivert.dll" -Destination $guiPath -Force
    Copy-Item "$windivertPath\x64\WinDivert64.sys" -Destination $guiPath -Force
    
    Write-Host "✓ Files copied to $guiPath" -ForegroundColor Green
    
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Build completed successfully!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
} else {
    Write-Host "✗ Compilation failed" -ForegroundColor Red
    pause
    exit 1
}

Write-Host ""
Write-Host "You can now run the application!" -ForegroundColor Cyan
pause
