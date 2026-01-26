# Build ProxyBridge Installer
# Автоматическая сборка установщика ProxyBridge

Write-Host "=== ProxyBridge Installer Builder ===" -ForegroundColor Cyan
Write-Host ""

# Проверка NSIS
$nsisPath = "C:\Program Files (x86)\NSIS\makensis.exe"
if (-not (Test-Path $nsisPath)) {
    Write-Host "ERROR: NSIS not found at $nsisPath" -ForegroundColor Red
    Write-Host "Please install NSIS from https://nsis.sourceforge.io/Download" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Downloading NSIS installer..." -ForegroundColor Yellow
    $nsisUrl = "https://sourceforge.net/projects/nsis/files/latest/download"
    $nsisInstaller = "$env:TEMP\nsis-installer.exe"
    
    try {
        Invoke-WebRequest -Uri $nsisUrl -OutFile $nsisInstaller -UseBasicParsing
        Write-Host "Starting NSIS installation..." -ForegroundColor Green
        Start-Process -FilePath $nsisInstaller -Wait
        Write-Host "NSIS installed! Please run this script again." -ForegroundColor Green
    } catch {
        Write-Host "Failed to download NSIS. Please install manually." -ForegroundColor Red
    }
    exit 1
}

Write-Host "✓ NSIS found" -ForegroundColor Green

# Установка путей
$projectRoot = "C:\Users\Asus\Desktop\ProxyBridge"
$windowsDir = "$projectRoot\Windows"
$guiDir = "$windowsDir\gui"
$installerDir = "$windowsDir\installer"
$outputDir = "$windowsDir\output"

# Создание output директории
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

Write-Host ""
Write-Host "Step 1: Building ProxyBridge..." -ForegroundColor Cyan

# Сборка GUI Release
Set-Location $guiDir
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "✓ Build successful" -ForegroundColor Green

Write-Host ""
Write-Host "Step 2: Compiling ProxyBridgeCore.dll..." -ForegroundColor Cyan

# Компиляция C DLL
$env:PATH = "C:\msys64\mingw64\bin;$env:PATH"
Set-Location $windowsDir

gcc -shared -o ProxyBridgeCore.dll -O2 -DPROXYBRIDGE_EXPORTS src\ProxyBridge.c -IC:\WinDivert-2.2.2-A\include -LC:\WinDivert-2.2.2-A\x64 -lWinDivert -lws2_32 -liphlpapi

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: DLL compilation failed!" -ForegroundColor Red
    exit 1
}

# Копирование DLL в Release
Copy-Item ProxyBridgeCore.dll "$guiDir\bin\Release\net9.0-windows\" -Force

Write-Host "✓ DLL compiled and copied" -ForegroundColor Green

Write-Host ""
Write-Host "Step 3: Creating installer..." -ForegroundColor Cyan

# Сборка установщика с NSIS
Set-Location $installerDir
& $nsisPath ProxyBridge.nsi

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Installer creation failed!" -ForegroundColor Red
    exit 1
}

Write-Host "✓ Installer created" -ForegroundColor Green

Write-Host ""
Write-Host "=== BUILD COMPLETE ===" -ForegroundColor Green
Write-Host ""
Write-Host "Installer location: " -NoNewline
Write-Host "$outputDir\ProxyBridge-Setup-3.0.0.exe" -ForegroundColor Yellow
Write-Host ""

# Открыть папку с установщиком
explorer $outputDir
