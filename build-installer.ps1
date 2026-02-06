# Build ProxyBridge Installer
# Автоматическая сборка установщика ProxyBridge v3.0.0
# Запускать из корня проекта: .\build-installer.ps1

param(
    [string]$WinDivertPath = "C:\WinDivert-2.2.2-A"
)

$ErrorActionPreference = "Stop"

Write-Host "=== ProxyBridge Installer Builder ===" -ForegroundColor Cyan
Write-Host ""

# Определяем корень проекта (папка, где лежит этот скрипт)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = $scriptDir
$guiDir = "$projectRoot\gui"
$installerDir = "$projectRoot\installer"
$outputDir = "$projectRoot\output"
$publishDir = "$guiDir\bin\Release\net9.0-windows\win-x64\publish"

# === Проверка зависимостей ===

# 1. NSIS
$nsisPath = "C:\Program Files (x86)\NSIS\makensis.exe"
if (-not (Test-Path $nsisPath)) {
    Write-Host "ОШИБКА: NSIS не найден: $nsisPath" -ForegroundColor Red
    Write-Host "Установите: https://nsis.sourceforge.io/Download" -ForegroundColor Yellow
    exit 1
}
Write-Host "✓ NSIS найден" -ForegroundColor Green

# 2. GCC (MinGW)
$gccCmd = Get-Command gcc -ErrorAction SilentlyContinue
if (-not $gccCmd) {
    # Пробуем MSYS2 путь
    $env:PATH = "C:\msys64\mingw64\bin;$env:PATH"
    $gccCmd = Get-Command gcc -ErrorAction SilentlyContinue
    if (-not $gccCmd) {
        Write-Host "ОШИБКА: GCC не найден. Установите MSYS2/MinGW-w64." -ForegroundColor Red
        exit 1
    }
}
Write-Host "✓ GCC найден: $($gccCmd.Source)" -ForegroundColor Green

# 3. WinDivert SDK
if (-not (Test-Path "$WinDivertPath\include\windivert.h")) {
    Write-Host "ОШИБКА: WinDivert SDK не найден: $WinDivertPath" -ForegroundColor Red
    Write-Host "Передайте путь: .\build-installer.ps1 -WinDivertPath C:\path\to\WinDivert" -ForegroundColor Yellow
    exit 1
}
Write-Host "✓ WinDivert SDK найден: $WinDivertPath" -ForegroundColor Green

# 4. Проверяем наличие WinDivert64.sys
$sysFile = "$WinDivertPath\x64\WinDivert64.sys"
if (-not (Test-Path $sysFile)) {
    Write-Host "ОШИБКА: WinDivert64.sys не найден: $sysFile" -ForegroundColor Red
    Write-Host "Скачайте полный пакет WinDivert v2.2.2 с https://github.com/basil00/WinDivert/releases" -ForegroundColor Yellow
    exit 1
}
Write-Host "✓ WinDivert64.sys найден" -ForegroundColor Green

# Создание output директории
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# ========================================
# Шаг 1: Компиляция ProxyBridgeCore.dll
# ========================================
Write-Host ""
Write-Host "Шаг 1: Компиляция ProxyBridgeCore.dll..." -ForegroundColor Cyan

Set-Location $projectRoot

gcc -shared -o ProxyBridgeCore.dll -O2 -DPROXYBRIDGE_EXPORTS `
    src\ProxyBridge.c `
    "-I$WinDivertPath\include" `
    "-L$WinDivertPath\x64" `
    -lWinDivert -lws2_32 -liphlpapi

if ($LASTEXITCODE -ne 0) {
    Write-Host "ОШИБКА: Компиляция DLL провалилась!" -ForegroundColor Red
    exit 1
}
Write-Host "✓ ProxyBridgeCore.dll скомпилирована" -ForegroundColor Green

# Копируем WinDivert64.sys в корень проекта (csproj ссылается на него)
Copy-Item $sysFile "$projectRoot\WinDivert64.sys" -Force

# ========================================
# Шаг 2: Сборка .NET приложения (Self-Contained)
# ========================================
Write-Host ""
Write-Host "Шаг 2: Сборка ProxyBridge (Self-Contained)..." -ForegroundColor Cyan

Set-Location $guiDir
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "ОШИБКА: Сборка .NET провалилась!" -ForegroundColor Red
    exit 1
}
Write-Host "✓ .NET приложение собрано" -ForegroundColor Green

# ========================================
# Шаг 3: Проверка publish-папки
# ========================================
Write-Host ""
Write-Host "Шаг 3: Проверка файлов..." -ForegroundColor Cyan

$requiredFiles = @(
    "ProxyBridge.exe",
    "ProxyBridgeCore.dll",
    "WinDivert.dll",
    "WinDivert64.sys"
)

$allOk = $true
foreach ($f in $requiredFiles) {
    if (Test-Path "$publishDir\$f") {
        Write-Host "  ✓ $f" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $f ОТСУТСТВУЕТ!" -ForegroundColor Red
        $allOk = $false
    }
}

# Проверяем локализации
foreach ($lang in @("ru", "zh")) {
    if (Test-Path "$publishDir\$lang") {
        Write-Host "  ✓ $lang\" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ $lang\ не найдена (необязательно)" -ForegroundColor Yellow
    }
}

if (-not $allOk) {
    Write-Host "ОШИБКА: Не все файлы на месте!" -ForegroundColor Red
    exit 1
}

Write-Host "✓ Все файлы на месте" -ForegroundColor Green

# ========================================
# Шаг 4: Сборка NSIS-установщика
# ========================================
Write-Host ""
Write-Host "Шаг 4: Создание установщика..." -ForegroundColor Cyan

Set-Location $installerDir
& $nsisPath ProxyBridge.nsi

if ($LASTEXITCODE -ne 0) {
    Write-Host "ОШИБКА: Создание установщика провалилось!" -ForegroundColor Red
    exit 1
}

$installerFile = "$outputDir\ProxyBridge-Setup-3.0.0.exe"
if (Test-Path $installerFile) {
    $size = [math]::Round((Get-Item $installerFile).Length / 1MB, 1)
    Write-Host "✓ Установщик создан ($($size) МБ)" -ForegroundColor Green
} else {
    Write-Host "⚠ Установщик не найден по ожидаемому пути" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== ГОТОВО ===" -ForegroundColor Green
Write-Host ""
Write-Host "Установщик: " -NoNewline
Write-Host "$installerFile" -ForegroundColor Yellow
Write-Host ""

# Открыть папку с установщиком
explorer $outputDir
