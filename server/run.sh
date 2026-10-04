#!/usr/bin/env sh
# Local development server (Linux / macOS)
cd "$(dirname "$0")" || exit 1
exec python -m uvicorn app.main:app --reload --port 8080
