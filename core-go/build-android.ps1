# Builds the Android core library (android/app/libs/pbcore.aar) from core-go/mobile with gomobile.
#
#   .\build-android.ps1 [-Version 3.7.0]
#
# Needs: Go, an Android SDK with an NDK (ANDROID_HOME / ANDROID_NDK_HOME, or the defaults below)
# and gomobile:  go install golang.org/x/mobile/cmd/gomobile@latest ; gomobile init
param(
    [string]$Version = "3.7.0-dev",
    [string]$Targets = "android/arm64,android/amd64",
    [int]$Api = 29
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
if (-not $env:ANDROID_NDK_HOME) {
    $ndk = Get-ChildItem (Join-Path $env:ANDROID_HOME "ndk") -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if ($ndk) { $env:ANDROID_NDK_HOME = $ndk.FullName }
}
$env:PATH = "$env:PATH;$(Join-Path $env:USERPROFILE 'go\bin')"
$out = Join-Path $root "..\android\app\libs\pbcore.aar"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
Push-Location $root
try {
    gomobile bind -target $Targets -androidapi $Api -javapkg org.proxybridge `
        -ldflags "-s -w -X proxybridge/core/mobile.Version=$Version" -o $out ./mobile
    if ($LASTEXITCODE -ne 0) { throw "gomobile bind failed" }
    Write-Host "built $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"
} finally {
    Pop-Location
}
