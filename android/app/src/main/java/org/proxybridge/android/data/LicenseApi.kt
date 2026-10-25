package org.proxybridge.android.data

import android.content.Context
import android.provider.Settings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.time.Instant

/** The license API of proxybridge.org (docs/LICENSE_API.md); the same keys as on the desktop. */
object LicenseApi {
    private const val BASE = "https://www.proxybridge.org"
    private const val CACHE_DAYS = 7L

    sealed class Result {
        data class Ok(val plan: String, val expiresAt: String) : Result()
        data class Error(val code: String) : Result() // not_found, expired, device_limit, revoked, network, other
    }

    fun hwid(context: Context): String {
        val id = Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: "android"
        val d = MessageDigest.getInstance("SHA-256").digest("android:$id:${context.packageName}".toByteArray())
        return d.joinToString("") { "%02x".format(it) }
    }

    /** Valid locally: a key, not expired, and a server check within the cache window. */
    fun isValid(l: LicenseState, now: Long = System.currentTimeMillis()): Boolean {
        if (l.key.isBlank() || l.lastCheck <= 0) return false
        if (now - l.lastCheck > CACHE_DAYS * 86_400_000L) return false
        if (l.expiresAt.isNotBlank()) {
            val exp = runCatching { Instant.parse(l.expiresAt).toEpochMilli() }.getOrNull() ?: return false
            if (exp < now) return false
        }
        return true
    }

    suspend fun activate(context: Context, key: String, version: String) = call(context, "activate", key, version)
    suspend fun verify(context: Context, key: String, version: String) = call(context, "verify", key, version)

    suspend fun deactivate(context: Context, key: String): Boolean = withContext(Dispatchers.IO) {
        try {
            val body = JSONObject().put("key", key).put("hwid", hwid(context)).toString()
            post("$BASE/api/license/deactivate", body).second.optBoolean("ok")
        } catch (_: Exception) {
            false
        }
    }

    private suspend fun call(context: Context, op: String, key: String, version: String): Result = withContext(Dispatchers.IO) {
        try {
            val body = JSONObject().put("key", key.trim().uppercase()).put("hwid", hwid(context)).put("app_version", version).toString()
            val (status, json) = post("$BASE/api/license/$op", body)
            if (status == 200 && json.optBoolean("ok")) {
                Result.Ok(json.optString("plan"), json.optString("expires_at").takeIf { it != "null" } ?: "")
            } else {
                Result.Error(json.optString("error").ifBlank { "other" })
            }
        } catch (_: Exception) {
            Result.Error("network")
        }
    }

    private fun post(url: String, body: String): Pair<Int, JSONObject> {
        val c = URL(url).openConnection() as HttpURLConnection
        c.requestMethod = "POST"
        c.connectTimeout = 15000
        c.readTimeout = 15000
        c.doOutput = true
        c.setRequestProperty("Content-Type", "application/json; charset=utf-8")
        c.setRequestProperty("User-Agent", "ProxyBridge-Android")
        c.outputStream.use { it.write(body.toByteArray()) }
        val status = c.responseCode
        val stream = if (status in 200..299) c.inputStream else c.errorStream
        val text = stream?.bufferedReader()?.readText() ?: "{}"
        return status to (runCatching { JSONObject(text) }.getOrNull() ?: JSONObject())
    }
}
