package org.proxybridge.android

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import androidx.core.graphics.drawable.toBitmap

/** Installed apps with a launcher icon, for the app picker and the rule rows. */
object AppCatalog {
    data class Entry(val pkg: String, val label: String)

    @Volatile private var entries: List<Entry> = emptyList()
    private val icons = HashMap<String, Bitmap?>()

    fun apps(context: Context): List<Entry> {
        if (entries.isNotEmpty()) return entries
        val pm = context.packageManager
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        val list = pm.queryIntentActivities(intent, PackageManager.MATCH_ALL)
            .map { it.activityInfo.packageName }
            .distinct()
            .filter { it != context.packageName }
            .mapNotNull { pkg ->
                runCatching { Entry(pkg, pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString()) }.getOrNull()
            }
            .sortedBy { it.label.lowercase() }
        entries = list
        return list
    }

    fun icon(context: Context, pkg: String): Bitmap? {
        synchronized(icons) {
            if (icons.containsKey(pkg)) return icons[pkg]
        }
        val bmp = runCatching { context.packageManager.getApplicationIcon(pkg).toBitmap(96, 96) }.getOrNull()
        synchronized(icons) { icons[pkg] = bmp }
        return bmp
    }

    fun label(context: Context, pkg: String): String =
        apps(context).firstOrNull { it.pkg == pkg }?.label ?: pkg
}
