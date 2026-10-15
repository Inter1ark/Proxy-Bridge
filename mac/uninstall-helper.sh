#!/bin/sh
# Removes the pbcore daemon installed by install-helper.sh. Run as root.
set -u

LABEL="org.proxybridge.pbcore"
launchctl bootout "system/$LABEL" 2>/dev/null || true
rm -f "/Library/LaunchDaemons/$LABEL.plist" "/Library/PrivilegedHelperTools/$LABEL"
rm -f "/Library/Application Support/ProxyBridge/control.token"
rmdir "/Library/Application Support/ProxyBridge" 2>/dev/null || true
echo "helper removed"
