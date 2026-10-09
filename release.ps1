# Release ProxyBridge for Windows: bump version, build, hash, publish the update manifest locally.
#
#   .\release.ps1 -Version 3.3.0 -NotesRu "Fix one|Fix two" -NotesEn "Fix one|Fix two"
#
# Notes are "|"-separated lines. Steps:
#   1. set the version in gui\ProxyBridge.GUI.csproj, gui\app.manifest, installer\ProxyBridge.nsi
#   2. dotnet publish (win-x64, self-contained) and build the installer via build-installer.ps1
#   3. compute SHA-256 and size, copy the installer to site\download\ (not committed to git)
#   4. write site\update\latest.json (the app reads https://www.proxybridge.org/update/latest.json)
#   5. print the next manual steps (upload, GitHub release). Nothing is uploaded by this script.
#
# Switches:
#   -DryRun         show what would change, write nothing, build nothing
#   -SkipBuild      do not build, use the existing output\ProxyBridge-Setup-<ver>.exe
#   -SkipCoreBuild  do not recompile ProxyBridgeCore.dll (passed to build-installer.ps1)
#   -MinSupported   versions below this get a mandatory update (default: keep the current manifest value)
#   -Root           repository root (default: folder of this script), useful for testing on a copy

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$NotesRu = "",
    [string]$NotesEn = "",
    [string]$MinSupported = "",
    [string]$Published = (Get-Date -Format "yyyy-MM-dd"),
    [string]$SiteBase = "https://www.proxybridge.org",
    [string]$GitHubRepo = "Inter1ark/Proxy-Bridge",
    [string]$Root = $PSScriptRoot,
    [switch]$DryRun,
    [switch]$SkipBuild,
    [switch]$SkipCoreBuild
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 3.3.0, got '$Version'" }
if ($MinSupported -and $MinSupported -notmatch '^\d+\.\d+\.\d+$') { throw "MinSupported must look like 3.2.0" }

$Root = (Resolve-Path $Root).Path
$csproj    = Join-Path $Root "gui\ProxyBridge.GUI.csproj"
$appManif  = Join-Path $Root "gui\app.manifest"
$nsi       = Join-Path $Root "installer\ProxyBridge.nsi"
$builder   = Join-Path $Root "build-installer.ps1"
$installer = Join-Path $Root "output\ProxyBridge-Setup-$Version.exe"
$dlDir     = Join-Path $Root "site\download"
$updDir    = Join-Path $Root "site\update"
$manifest  = Join-Path $updDir "latest.json"

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

# Replace a regex in a text file, keeping its encoding (UTF-8 with or without BOM).
function Set-InFile([string]$Path, [string]$Pattern, [string]$Replacement, [string]$What) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.Substring(1) }
    $rx = [regex]$Pattern
    if (-not $rx.IsMatch($text)) { throw "$What not found in $Path" }
    $new = $rx.Replace($text, $Replacement, 1)
    if ($new -eq $text) { Write-Host "  $What already set ($Path)"; return }
    if ($DryRun) { Write-Host "  [dry-run] would set $What in $Path"; return }
    [IO.File]::WriteAllText($Path, $new, (New-Object Text.UTF8Encoding($hasBom)))
    Write-Host "  set $What in $Path"
}

