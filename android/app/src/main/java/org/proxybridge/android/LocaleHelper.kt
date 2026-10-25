package org.proxybridge.android

import android.content.Context
import android.content.res.Configuration
import java.util.Locale

/** Applies the language chosen in Settings ("" keeps the system one) to a context. */
object LocaleHelper {
    fun wrap(base: Context, lang: String): Context {
        if (lang.isBlank()) return base
        val locale = Locale(lang)
        Locale.setDefault(locale)
        val cfg = Configuration(base.resources.configuration)
        cfg.setLocale(locale)
        return base.createConfigurationContext(cfg)
    }
}
