package org.proxybridge.probe

import android.app.Activity
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.TextView
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URL
import kotlin.concurrent.thread

/**
 * Test app: every few seconds asks the internet what IP it has (TCP, HTTPS) and sends a
 * STUN request (UDP) so the tunnel's TCP and UDP paths can both be checked from logcat:
 *   adb logcat -s probe
 */
class ProbeActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private lateinit var out: TextView
    private var round = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        out = TextView(this).apply { textSize = 13f; setPadding(24, 48, 24, 24) }
        setContentView(out)
        tick()
    }

    private fun tick() {
        round++
        val n = round
        thread {
            val ip = try {
                val c = URL("https://api.ipify.org?format=text").openConnection() as HttpURLConnection
                c.connectTimeout = 10000; c.readTimeout = 10000
                c.inputStream.bufferedReader().readText().trim()
            } catch (e: Exception) {
                "error ${e.javaClass.simpleName}: ${e.message}"
            }
            val stun = try { stunIp() } catch (e: Exception) { "error ${e.javaClass.simpleName}: ${e.message}" }
            val net = try {
                val cm = getSystemService(android.net.ConnectivityManager::class.java)
                val a = cm.activeNetwork
                val lp = a?.let { cm.getLinkProperties(it) }
                "${a?.toString() ?: "none"}/${lp?.interfaceName ?: "?"} dns=${lp?.dnsServers?.joinToString { it.hostAddress ?: "?" } ?: "?"}"
            } catch (e: Exception) { "net-error ${e.message}" }
            val line = "round $n: net=$net | tcp ip=$ip | stun ip=$stun"
            Log.i("probe", line)
            handler.post {
                out.append("$line\n")
                if (n % 20 == 0) out.text = ""
            }
        }
        handler.postDelayed({ tick() }, 5000)
    }

    /** STUN binding request to Google's server; the mapped address is what the UDP path shows. */
    private fun stunIp(): String {
        val sock = DatagramSocket()
        sock.soTimeout = 5000
        try {
            val tid = ByteArray(12).also { java.util.Random().nextBytes(it) }
            val req = ByteArray(20)
            req[0] = 0; req[1] = 1; req[2] = 0; req[3] = 0
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4.toByte(); req[7] = 0x42
            System.arraycopy(tid, 0, req, 8, 12)
            val addr = InetAddress.getByName("stun.l.google.com")
            sock.send(DatagramPacket(req, req.size, addr, 19302))
            val buf = ByteArray(1024)
            val p = DatagramPacket(buf, buf.size)
            sock.receive(p)
            var off = 20
            while (off + 4 <= p.length) {
                val at = ((buf[off].toInt() and 0xff) shl 8) or (buf[off + 1].toInt() and 0xff)
                val al = ((buf[off + 2].toInt() and 0xff) shl 8) or (buf[off + 3].toInt() and 0xff)
                if (at == 0x20 && al >= 8) {
                    val m = byteArrayOf(0x21, 0x12, 0xA4.toByte(), 0x42)
                    return (0 until 4).joinToString(".") { ((buf[off + 8 + it].toInt() xor m[it].toInt()) and 0xff).toString() }
                }
                off += 4 + ((al + 3) and 3.inv())
            }
            return "no mapped address"
        } finally {
            sock.close()
        }
    }
}
