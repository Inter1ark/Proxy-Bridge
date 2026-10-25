package org.proxybridge.android

import android.app.Activity
import android.content.Intent
import android.net.VpnService
import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.ScrollView
import android.widget.TextView

/** Prototype screen: the start request as text, connect / disconnect, the core log. */
class MainActivity : Activity() {

    private lateinit var request: EditText
    private lateinit var log: TextView
    private lateinit var status: TextView
    private lateinit var scroll: ScrollView
    private var pendingRequest: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        request = findViewById(R.id.request)
        log = findViewById(R.id.log)
        status = findViewById(R.id.status)
        scroll = findViewById(R.id.logScroll)

        val saved = getSharedPreferences("pb", MODE_PRIVATE).getString("request", null)
        request.setText(saved ?: SAMPLE)

        findViewById<Button>(R.id.start).setOnClickListener { connect(request.text.toString()) }
        findViewById<Button>(R.id.stop).setOnClickListener {
            startService(Intent(this, PbVpnService::class.java).setAction(PbVpnService.ACTION_STOP))
        }
        log.text = PbVpnService.logText()
        PbVpnService.listener = { line ->
            log.append("\n$line")
            status.text = PbVpnService.lastStatus
            scroll.post { scroll.fullScroll(ScrollView.FOCUS_DOWN) }
        }
        handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /** adb: am start -n org.proxybridge.android/.MainActivity --es request '<json>' --ez autostart true
     *       --es reload '<rules json>' replaces the rules live, --ez stop true disconnects */
    private fun handleIntent(intent: Intent?) {
        val req = intent?.getStringExtra("request") ?: return
        request.setText(req)
        if (intent.getBooleanExtra("autostart", false)) connect(req)
        intent.getStringExtra("reload")?.let { rules ->
            startService(
                Intent(this, PbVpnService::class.java)
                    .setAction(PbVpnService.ACTION_RELOAD)
                    .putExtra(PbVpnService.EXTRA_REQUEST, rules)
            )
        }
        if (intent.getBooleanExtra("stop", false)) {
            startService(Intent(this, PbVpnService::class.java).setAction(PbVpnService.ACTION_STOP))
        }
    }

    private fun connect(req: String) {
        getSharedPreferences("pb", MODE_PRIVATE).edit().putString("request", req).apply()
        pendingRequest = req
        val consent = VpnService.prepare(this)
        if (consent != null) startActivityForResult(consent, 1) else onActivityResult(1, RESULT_OK, null)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != 1) return
        if (resultCode != RESULT_OK) {
            PbVpnService.log("VPN consent refused")
            return
        }
        val req = pendingRequest ?: return
        startService(
            Intent(this, PbVpnService::class.java)
                .setAction(PbVpnService.ACTION_START)
                .putExtra(PbVpnService.EXTRA_REQUEST, req)
        )
    }

    override fun onDestroy() {
        PbVpnService.listener = null
        super.onDestroy()
    }

    companion object {
        private val SAMPLE = """
            {"proxies":[{"id":1,"url":"socks5://user:pass@host:port"}],
             "rules":[{"process":"org.proxybridge.probe","action":"proxy","proxy":1},
                      {"process":"*","action":"direct"}],
             "udp":"proxy","dns_via_proxy":true,"dns":"1.1.1.1"}
        """.trimIndent()
    }
}
