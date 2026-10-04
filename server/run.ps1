# Local development server (Windows)
Set-Location $PSScriptRoot
python -m uvicorn app.main:app --reload --port 8080
