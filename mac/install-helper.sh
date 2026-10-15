#!/bin/sh
# Installs the pbcore daemon ("system helper") so ProxyBridge can connect
# without an administrator password: a root-owned copy of pbcore under
# /Library/PrivilegedHelperTools, a launchd job that keeps it running, and a
# control token readable by administrators only. Run as root.
#
#   install-helper.sh <path to pbcore>
set -eu

SRC="${1:?usage: install-helper.sh <pbcore>}"
LABEL="org.proxybridge.pbcore"
HELPER="/Library/PrivilegedHelperTools/$LABEL"
PLIST="/Library/LaunchDaemons/$LABEL.plist"
DATA="/Library/Application Support/ProxyBridge"
TOKEN="$DATA/control.token"
LOGDIR="/Library/Logs/ProxyBridge"
PORT=34050

[ "$(id -u)" = "0" ] || { echo "must run as root" >&2; exit 1; }
[ -f "$SRC" ] || { echo "pbcore not found: $SRC" >&2; exit 1; }

mkdir -p /Library/PrivilegedHelperTools "$DATA" "$LOGDIR"
chmod 755 /Library/PrivilegedHelperTools "$DATA" "$LOGDIR"

# A previous daemon or a per-session pbcore would hold the control port.
launchctl bootout "system/$LABEL" 2>/dev/null || true
pkill -f "/Contents/MacOS/pbcore " 2>/dev/null || true

cp -f "$SRC" "$HELPER.new"
chown root:wheel "$HELPER.new"
chmod 755 "$HELPER.new"
xattr -c "$HELPER.new" 2>/dev/null || true
mv -f "$HELPER.new" "$HELPER"

# The token lets the app drive the daemon; only root and administrators can read it.
if [ ! -s "$TOKEN" ]; then
  od -An -tx1 -N16 /dev/urandom | tr -d ' \n' > "$TOKEN"
  echo >> "$TOKEN"
fi
chown root:admin "$TOKEN"
chmod 640 "$TOKEN"

cat > "$PLIST.new" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>$HELPER</string>
    <string>--daemon</string>
    <string>--control-port</string><string>$PORT</string>
    <string>--token-file</string><string>$TOKEN</string>
    <string>--log-level</string><string>info</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ProcessType</key><string>Interactive</string>
  <key>StandardOutPath</key><string>$LOGDIR/pbcore.log</string>
  <key>StandardErrorPath</key><string>$LOGDIR/pbcore.log</string>
</dict>
</plist>
EOF
chown root:wheel "$PLIST.new"
chmod 644 "$PLIST.new"
mv -f "$PLIST.new" "$PLIST"

# Keep the log from growing without bound.
if [ -f "$LOGDIR/pbcore.log" ] && [ "$(stat -f %z "$LOGDIR/pbcore.log" 2>/dev/null || echo 0)" -gt 5000000 ]; then
  mv -f "$LOGDIR/pbcore.log" "$LOGDIR/pbcore.log.1"
fi

launchctl bootstrap system "$PLIST"
echo "helper installed"
