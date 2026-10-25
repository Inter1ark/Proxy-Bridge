package org.proxybridge.android.data

import java.net.URLDecoder
import java.net.URLEncoder

data class ParsedProxy(val type: String, val host: String, val port: Int, val user: String, val pass: String) {
    /** Canonical URL the core understands. */
    fun url(): String {
        val scheme = if (type == "HTTP") "http" else "socks5"
        val auth = if (user.isNotEmpty() || pass.isNotEmpty()) "${enc(user)}:${enc(pass)}@" else ""
        val h = if (host.contains(':')) "[$host]" else host
        return "$scheme://$auth$h:$port"
    }

    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")
}

/** Accepts the same spellings as the desktop app (see gui/Services/ProxyTools.cs). */
object ProxyParser {
    fun parse(input: String?): ParsedProxy? {
        var s = (input ?: "").trim()
        if (s.isEmpty() || s.contains(' ') || s.contains('\t')) return null
        var type = "SOCKS5"
        val schemeEnd = s.indexOf("://")
        if (schemeEnd > 0) {
            type = when (s.substring(0, schemeEnd).lowercase()) {
                "http", "https" -> "HTTP"
                "socks5", "socks5h", "socks" -> "SOCKS5"
                else -> return null
            }
            s = s.substring(schemeEnd + 3).trimEnd('/')
        }
        val at = s.lastIndexOf('@')
        if (at >= 0) {
            var left = s.substring(0, at)
            var right = s.substring(at + 1)
            var hp = splitHostPort(right)
            if (hp == null) {
                hp = splitHostPort(left) ?: return null
                val t = left; left = right; right = t
            }
            val colon = left.indexOf(':')
            val user = dec(if (colon >= 0) left.substring(0, colon) else left)
            val pass = dec(if (colon >= 0) left.substring(colon + 1) else "")
            return ParsedProxy(type, hp.first, hp.second, user, pass)
        }
        if (s.startsWith("[")) {
            val hp = splitHostPort(s) ?: return null
            return ParsedProxy(type, hp.first, hp.second, "", "")
        }
        val parts = s.split(':')
        if (parts.size < 2) return null
        val host = parts[0].trim()
        val port = parts[1].trim().toIntOrNull() ?: return null
        if (host.isEmpty() || port !in 1..65535) return null
        return when {
            parts.size == 2 -> ParsedProxy(type, host, port, "", "")
            parts.size == 3 -> ParsedProxy(type, host, port, "", parts[2].trim())
            else -> ParsedProxy(type, host, port, parts[2].trim(), parts.drop(3).joinToString(":").trim())
        }
    }

    private fun splitHostPort(s: String): Pair<String, Int>? {
        val t = s.trim()
        if (t.startsWith("[")) {
            val end = t.indexOf(']')
            if (end < 0 || end + 1 >= t.length || t[end + 1] != ':') return null
            val port = t.substring(end + 2).toIntOrNull() ?: return null
            return if (port in 1..65535) t.substring(1, end) to port else null
        }
        val colon = t.lastIndexOf(':')
        if (colon <= 0 || t.indexOf(':') != colon) return null
        val port = t.substring(colon + 1).toIntOrNull() ?: return null
        val host = t.substring(0, colon)
        return if (host.isNotEmpty() && port in 1..65535) host to port else null
    }

    private fun dec(s: String) = try { URLDecoder.decode(s.replace("+", "%2B"), "UTF-8") } catch (_: Exception) { s }
}
