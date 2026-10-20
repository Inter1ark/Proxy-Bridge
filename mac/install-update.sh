#!/bin/sh
# Installs a downloaded ProxyBridge .pkg in place (the app runs this once with
# administrator privileges) and refreshes the system helper from the new bundle,
# so the next connect needs no second password. Run as root.
#
#   install-update.sh <path to .pkg>
set -u

PKG="${1:?usage: install-update.sh <pkg>}"
APP="/Applications/ProxyBridge.app"

[ "$(id -u)" = "0" ] || { echo "must run as root" >&2; exit 1; }
[ -f "$PKG" ] || { echo "package not found: $PKG" >&2; exit 1; }

# The command-line installer does not show the Gatekeeper dialog that a double-clicked
# package would (the app downloaded the file itself, so it carries no quarantine flag).
if ! installer -pkg "$PKG" -target / ; then
  echo "installer failed" >&2
  exit 2
fi
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

# The helper daemon runs the previous pbcore; swap it for the new one now.
if [ -f "/Library/LaunchDaemons/org.proxybridge.pbcore.plist" ] && [ -f "$APP/Contents/MacOS/install-helper.sh" ]; then
  /bin/sh "$APP/Contents/MacOS/install-helper.sh" "$APP/Contents/MacOS/pbcore" || echo "helper refresh failed (the app will retry on connect)" >&2
fi
rm -f "$PKG"
echo "update installed"
