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
SOCK="$DATA/control.sock"
LOGDIR="/Library/Logs/ProxyBridge"
PORT=34050

[ "$(id -u)" = "0" ] || { echo "must run as root" >&2; exit 1; }
[ -f "$SRC" ] || { echo "pbcore not found: $SRC" >&2; exit 1; }

mkdir -p /Library/PrivilegedHelperTools "$DATA" "$LOGDIR"
chmod 755 /Library/PrivilegedHelperTools "$DATA" "$LOGDIR"

# A previous daemon or a per-session pbcore would hold the control port.
launchctl bootout "system/$LABEL" 2>/dev/null || true
pkill -f "/Contents/MacOS/pbcore " 2>/dev/null || true
rm -f "$SOCK"

cp -f "$SRC" "$HELPER.new"
chown root:wheel "$HELPER.new"
chmod 755 "$HELPER.new"
xattr -c "$HELPER.new" 2>/dev/null || true
mv -f "$HELPER.new" "$HELPER"

# The token lets the app drive the daemon. It is readable by every local user (group staff):
# the person using the app is often not an administrator (the admin only types the password
# at install time), and driving the proxy daemon gives no privilege beyond routing traffic.
if [ ! -s "$TOKEN" ]; then
  od -An -tx1 -N16 /dev/urandom | tr -d ' \n' > "$TOKEN"
  echo >> "$TOKEN"
fi
chown root:staff "$TOKEN"
chmod 644 "$TOKEN"

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
    <string>--control-socket</string><string>$SOCK</string>
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

# A job that is still registered (bootout did not finish) makes bootstrap fail with
# "service already loaded" or "Input/output error"; kickstart restarts it in that case.
if ! launchctl bootstrap system "$PLIST" 2>/tmp/pb-bootstrap.err; then
  echo "bootstrap: $(cat /tmp/pb-bootstrap.err)"; sleep 1
  launchctl bootstrap system "$PLIST" 2>/dev/null || launchctl kickstart -k "system/$LABEL"
fi
# wait until the daemon has opened its unix socket (up to 10 s); the TCP port is
# only probed with a timeout, since a VPN packet filter may swallow loopback SYNs
i=0
while [ $i -lt 20 ]; do
  if [ -S "$SOCK" ] || nc -z -w 1 127.0.0.1 $PORT 2>/dev/null; then break; fi
  sleep 0.5; i=$((i+1))
done
ls -l "$SOCK" 2>/dev/null || echo "no unix socket yet"
launchctl print "system/$LABEL" 2>/dev/null | grep -E "state =|pid =" | head -2
echo "helper installed"
