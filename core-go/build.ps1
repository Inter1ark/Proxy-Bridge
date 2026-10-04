# Cross-compiles pbcore for macOS (arm64, amd64) and Linux (amd64).
# Output: core-go/dist/pbcore-darwin-arm64, pbcore-darwin-amd64, pbcore-linux-amd64
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$go = Get-Command go -ErrorAction SilentlyContinue
if (-not $go) {
    $candidate = "C:\Program Files\Go\bin\go.exe"
    if (Test-Path $candidate) { $env:PATH = "C:\Program Files\Go\bin;" + $env:PATH }
    else { throw "go.exe not found. Install Go (winget install -e --id GoLang.Go) and retry." }
}

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

$env:CGO_ENABLED = "0"
$ldflags = "-s -w"

if (-not $SkipTests) {
    Write-Host "== go test ./..."
    go test ./...
    if ($LASTEXITCODE -ne 0) { throw "go test failed" }
}

$targets = @(
    @{ os = "darwin"; arch = "arm64" },
    @{ os = "darwin"; arch = "amd64" },
    @{ os = "linux";  arch = "amd64" }
)

foreach ($t in $targets) {
    $env:GOOS = $t.os
    $env:GOARCH = $t.arch
    $out = Join-Path $dist ("pbcore-{0}-{1}" -f $t.os, $t.arch)
    Write-Host ("== go vet ({0}/{1})" -f $t.os, $t.arch)
    go vet ./...
    if ($LASTEXITCODE -ne 0) { throw "go vet failed for $($t.os)/$($t.arch)" }
    Write-Host ("== go build -> {0}" -f $out)
    go build -trimpath -ldflags $ldflags -o $out .
    if ($LASTEXITCODE -ne 0) { throw "go build failed for $($t.os)/$($t.arch)" }
}

Remove-Item Env:GOOS, Env:GOARCH -ErrorAction SilentlyContinue
Write-Host "== done"
Get-ChildItem $dist | Format-Table Name, Length -AutoSize
