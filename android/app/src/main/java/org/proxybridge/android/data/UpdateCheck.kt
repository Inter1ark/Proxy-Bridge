package org.proxybridge.android.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/** Reads the "android" block of the update manifest the desktop apps use. */
object UpdateCheck {
    private const val MANIFEST = "https://www.proxybridge.org/update/latest.json"

    data class Info(val version: String, val url: String)

    /** The newest Android version in the manifest, or null when the manifest has no Android block / is unreachable. */
    suspend fun fetch(): Info? = withContext(Dispatchers.IO) {
        try {
            val c = URL(MANIFEST).openConnection() as HttpURLConnection
            c.connectTimeout = 15000; c.readTimeout = 15000
            c.setRequestProperty("User-Agent", "ProxyBridge-Android")
            val text = c.inputStream.bufferedReader().readText()
            // a manifest without an Android block means "nothing newer", not a failure
            val a = JSONObject(text).optJSONObject("android") ?: return@withContext Info("", "")
            Info(a.optString("version"), a.optString("url"))
        } catch (_: Exception) {
            null
        }
    }

    /** True when a is newer than b ("3.7.1" > "3.7.0"). */
    fun newer(a: String, b: String): Boolean {
        val x = a.split(".").map { it.filter(Char::isDigit).toIntOrNull() ?: 0 }
        val y = b.split(".").map { it.filter(Char::isDigit).toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(x.size, y.size)) {
            val p = x.getOrElse(i) { 0 }; val q = y.getOrElse(i) { 0 }
            if (p != q) return p > q
        }
        return false
    }
}
