package org.proxybridge.android

import android.content.Intent
import android.net.ConnectivityManager
import android.net.VpnService
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import android.os.Process
import android.util.Log
import org.json.JSONObject
import org.proxybridge.pbmobile.Callbacks
import org.proxybridge.pbmobile.Pbmobile
import java.net.InetAddress
import java.net.InetSocketAddress

/**
 * The tunnel: VpnService opens the TUN, pbcore (Go) drives it.
 *
 * The start request is the same JSON the desktop helper takes; rule "process" names are
 * package names. Every app but ProxyBridge itself enters the tunnel (addDisallowedApplication)
 * and the core decides per flow, exactly like on the desktop: apps without a rule go "direct"
 * through the core's own sockets, which never enter the tunnel. (An allowed-apps-only VPN was
 * tried first: Android lingers and drops a VPN that no app in its uid range holds a network
 * request for, and rule changes would need the VPN re-established.) No protect() calls needed.
 */
class PbVpnService : VpnService() {

    companion object {
        const val ACTION_START = "org.proxybridge.android.START"
        const val ACTION_STOP = "org.proxybridge.android.STOP"
        const val ACTION_RELOAD = "org.proxybridge.android.RELOAD"
        const val EXTRA_REQUEST = "request"
        private const val TAG = "pbcore"

        @Volatile var running: Boolean = false
            private set
        @Volatile var lastStatus: String = "idle"
            private set

        private val main = Handler(Looper.getMainLooper())
        private val lines = ArrayDeque<String>()
        var listener: ((String) -> Unit)? = null

        fun log(line: String) {
            Log.i(TAG, line)
            synchronized(lines) {
                lines.addLast(line)
                while (lines.size > 400) lines.removeFirst()
            }
            main.post { listener?.invoke(line) }
        }

        fun logText(): String = synchronized(lines) { lines.joinToString("\n") }
    }

    private var tun: ParcelFileDescriptor? = null
    private val uidNames = HashMap<Long, String>()

    private val callbacks = object : Callbacks {
        override fun log(level: Long, msg: String) {
            if (level >= Pbmobile.LevelInfo) log(msg) else Log.d(TAG, msg)
        }

        override fun ownerUid(proto: Long, srcIP: String, srcPort: Long, dstIP: String, dstPort: Long): Long {
            val cm = getSystemService(ConnectivityManager::class.java) ?: return -1
            return try {
                val uid = cm.getConnectionOwnerUid(
                    proto.toInt(),
                    InetSocketAddress(InetAddress.getByName(srcIP), srcPort.toInt()),
                    InetSocketAddress(InetAddress.getByName(dstIP), dstPort.toInt()),
                )
                if (uid == Process.INVALID_UID) -1 else uid.toLong()
            } catch (e: Exception) {
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
            lastStatus = line
            log("[status] $line")
            try {
                val st = JSONObject(line).optString("status")
                if (st == "down" || st == "error") {
                    running = false
                    main.post { stopSelf() }
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
                val rules = intent.getStringExtra(EXTRA_REQUEST)
                try {
                    Pbmobile.reload(rules ?: "")
                    log("rules reloaded")
                } catch (e: Exception) {
                    log("reload failed: ${e.message}")
                }
            }
            else -> {
                val request = intent?.getStringExtra(EXTRA_REQUEST)
                if (request.isNullOrBlank()) log("start: empty request") else startTunnel(request)
            }
        }
        return START_NOT_STICKY
    }

    private fun startTunnel(request: String) {
        if (Pbmobile.running()) {
            log("already running")
            return
        }
        val req = try {
            JSONObject(request)
        } catch (e: Exception) {
            log("bad request JSON: ${e.message}")
            return
        }
        val rules = req.optJSONArray("rules")
        var catchAll = "direct"
        var named = 0
        if (rules != null) {
            for (i in 0 until rules.length()) {
                val r = rules.getJSONObject(i)
                if (r.optString("process") == "*") catchAll = r.optString("action", "direct") else named++
            }
        }
        val builder = Builder()
            .setSession("ProxyBridge")
            .setMtu(1500)
            .addAddress("10.255.0.2", 24)
            .addRoute("0.0.0.0", 0)
            .addDnsServer("1.1.1.1")
            .addDisallowedApplication(packageName)
        log("tunnel takes every app except ProxyBridge: $named app rules, everything else $catchAll")

        val pfd = builder.establish()
        if (pfd == null) {
            log("VPN not established: no consent or another VPN is active")
            return
        }
        tun = pfd
        // The core gets its own copy of the descriptor; the original stays here and is closed
        // on stop, so the system sees the tunnel end the way VpnService expects.
        val fd = pfd.dup().detachFd()
        try {
            Pbmobile.start(fd.toLong(), request, callbacks)
            running = true
            log("core ${Pbmobile.coreVersion()} started on fd $fd")
        } catch (e: Exception) {
            running = false
            log("core start failed: ${e.message}")
            stopSelf()
        }
    }

    private fun stopTunnel() {
        if (Pbmobile.running()) {
            Pbmobile.stop()
            log("core stopped")
        }
        running = false
        try {
            tun?.close()
        } catch (_: Exception) {
        }
        tun = null
    }

    override fun onRevoke() {
        log("VPN revoked by the system")
        stopTunnel()
        super.onRevoke()
    }

    override fun onDestroy() {
        stopTunnel()
        log("service destroyed")
        super.onDestroy()
    }
}
