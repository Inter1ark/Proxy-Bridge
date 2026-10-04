# Builds the macOS app bundles on Windows (cross-compile, unsigned):
#   1. pbcore (Go) for darwin arm64 and amd64
#   2. GUI published for osx-arm64 and osx-x64
#   3. mac/dist/ProxyBridge-arm64.app and ProxyBridge-x64.app
#   4. mac/dist/ProxyBridge-<version>-macOS-arm64.zip and -x64.zip (Unix modes set via Python zipfile)
param(
    [string]$Version = "3.2.0",
    [switch]$SkipGo,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$macDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $macDir
$coreDir = Join-Path $repo "core-go"
$guiProj = Join-Path $repo "gui\ProxyBridge.GUI.csproj"
$icns = Join-Path $repo "branding\logo.icns"
$dist = Join-Path $macDir "dist"

if (-not (Test-Path $icns)) { throw "Icon not found: $icns" }
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $python) { throw "python is required to build the zips" }

if (-not $SkipGo) {
    Write-Host "== building pbcore"
    $args = @()
    if ($SkipTests) { $args += "-SkipTests" }
    & (Join-Path $coreDir "build.ps1") @args
}
foreach ($f in @("pbcore-darwin-arm64", "pbcore-darwin-amd64")) {
    if (-not (Test-Path (Join-Path $coreDir "dist\$f"))) { throw "missing $f; run core-go\build.ps1" }
}

New-Item -ItemType Directory -Force $dist | Out-Null

function Write-InfoPlist([string]$path, [string]$version) {
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>ProxyBridge</string>
    <key>CFBundleDisplayName</key>
    <string>ProxyBridge</string>
    <key>CFBundleIdentifier</key>
    <string>org.proxybridge.app</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>ProxyBridge</string>
    <key>CFBundleIconFile</key>
    <string>logo.icns</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.utilities</string>
</dict>
</plist>
"@
    [System.IO.File]::WriteAllText($path, $plist, (New-Object System.Text.UTF8Encoding($false)))
}

$targets = @(
    @{ rid = "osx-arm64"; suffix = "arm64"; core = "pbcore-darwin-arm64" },
    @{ rid = "osx-x64";   suffix = "x64";   core = "pbcore-darwin-amd64" }
)

foreach ($t in $targets) {
    $publishDir = Join-Path $dist ("publish-" + $t.rid)
    Write-Host ("== dotnet publish {0}" -f $t.rid)
    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
    dotnet publish $guiProj -c Release -r $t.rid --self-contained -p:PublishSingleFile=false -p:DebugType=none -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($t.rid)" }

    $app = Join-Path $dist ("ProxyBridge-{0}.app" -f $t.suffix)
    Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
    $contents = Join-Path $app "Contents"
    $macos = Join-Path $contents "MacOS"
    $resources = Join-Path $contents "Resources"
    New-Item -ItemType Directory -Force $macos, $resources | Out-Null

    Copy-Item (Join-Path $publishDir "*") $macos -Recurse -Force
    Copy-Item (Join-Path $coreDir ("dist\" + $t.core)) (Join-Path $macos "pbcore") -Force
    Copy-Item $icns (Join-Path $resources "logo.icns") -Force
    Write-InfoPlist (Join-Path $contents "Info.plist") $Version
    [System.IO.File]::WriteAllText((Join-Path $contents "PkgInfo"), "APPL????", (New-Object System.Text.UTF8Encoding($false)))

    # Windows-only payload must not ship in the bundle.
    foreach ($junk in @("ProxyBridgeCore.dll", "WinDivert.dll", "WinDivert64.sys")) {
        Remove-Item (Join-Path $macos $junk) -Force -ErrorAction SilentlyContinue
    }

    $zip = Join-Path $dist ("ProxyBridge-{0}-macOS-{1}.zip" -f $Version, $t.suffix)
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Write-Host ("== zip {0}" -f $zip)
    & $python.Source (Join-Path $macDir "make-app-zip.py") $app $zip ProxyBridge pbcore --extra (Join-Path $macDir "fix-perms.sh")
    if ($LASTEXITCODE -ne 0) { throw "zip failed for $($t.suffix)" }
    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
}

Write-Host "== done"
Get-ChildItem $dist -Filter "*.zip" | Format-Table Name, Length -AutoSize
