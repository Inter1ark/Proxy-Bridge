$ErrorActionPreference = "Stop"
Remove-Module PSReadLine -Force -ErrorAction SilentlyContinue
Set-Location "C:\Users\Asus\Desktop\ProxyBridge\Windows\gui"
Write-Host "Launching ProxyBridge as Administrator..." -ForegroundColor Cyan
Start-Process "bin\Debug\net9.0-windows\ProxyBridge.exe" -Verb RunAs