function Split-Notes([string]$s) {
    return @($s -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
}

function ConvertTo-JsonString([string]$s) {
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $s.ToCharArray()) {
        switch ($ch) {
            '"'  { [void]$sb.Append('\"') }
            '\'  { [void]$sb.Append('\\') }
            "`n" { [void]$sb.Append('\n') }
            "`r" { [void]$sb.Append('\r') }
            "`t" { [void]$sb.Append('\t') }
            default {
                if ([int]$ch -lt 0x20) { [void]$sb.AppendFormat('\u{0:x4}', [int]$ch) } else { [void]$sb.Append($ch) }
            }
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function ConvertTo-JsonArray($items, [string]$indent) {
    if ($items.Count -eq 0) { return "[]" }
    $lines = $items | ForEach-Object { "$indent  " + (ConvertTo-JsonString $_) }
    return "[`n" + ($lines -join ",`n") + "`n$indent]"
}

# ---------------------------------------------------------------------------
Step "ProxyBridge release $Version$(if ($DryRun) { ' (dry run)' })"

$ru = Split-Notes $NotesRu
$en = Split-Notes $NotesEn
if ($ru.Count -eq 0 -or $en.Count -eq 0) { Write-Host "  warning: release notes are empty for ru or en" -ForegroundColor Yellow }

if (-not $MinSupported) {
    $MinSupported = "3.2.0"
    if (Test-Path $manifest) {
        try {
            $old = Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($old.min_supported) { $MinSupported = [string]$old.min_supported }
        } catch { }
    }
}
Write-Host "  min_supported = $MinSupported"

# ---------------------------------------------------------------------------
Step "1. Version numbers"
Set-InFile $csproj   '<Version>[^<]*</Version>'                       "<Version>$Version</Version>"            "csproj <Version>"
Set-InFile $appManif '(<assemblyIdentity\s+version=")[^"]*(")'         "`${1}$Version.0`${2}"                  "app.manifest assemblyIdentity version"
Set-InFile $nsi      '(!define\s+PRODUCT_VERSION\s+")[^"]*(")'         "`${1}$Version`${2}"                    "NSIS PRODUCT_VERSION"

# ---------------------------------------------------------------------------
Step "2. Build"
if ($DryRun -or $SkipBuild) {
    Write-Host "  skipped ($(if ($DryRun) { '-DryRun' } else { '-SkipBuild' }))"
} else {
    Push-Location (Join-Path $Root "gui")
    try {
        dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    } finally { Pop-Location }

    $builderArgs = @{ SkipPublish = $true; NoOpen = $true }
    if ($SkipCoreBuild) { $builderArgs.SkipCoreBuild = $true }
    Push-Location $Root
    try {
        & $builder @builderArgs
        if ($LASTEXITCODE -ne 0) { throw "build-installer.ps1 failed" }
    } finally { Pop-Location }
}

# ---------------------------------------------------------------------------
Step "3. Hash and copy"
if (-not (Test-Path $installer)) {
    if ($DryRun) {
        Write-Host "  [dry-run] installer not built yet: $installer" -ForegroundColor Yellow
        $sha = "<sha256 after build>"; $size = 0
    } else {
        throw "Installer not found: $installer"
    }
} else {
    $sha = (Get-FileHash -Algorithm SHA256 -Path $installer).Hash.ToLowerInvariant()
    $size = (Get-Item $installer).Length
}
Write-Host "  installer: $installer"
Write-Host "  sha256:    $sha"
Write-Host "  size:      $size bytes"

$fileName = "ProxyBridge-Setup-$Version.exe"
if ($DryRun) {
    Write-Host "  [dry-run] would copy to $dlDir\$fileName"
} else {
    New-Item -ItemType Directory -Force -Path $dlDir | Out-Null
    Copy-Item $installer (Join-Path $dlDir $fileName) -Force
    Write-Host "  copied to $dlDir\$fileName"
}

# ---------------------------------------------------------------------------
Step "4. Update manifest"
$gh = "https://github.com/$GitHubRepo/releases/download/v$Version"
$json = @"
{
  "version": "$Version",
  "published": "$Published",
  "min_supported": "$MinSupported",
  "notes": {
    "ru": $(ConvertTo-JsonArray $ru '    '),
    "en": $(ConvertTo-JsonArray $en '    ')
  },
  "windows": {
    "url": "$SiteBase/download/$fileName",
    "fallback_url": "$gh/$fileName",
    "sha256": "$sha",
    "size": $size
  },
  "macos": {
    "arm64": "$gh/ProxyBridge-$Version-macOS-arm64.zip",
    "x64": "$gh/ProxyBridge-$Version-macOS-x64.zip",
    "page": "$SiteBase/skachat/"
  }
}
"@
$null = $json | ConvertFrom-Json  # validate
if ($DryRun) {
    Write-Host "  [dry-run] would write $manifest :"
    Write-Host $json
} else {
    New-Item -ItemType Directory -Force -Path $updDir | Out-Null
    [IO.File]::WriteAllText($manifest, $json.Replace("`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "  wrote $manifest"
}

# ---------------------------------------------------------------------------
Step "5. Next steps (manual)"
Write-Host "  a) Upload the installer FIRST, then the manifest (clients act on the manifest):"
Write-Host "       site\download\$fileName  ->  <server>/site/download/"
Write-Host "       site\update\latest.json   ->  <server>/site/update/"
Write-Host "  b) Check: $SiteBase/download/$fileName and $SiteBase/update/latest.json"
Write-Host "  c) GitHub fallback release:"
Write-Host "       gh release create v$Version `"$installer`" --repo $GitHubRepo --title `"ProxyBridge $Version`" --notes-file <notes.md>"
Write-Host "  d) macOS zips (if built) go to the same GitHub release:"
Write-Host "       ProxyBridge-$Version-macOS-arm64.zip, ProxyBridge-$Version-macOS-x64.zip"
Write-Host "  e) Commit the version bump and site\update\latest.json (site\download is git-ignored)."
