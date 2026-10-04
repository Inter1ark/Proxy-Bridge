#!/bin/sh
# Run after unzipping if macOS refuses to launch the app:
#   sh fix-perms.sh [path/to/ProxyBridge.app]
# Restores executable bits and removes the quarantine attribute.
set -e
APP="${1:-}"
if [ -z "$APP" ]; then
  for c in ProxyBridge.app ProxyBridge-arm64.app ProxyBridge-x64.app; do
    if [ -d "$c" ]; then APP="$c"; break; fi
  done
fi
if [ -z "$APP" ] || [ ! -d "$APP" ]; then
  echo "usage: sh fix-perms.sh path/to/ProxyBridge.app"
  exit 1
fi
chmod +x "$APP/Contents/MacOS/ProxyBridge" "$APP/Contents/MacOS/pbcore" 2>/dev/null || true
chmod +x "$APP"/Contents/MacOS/*.dylib 2>/dev/null || true
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
echo "ok: $APP"
