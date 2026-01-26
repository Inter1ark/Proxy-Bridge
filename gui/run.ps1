Set-Location "C:\Users\Asus\Desktop\ProxyBridge\Windows\gui"
Write-Host "Starting ProxyBridge..." -ForegroundColor Green
Write-Host ""

try {
    dotnet run
} catch {
    Write-Host "Error occurred:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}

Write-Host ""
Write-Host "Press any key to exit..." -ForegroundColor Yellow
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
