# ProxyBridge for Android (prototype)

The same pbcore engine as on macOS, driven by Android's `VpnService`.

- `app/`: the ProxyBridge app. `PbVpnService` opens the TUN and hands the descriptor to the
  core (`core-go/mobile`, built into `app/libs/pbcore.aar`). Every app but ProxyBridge enters the
  tunnel; the core decides per flow by the owning package (`ConnectivityManager.getConnectionOwnerUid`),
  so rules change live without re-establishing the VPN. Apps without a rule go direct through the
  core's own sockets. DNS through a proxy uses DNS over HTTPS on port 443 (some proxy vendors drop
  port 53) and falls back to DNS over TCP.
- `probe/`: a test app that reports its public IP over TCP and over UDP (STUN) every 5 s to logcat
  (`adb logcat -s probe`), so per-app rules can be checked on an emulator.

Build the core library first, then the apps:

    cd core-go && .\build-android.ps1
    cd android && .\gradlew assembleDebug

Drive the prototype from adb (the first connect shows the VPN consent dialog):

    adb shell am start -n org.proxybridge.android/.MainActivity --es request '<start request json>' --ez autostart true
    adb shell am start -n org.proxybridge.android/.MainActivity --es request 'x' --es reload '<rules json>'
    adb shell am start -n org.proxybridge.android/.MainActivity --es request 'x' --ez stop true

The start request is the one the desktop helper takes (`core-go/session.go`), with rule
`process` names being package names.
