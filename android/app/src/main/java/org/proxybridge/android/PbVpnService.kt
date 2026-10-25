package org.proxybridge.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.net.ConnectivityManager
import android.net.VpnService
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import android.os.Process
import android.util.Log
import org.json.JSONObject
import org.proxybridge.android.data.AppConfig
import org.proxybridge.android.data.ConfigStore
import org.proxybridge.pbmobile.Callbacks
import org.proxybridge.pbmobile.Pbmobile
import java.net.InetAddress
import java.net.InetSocketAddress

/**
 * The tunnel: VpnService opens the TUN, pbcore (Go) drives it.
 *
 * Every app but ProxyBridge itself enters the tunnel (addDisallowedApplication) and the core
 * decides per flow by the owning package, exactly like on the desktop: apps without a rule go
 * "direct" through the core's own sockets, which never enter the tunnel. (An allowed-apps-only
 * VPN was tried first: Android lingers and drops a VPN that no app in its uid range holds a
 * network request for, and rule changes would need the VPN re-established.) Rules change live
 * with the "rules" command.
 */
class PbVpnService : VpnService() {

    companion object {
        const val ACTION_START = "org.proxybridge.android.START"
        const val ACTION_STOP = "org.proxybridge.android.STOP"
        const val ACTION_RELOAD = "org.proxybridge.android.RELOAD"
        /** Optional: a raw start request (adb tests); otherwise the saved configuration is used. */
        const val EXTRA_REQUEST = "request"
        private const val TAG = "pbcore"
        private const val CHANNEL = "tunnel"
        private const val NOTIF_ID = 1
        private val main = Handler(Looper.getMainLooper())
        @Volatile var running: Boolean = false
            private set
    }

    private var tun: ParcelFileDescriptor? = null
    private val uidNames = HashMap<Long, String>()

    private val callbacks = object : Callbacks {
        override fun log(level: Long, msg: String) {
            if (level >= Pbmobile.LevelInfo) TunnelState.log(msg) else Log.d(TAG, msg)
        }

        override fun ownerUid(proto: Long, srcIP: String, srcPort: Long, dstIP: String, dstPort: Long): Long {
            // Android 10 added the owner lookup; before it only the "all other apps" row can apply.
            if (Build.VERSION.SDK_INT < 29) return -1
            val cm = getSystemService(ConnectivityManager::class.java) ?: return -1
            return try {
                val uid = cm.getConnectionOwnerUid(
                    proto.toInt(),
                    InetSocketAddress(InetAddress.getByName(srcIP), srcPort.toInt()),
                    InetSocketAddress(InetAddress.getByName(dstIP), dstPort.toInt()),
                )
                if (uid == Process.INVALID_UID) -1 else uid.toLong()
            } catch (_: Exception) {
                -1
            }
        }

        override fun packageForUid(uid: Long): String {
            uidNames[uid]?.let { return it }
            val name = packageManager.getPackagesForUid(uid.toInt())?.firstOrNull() ?: "uid:$uid"
            uidNames[uid] = name
            return name
        }

        override fun status(line: String) {
            try {
                val o = JSONObject(line)
                when (o.optString("status")) {
                    "up" -> { running = true; TunnelState.set(TunnelState.Phase.On) }
                    "stats" -> TunnelState.stats(o.optLong("up"), o.optLong("down"))
                    "down" -> {
                        running = false
                        if (TunnelState.state.value.phase != TunnelState.Phase.Error) TunnelState.set(TunnelState.Phase.Off)
                        main.post { stopSelf() }
                    }
                    "error" -> {
                        running = false
                        TunnelState.set(TunnelState.Phase.Error, o.optString("message"))
                        TunnelState.log("error: " + o.optString("message"))
                    }
                    else -> TunnelState.log("[status] $line")
                }
            } catch (_: Exception) {
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopTunnel()
                stopSelf()
            }
            ACTION_RELOAD -> {
                val rules = intent.getStringExtra(EXTRA_REQUEST) ?: ConfigStore.current.reloadJson()
                try {
                    Pbmobile.reload(rules)
                    updateNotification()
                } catch (e: Exception) {
                    TunnelState.log("reload failed: ${e.message}")
                }
            }
            else -> {
                val request = intent?.getStringExtra(EXTRA_REQUEST) ?: ConfigStore.current.startRequestJson()
                startTunnel(request)
            }
        }
        return START_NOT_STICKY
    }

    private fun startTunnel(request: String) {
        if (Pbmobile.running()) return
        TunnelState.set(TunnelState.Phase.Connecting)
        startForeground(NOTIF_ID, notification())
        val builder = Builder()
            .setSession("ProxyBridge")
            .setMtu(1500)
            .addAddress("10.255.0.2", 24)
            .addRoute("0.0.0.0", 0)
            .addDnsServer("1.1.1.1")
            .addDisallowedApplication(packageName)
        val pfd = builder.establish()
        if (pfd == null) {
            fail("VPN not established: no consent or another VPN is active")
            return
        }
        tun = pfd
        // The core gets its own copy of the descriptor; the original stays here and is closed
        // on stop, so the system sees the tunnel end the way VpnService expects.
        val fd = pfd.dup().detachFd()
        try {
            Pbmobile.start(fd.toLong(), request, callbacks)
            TunnelState.log("core ${Pbmobile.coreVersion()} started")
        } catch (e: Exception) {
            fail("core start failed: ${e.message}")
        }
    }

    private fun fail(msg: String) {
        running = false
        TunnelState.log(msg)
        TunnelState.set(TunnelState.Phase.Error, msg)
        try { tun?.close() } catch (_: Exception) {}
        tun = null
        stopSelf()
    }

    private fun stopTunnel() {
        if (Pbmobile.running()) {
            Pbmobile.stop()
            TunnelState.log("core stopped")
        }
        running = false
        try { tun?.close() } catch (_: Exception) {}
        tun = null
        TunnelState.set(TunnelState.Phase.Off)
        TunnelState.stats(0, 0)
    }

    override fun onRevoke() {
        TunnelState.log("VPN revoked by the system")
        stopTunnel()
        super.onRevoke()
    }

    override fun onDestroy() {
        stopTunnel()
        super.onDestroy()
    }

    private fun notification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null) {
            nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.notif_channel), NotificationManager.IMPORTANCE_LOW))
        }
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, PbVpnService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val b = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CHANNEL) else @Suppress("DEPRECATION") Notification.Builder(this)
        return b.setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(getString(R.string.notif_connected))
            .setContentText(routeSummary(ConfigStore.current))
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, getString(R.string.disconnect), stop).build())
            .build()
    }

    private fun updateNotification() {
        getSystemService(NotificationManager::class.java).notify(NOTIF_ID, notification())
    }

    private fun routeSummary(c: AppConfig): String {
        val named = c.rules.filter { it.enabled && it.action == "proxy" && c.proxy(it.proxyId) != null }
        val rest = if (c.defaultAction == "proxy") c.proxy(c.defaultProxyId) else null
        return when {
            named.isEmpty() && rest == null -> getString(R.string.all_direct)
            named.isEmpty() -> getString(R.string.whole_via_fmt, rest!!.title())
            rest == null -> getString(R.string.apps_via_fmt, named.joinToString { it.label.ifBlank { it.pkg } })
            else -> getString(R.string.apps_via_rest_fmt, named.joinToString { it.label.ifBlank { it.pkg } }, rest.title())
        }
    }
}
