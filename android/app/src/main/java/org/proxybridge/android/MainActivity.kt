package org.proxybridge.android

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.List
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import org.proxybridge.android.data.AppRule
import org.proxybridge.android.data.CheckInfo
import org.proxybridge.android.data.ConfigStore
import org.proxybridge.android.data.LicenseApi
import org.proxybridge.android.data.LicenseState
import org.proxybridge.android.data.ProxyEntry
import org.proxybridge.android.data.UpdateCheck
import org.proxybridge.android.ui.AppPickerDialog
import org.proxybridge.android.ui.HomeScreen
import org.proxybridge.android.ui.LogDialog
import org.proxybridge.android.ui.PbTheme
import org.proxybridge.android.ui.ProxiesScreen
import org.proxybridge.android.ui.SettingsScreen
import org.proxybridge.pbmobile.Pbmobile

class MainActivity : ComponentActivity() {

    private var tab by mutableStateOf(0)
    private var checking by mutableStateOf(setOf<Int>())
    private var licenseBusy by mutableStateOf(false)
    private var licenseNote by mutableStateOf("")
    private var hint by mutableStateOf("")
    private var pendingRequest: String? = null

    private val consent = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { r ->
        if (r.resultCode == RESULT_OK) startService(pendingRequest) else hint = getString(R.string.pb_status_consent)
    }
    private val notifPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun attachBaseContext(newBase: android.content.Context) {
        ConfigStore.init(newBase)
        super.attachBaseContext(LocaleHelper.wrap(newBase, ConfigStore.current.language))
    }

