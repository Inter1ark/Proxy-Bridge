package org.proxybridge.android.data

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

@Serializable
data class CheckInfo(
    val ok: Boolean = false,
    val ip: String = "",
    val country: String = "",
    val countryCode: String = "",
    val city: String = "",
    val ms: Int = 0,
    val udp: Boolean = false,
    val error: String = "",
    val at: Long = 0,
)

@Serializable
data class ProxyEntry(
    val id: Int,
    val url: String,
    val label: String = "",
    val check: CheckInfo? = null,
) {
    /** "Амстердам" or host:port when unnamed. */
    fun title(): String = label.ifBlank { hostPort() }
    fun hostPort(): String = ProxyParser.parse(url)?.let { "${it.host}:${it.port}" } ?: url
    fun kind(): String = if (url.startsWith("http")) "HTTP" else "SOCKS5"
}

/** One row of the home table: an app and where its traffic goes. */
@Serializable
data class AppRule(
    val pkg: String,
    val label: String = "",
    val action: String = ACTION_PROXY, // proxy | direct | block
    val proxyId: Int = 0,
    val enabled: Boolean = true,
) {
    companion object {
        const val ACTION_PROXY = "proxy"
        const val ACTION_DIRECT = "direct"
        const val ACTION_BLOCK = "block"
    }
}

@Serializable
data class LicenseState(
    val key: String = "",
    val plan: String = "",
    val expiresAt: String = "", // ISO, empty for lifetime
    val lastCheck: Long = 0,     // epoch ms of the last successful server check
    val hwid: String = "",
)

@Serializable
data class AppConfig(
    val proxies: List<ProxyEntry> = emptyList(),
    val rules: List<AppRule> = emptyList(),
    val defaultAction: String = AppRule.ACTION_DIRECT, // what "all other apps" do: direct | proxy | block
    val defaultProxyId: Int = 0,
    val udpMode: String = "auto",                       // auto | block | direct
    val dnsViaProxy: Boolean = true,
    val license: LicenseState = LicenseState(),
    val nextProxyId: Int = 1,
) {
    fun proxy(id: Int): ProxyEntry? = proxies.firstOrNull { it.id == id }

    /** True when at least one enabled rule or the catch-all sends traffic to an existing proxy. */
    fun routesSomething(): Boolean =
        (defaultAction == AppRule.ACTION_PROXY && proxy(defaultProxyId) != null) ||
            rules.any { it.enabled && it.action == AppRule.ACTION_PROXY && proxy(it.proxyId) != null }

    /** The start request the core takes (see core-go/session.go). */
    fun startRequestJson(): String {
        val req = JSONObject()
        val proxies = JSONArray()
        for (p in this.proxies) proxies.put(JSONObject().put("id", p.id).put("url", p.url))
        req.put("proxies", proxies)
        req.put("rules", rulesJson())
        req.put("udp", when (udpMode) { "block" -> "block"; "direct" -> "direct"; else -> "proxy" })
        req.put("dns_via_proxy", dnsViaProxy)
        req.put("dns", "1.1.1.1")
        if (defaultAction == AppRule.ACTION_PROXY && proxy(defaultProxyId) != null) req.put("dns_proxy", defaultProxyId)
        return req.toString()
    }

    /** The "rules" command payload: proxies plus rules. */
    fun rulesJson(): JSONArray {
        val arr = JSONArray()
        for (r in rules) {
            if (!r.enabled) continue
            val o = JSONObject().put("process", r.pkg)
            when (r.action) {
                AppRule.ACTION_PROXY -> {
                    if (proxy(r.proxyId) == null) continue
                    o.put("action", "proxy").put("proxy", r.proxyId)
                }
                AppRule.ACTION_BLOCK -> o.put("action", "block")
                else -> o.put("action", "direct")
            }
            arr.put(o)
        }
        val rest = JSONObject().put("process", "*")
        if (defaultAction == AppRule.ACTION_PROXY && proxy(defaultProxyId) != null) rest.put("action", "proxy").put("proxy", defaultProxyId)
        else if (defaultAction == AppRule.ACTION_BLOCK) rest.put("action", "block")
        else rest.put("action", "direct")
        arr.put(rest)
        return arr
    }

    fun reloadJson(): String {
        val proxies = JSONArray()
        for (p in this.proxies) proxies.put(JSONObject().put("id", p.id).put("url", p.url))
        return JSONObject().put("proxies", proxies).put("rules", rulesJson()).toString()
    }
}

/** The one place the configuration lives: a JSON file plus a flow the UI and the service observe. */
object ConfigStore {
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true; prettyPrint = true }
    private lateinit var file: File
    private val _config = MutableStateFlow(AppConfig())
    val config: StateFlow<AppConfig> get() = _config
    @Volatile private var loaded = false

    fun init(context: Context) {
        if (loaded) return
        file = File(context.filesDir, "config.json")
        _config.value = try {
            if (file.exists()) json.decodeFromString<AppConfig>(file.readText()) else AppConfig()
        } catch (_: Exception) {
            AppConfig()
        }
        loaded = true
    }

    val current: AppConfig get() = _config.value

    @Synchronized
    fun update(change: (AppConfig) -> AppConfig) {
        val next = change(_config.value)
        _config.value = next
        try {
            file.writeText(json.encodeToString(next))
        } catch (_: Exception) {
        }
    }
}
