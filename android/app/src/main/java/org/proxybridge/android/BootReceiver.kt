package org.proxybridge.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.VpnService
import org.proxybridge.android.data.ConfigStore
import org.proxybridge.android.data.LicenseApi

/** Connects after a reboot when the setting is on and the VPN consent was given before. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return
        ConfigStore.init(context.applicationContext)
        val c = ConfigStore.current
        if (!c.autoConnect || !c.routesSomething()) return
        if (!LicenseApi.isValid(c.license) && !BuildConfigFlags.allowUnlicensed) return
        if (VpnService.prepare(context) != null) return // consent would need the screen
        context.startForegroundService(Intent(context, PbVpnService::class.java).setAction(PbVpnService.ACTION_START))
    }
}