    private var updateBusy by mutableStateOf(false)
    private var updateNote by mutableStateOf("")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ConfigStore.init(this)
        handleIntent(intent)
        verifyLicenseInBackground()
        checkUpdateInBackground(manual = false)
        setContent {
            PbTheme {
                val config by ConfigStore.config.collectAsStateWithLifecycle()
                val state by TunnelState.state.collectAsStateWithLifecycle()
                val log by TunnelState.log.collectAsStateWithLifecycle()
                var picker by remember { mutableStateOf(false) }
                var logs by remember { mutableStateOf(false) }
                val licensed = LicenseApi.isValid(config.license)
                val homeHint = when {
                    Build.VERSION.SDK_INT < 29 && config.rules.isNotEmpty() -> stringResource(R.string.pb_status_old_android)
                    state.phase != TunnelState.Phase.Off -> ""
                    hint.isNotBlank() -> hint
                    config.proxies.isEmpty() -> stringResource(R.string.pb_status_no_proxy)
                    !config.routesSomething() -> stringResource(R.string.pb_status_nothing)
                    !licensed -> stringResource(R.string.pb_status_license)
                    else -> ""
                }
                androidx.compose.material3.Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background, contentColor = MaterialTheme.colorScheme.onBackground) {
                Column(Modifier.fillMaxSize().statusBarsPadding()) {
                    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp)) {
                        Text("proxybridge", style = MaterialTheme.typography.titleLarge)
                    }
                    androidx.compose.foundation.layout.Box(Modifier.weight(1f)) {
                        when (tab) {
                            0 -> HomeScreen(config, state, homeHint, onConnect = { toggle(config.routesSomething(), licensed) }, onAddApp = { picker = true }, onUpdate = { openUpdate() })
                            1 -> ProxiesScreen(config, checking, onCheck = { check(it) }, onCheckAll = { config.proxies.forEach { check(it) } })
                            else -> SettingsScreen(config, versionName(), Pbmobile.coreVersion(), licenseBusy, licenseNote,
                                onActivate = { activate(it) }, onDeactivate = { deactivate() }, onLogs = { logs = true },
                                onLanguage = { lang -> if (lang != config.language) { ConfigStore.update { it.copy(language = lang) }; recreate() } },
                                updateBusy = updateBusy, updateNote = updateNote, onCheckUpdate = { checkUpdateInBackground(manual = true) }, onDownloadUpdate = { openUpdate() })
                        }
                    }
                    NavigationBar(containerColor = org.proxybridge.android.ui.Pb.surface, modifier = Modifier.navigationBarsPadding()) {
                        NavigationBarItem(selected = tab == 0, onClick = { tab = 0 }, icon = { Icon(Icons.Default.Home, null) }, label = { Text(stringResource(R.string.pb_tab_home)) })
                        NavigationBarItem(selected = tab == 1, onClick = { tab = 1 }, icon = { Icon(Icons.Default.List, null) }, label = { Text(stringResource(R.string.pb_tab_proxies)) })
                        NavigationBarItem(selected = tab == 2, onClick = { tab = 2 }, icon = { Icon(Icons.Default.Settings, null) }, label = { Text(stringResource(R.string.pb_tab_settings)) })
                    }
                }
                }
                if (picker) AppPickerDialog(onPick = { e ->
                    ConfigStore.update { c ->
                        if (c.rules.any { it.pkg == e.pkg }) c
                        else c.copy(rules = c.rules + AppRule(e.pkg, e.label, if (c.proxies.isNotEmpty()) AppRule.ACTION_PROXY else AppRule.ACTION_DIRECT, c.proxies.firstOrNull()?.id ?: 0))
                    }
                    picker = false
                }, onDismiss = { picker = false })
                if (logs) LogDialog(log) { logs = false }
            }
        }
        // rules change while connected: apply live
        lifecycleScope.launch {
            var last = ConfigStore.current.reloadJson()
            ConfigStore.config.collect { c ->
                val now = c.reloadJson()
                if (now != last) {
                    last = now
                    if (PbVpnService.running) startService(Intent(this@MainActivity, PbVpnService::class.java).setAction(PbVpnService.ACTION_RELOAD))
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /** adb: --es request '<start request json>' --ez autostart true | --es reload '<rules json>' | --ez stop true */
    private fun handleIntent(intent: Intent?) {
        if (intent == null) return
        intent.getStringExtra("reload")?.let { rules ->
            startService(Intent(this, PbVpnService::class.java).setAction(PbVpnService.ACTION_RELOAD).putExtra(PbVpnService.EXTRA_REQUEST, rules))
        }
        if (intent.getBooleanExtra("stop", false)) {
            startService(Intent(this, PbVpnService::class.java).setAction(PbVpnService.ACTION_STOP))
        }
        val req = intent.getStringExtra("request")
        if (req != null && intent.getBooleanExtra("autostart", false)) connect(req)
    }

    private fun toggle(routes: Boolean, licensed: Boolean) {
        val phase = TunnelState.state.value.phase
        if (phase == TunnelState.Phase.On || phase == TunnelState.Phase.Connecting) {
            startService(Intent(this, PbVpnService::class.java).setAction(PbVpnService.ACTION_STOP))
            return
        }
        if (!routes) return
        if (!licensed && !BuildConfigFlags.allowUnlicensed) { tab = 2; return }
        connect(null)
    }

    private fun connect(request: String?) {
        hint = ""
        pendingRequest = request
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        val intent = VpnService.prepare(this)
        if (intent != null) consent.launch(intent) else startService(request)
    }

    private fun startService(request: String?) {
        val i = Intent(this, PbVpnService::class.java).setAction(PbVpnService.ACTION_START)
        if (request != null) i.putExtra(PbVpnService.EXTRA_REQUEST, request)
        startForegroundService(i)
    }

    private fun check(p: ProxyEntry) {
        if (p.id in checking) return
        checking = checking + p.id
        lifecycleScope.launch {
            val lang = if (resources.configuration.locales[0].language == "en") "en" else "ru"
            val raw = withContext(Dispatchers.IO) { runCatching { Pbmobile.checkProxy(p.url, lang) }.getOrElse { "{\"ok\":false,\"error\":\"${it.message}\"}" } }
            val o = JSONObject(raw)
            val info = CheckInfo(
                ok = o.optBoolean("ok"), ip = o.optString("ip"), country = o.optString("country"), countryCode = o.optString("country_code"),
                city = o.optString("city"), ms = o.optInt("ms"), udp = o.optBoolean("udp"), error = o.optString("error"), at = System.currentTimeMillis(),
            )
            ConfigStore.update { c -> c.copy(proxies = c.proxies.map { if (it.id == p.id) it.copy(check = info) else it }) }
            checking = checking - p.id
        }
    }

    private fun activate(key: String) {
        licenseBusy = true; licenseNote = ""
        lifecycleScope.launch {
            when (val r = LicenseApi.activate(this@MainActivity, key, versionName())) {
                is LicenseApi.Result.Ok -> ConfigStore.update {
                    it.copy(license = LicenseState(key.trim().uppercase(), r.plan, r.expiresAt, System.currentTimeMillis(), LicenseApi.hwid(this@MainActivity)))
                }
                is LicenseApi.Result.Error -> licenseNote = licenseError(r.code)
            }
            licenseBusy = false
        }
    }

    private fun deactivate() {
        licenseBusy = true
        lifecycleScope.launch {
            LicenseApi.deactivate(this@MainActivity, ConfigStore.current.license.key)
            ConfigStore.update { it.copy(license = LicenseState()) }
            licenseBusy = false
        }
    }

    /** Re-checks the key with the server once a day; the server's "no" clears the local license. */
    private fun verifyLicenseInBackground() {
        val l = ConfigStore.current.license
        if (l.key.isBlank() || System.currentTimeMillis() - l.lastCheck < 86_400_000L) return
        lifecycleScope.launch {
            when (val r = LicenseApi.verify(this@MainActivity, l.key, versionName())) {
                is LicenseApi.Result.Ok -> ConfigStore.update { it.copy(license = it.license.copy(plan = r.plan, expiresAt = r.expiresAt, lastCheck = System.currentTimeMillis())) }
                is LicenseApi.Result.Error -> if (r.code != "network") ConfigStore.update { it.copy(license = LicenseState(key = l.key)) }
            }
        }
    }

    /** Once a day (or on demand) compares the manifest's Android version with ours. */
    private fun checkUpdateInBackground(manual: Boolean) {
        val c = ConfigStore.current
        if (!manual && System.currentTimeMillis() - c.lastUpdateCheck < 86_400_000L) return
        updateBusy = true; updateNote = ""
        lifecycleScope.launch {
            val info = UpdateCheck.fetch()
            val mine = versionName()
            val newer = info != null && UpdateCheck.newer(info.version, mine)
            ConfigStore.update {
                it.copy(
                    lastUpdateCheck = if (info != null) System.currentTimeMillis() else it.lastUpdateCheck,
                    updateVersion = if (newer) info!!.version else "",
                    updateUrl = if (newer) info!!.url else "",
                )
            }
            if (manual) updateNote = if (info == null) getString(R.string.pb_update_failed) else if (newer) "" else getString(R.string.pb_update_latest)
            updateBusy = false
        }
    }

    private fun openUpdate() {
        val url = ConfigStore.current.updateUrl
        if (url.isBlank()) return
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse(url))) }
    }

    private fun licenseError(code: String): String = when (code) {
        "not_found" -> getString(R.string.pb_license_err_not_found)
        "expired" -> getString(R.string.pb_license_err_expired)
        "device_limit" -> getString(R.string.pb_license_err_device_limit)
        "revoked" -> getString(R.string.pb_license_err_revoked)
        "network" -> getString(R.string.pb_license_err_network)
        else -> getString(R.string.pb_license_err_other, code)
    }

    private fun versionName(): String = runCatching { packageManager.getPackageInfo(packageName, 0).versionName ?: "" }.getOrDefault("")
}

/** Build-time switches for the prototype phase. */
object BuildConfigFlags {
    /** True while testing: the connect button works without a key. */
    const val allowUnlicensed = true
}
