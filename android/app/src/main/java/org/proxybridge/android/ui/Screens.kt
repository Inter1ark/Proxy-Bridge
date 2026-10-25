package org.proxybridge.android.ui

import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.AlertDialog
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.proxybridge.android.AppCatalog
import org.proxybridge.android.R
import org.proxybridge.android.TunnelState
import org.proxybridge.android.data.AppConfig
import org.proxybridge.android.data.AppRule
import org.proxybridge.android.data.ConfigStore
import org.proxybridge.android.data.LicenseApi
import org.proxybridge.android.data.LicenseState
import org.proxybridge.android.data.ProxyEntry
import org.proxybridge.android.data.ProxyParser
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

// ---------------------------------------------------------------- shared bits

@Composable
fun SectionTitle(text: String) {
    Text(text.uppercase(), style = MaterialTheme.typography.labelSmall, color = Pb.muted, modifier = Modifier.padding(top = 18.dp, bottom = 8.dp))
}

@Composable
fun Card(content: @Composable () -> Unit) {
    Column(
        Modifier.fillMaxWidth().background(Pb.surface, RoundedCornerShape(12.dp)).border(1.dp, Pb.line, RoundedCornerShape(12.dp)).padding(14.dp),
    ) { content() }
}

@Composable
fun AppIcon(pkg: String, size: Int = 36) {
    val ctx = LocalContext.current
    val bmp = remember(pkg) { AppCatalog.icon(ctx, pkg) }
    if (bmp != null) Image(bmp.asImageBitmap(), null, Modifier.size(size.dp))
    else Box(Modifier.size(size.dp).background(Pb.surface2, RoundedCornerShape(8.dp)))
}

/** The "where does it go" dropdown: direct, block, or one of the proxies. */
@Composable
fun TargetPicker(config: AppConfig, action: String, proxyId: Int, allowBlock: Boolean, onPick: (String, Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val label = when (action) {
        AppRule.ACTION_PROXY -> config.proxy(proxyId)?.title() ?: stringResource(R.string.action_direct)
        AppRule.ACTION_BLOCK -> stringResource(R.string.action_block)
        else -> stringResource(R.string.action_direct)
    }
    val color = if (action == AppRule.ACTION_PROXY && config.proxy(proxyId) != null) Pb.text else Pb.muted
    Box {
        Row(
            Modifier.background(Pb.surface2, RoundedCornerShape(8.dp)).border(1.dp, Pb.line, RoundedCornerShape(8.dp)).clickable { open = true }.padding(horizontal = 10.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(label, color = color, fontSize = 13.sp, maxLines = 1)
            Icon(Icons.Default.ArrowDropDown, null, tint = Pb.muted)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(text = { Text(stringResource(R.string.action_direct)) }, onClick = { open = false; onPick(AppRule.ACTION_DIRECT, 0) })
            if (allowBlock) DropdownMenuItem(text = { Text(stringResource(R.string.action_block)) }, onClick = { open = false; onPick(AppRule.ACTION_BLOCK, 0) })
            for (p in config.proxies) {
                DropdownMenuItem(
                    text = { Column { Text(p.title()); Text("${p.kind()} · ${p.hostPort()}", style = MaterialTheme.typography.bodySmall) } },
                    onClick = { open = false; onPick(AppRule.ACTION_PROXY, p.id) },
                )
            }
        }
    }
}

// ---------------------------------------------------------------- home

@Composable
fun HomeScreen(config: AppConfig, state: TunnelState.Snapshot, hint: String, onConnect: () -> Unit, onAddApp: () -> Unit) {
    val ctx = LocalContext.current
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 16.dp), contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 24.dp)) {
        item {
            ConnectPanel(state, config, hint, onConnect)
            Text(stringResource(R.string.home_title).uppercase(), style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(top = 20.dp))
            Text(stringResource(R.string.home_subtitle), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 4.dp, bottom = 12.dp))
            if (config.rules.isEmpty()) {
                Text(stringResource(R.string.rules_empty), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(bottom = 10.dp))
            }
        }
        items(config.rules, key = { it.pkg }) { rule ->
            Column(Modifier.fillMaxWidth().padding(vertical = 10.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    AppIcon(rule.pkg)
                    Column(Modifier.weight(1f).padding(horizontal = 10.dp)) {
                        Text(rule.label.ifBlank { AppCatalog.label(ctx, rule.pkg) }, maxLines = 1, fontWeight = FontWeight.SemiBold, color = if (rule.enabled) Pb.text else Pb.muted)
                        Text(rule.pkg, style = MaterialTheme.typography.bodySmall, maxLines = 1)
                    }
                    Switch(checked = rule.enabled, onCheckedChange = { on ->
                        ConfigStore.update { c -> c.copy(rules = c.rules.map { if (it.pkg == rule.pkg) it.copy(enabled = on) else it }) }
                    })
                    IconButton(onClick = { ConfigStore.update { c -> c.copy(rules = c.rules.filter { it.pkg != rule.pkg }) } }) {
                        Icon(Icons.Default.Delete, stringResource(R.string.delete), tint = Pb.muted)
                    }
                }
                Row(Modifier.padding(start = 46.dp, top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("→", color = Pb.muted, modifier = Modifier.padding(end = 8.dp))
                    TargetPicker(config, rule.action, rule.proxyId, allowBlock = true) { a, id ->
                        ConfigStore.update { c -> c.copy(rules = c.rules.map { if (it.pkg == rule.pkg) it.copy(action = a, proxyId = id) else it }) }
                    }
                }
            }
            HorizontalDivider(color = Pb.line)
        }
        item {
            Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(36.dp).background(Pb.surface2, RoundedCornerShape(8.dp)).border(1.dp, Pb.line, RoundedCornerShape(8.dp)), contentAlignment = Alignment.Center) {
                    Text("⁘", color = Pb.cyan, fontSize = 18.sp)
                }
                Column(Modifier.weight(1f).padding(horizontal = 10.dp)) {
                    Text(stringResource(R.string.rest_title), fontWeight = FontWeight.SemiBold)
                    Text(stringResource(R.string.rest_subtitle), style = MaterialTheme.typography.bodySmall)
                }
                TargetPicker(config, config.defaultAction, config.defaultProxyId, allowBlock = false) { a, id ->
                    ConfigStore.update { it.copy(defaultAction = a, defaultProxyId = id) }
                }
            }
            OutlinedButton(onClick = onAddApp, modifier = Modifier.padding(top = 8.dp)) {
                Icon(Icons.Default.Add, null); Spacer(Modifier.width(6.dp)); Text(stringResource(R.string.add_app))
            }
        }
    }
}

@Composable
fun ConnectPanel(state: TunnelState.Snapshot, config: AppConfig, hint: String, onConnect: () -> Unit) {
    val on = state.phase == TunnelState.Phase.On
    val busy = state.phase == TunnelState.Phase.Connecting
    val ring = when (state.phase) {
        TunnelState.Phase.On -> Pb.cyan
        TunnelState.Phase.Connecting -> Pb.warn
        TunnelState.Phase.Error -> Pb.danger
        else -> Pb.line
    }
    Column(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Box(
            Modifier.size(132.dp).border(4.dp, ring, CircleShape).padding(10.dp).background(if (on || busy) Pb.blue else Pb.surface2, CircleShape).clickable { onConnect() },
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Default.PowerSettingsNew, null, tint = Pb.text, modifier = Modifier.size(52.dp))
        }
        val word = when (state.phase) {
            TunnelState.Phase.On -> stringResource(R.string.state_on)
            TunnelState.Phase.Connecting -> stringResource(R.string.state_connecting)
            TunnelState.Phase.Error -> stringResource(R.string.state_error)
            else -> stringResource(R.string.state_off)
        }
        Text(word.uppercase(), style = MaterialTheme.typography.titleMedium, color = if (state.phase == TunnelState.Phase.Error) Pb.danger else if (on) Pb.cyan else Pb.text, modifier = Modifier.padding(top = 12.dp), letterSpacing = 1.sp)
        val detail = if (state.phase == TunnelState.Phase.Error) state.message else hint
        if (detail.isNotBlank()) Text(detail, style = MaterialTheme.typography.bodySmall, color = if (state.phase == TunnelState.Phase.Error) Pb.danger else Pb.muted, modifier = Modifier.padding(top = 6.dp, start = 8.dp, end = 8.dp))
        if (on) Text(stringResource(R.string.traffic_fmt, human(state.down), human(state.up)), style = MaterialTheme.typography.labelSmall, color = Pb.muted, modifier = Modifier.padding(top = 6.dp))
        Button(
            onClick = onConnect,
            modifier = Modifier.fillMaxWidth().padding(top = 14.dp).height(48.dp),
            colors = ButtonDefaults.buttonColors(containerColor = if (on) Pb.surface2 else Pb.blue),
            shape = RoundedCornerShape(10.dp),
        ) {
            Text((if (on) stringResource(R.string.disconnect) else if (busy) stringResource(R.string.cancel) else stringResource(R.string.connect)).uppercase(), letterSpacing = 1.sp)
        }
    }
}

fun human(b: Long): String = when {
    b >= 1L shl 30 -> "%.2f ГБ".format(b / (1024.0 * 1024 * 1024))
    b >= 1L shl 20 -> "%.1f МБ".format(b / (1024.0 * 1024))
    b >= 1L shl 10 -> "%.0f КБ".format(b / 1024.0)
    else -> "$b Б"
}

// ---------------------------------------------------------------- app picker

@Composable
fun AppPickerDialog(onPick: (AppCatalog.Entry) -> Unit, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val all = remember { AppCatalog.apps(ctx) }
    var q by remember { mutableStateOf("") }
    val shown = remember(q, all) { if (q.isBlank()) all else all.filter { it.label.contains(q, true) || it.pkg.contains(q, true) } }
    AlertDialog(
        onDismissRequest = onDismiss,
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
        title = { Text(stringResource(R.string.pick_app)) },
        text = {
            Column {
                OutlinedTextField(q, { q = it }, placeholder = { Text(stringResource(R.string.search)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                LazyColumn(Modifier.fillMaxWidth().height(380.dp).padding(top = 8.dp)) {
                    items(shown, key = { it.pkg }) { e ->
                        Row(Modifier.fillMaxWidth().clickable { onPick(e) }.padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                            AppIcon(e.pkg, 32)
                            Column(Modifier.padding(start = 10.dp)) { Text(e.label, maxLines = 1); Text(e.pkg, style = MaterialTheme.typography.bodySmall, maxLines = 1) }
                        }
                    }
                }
            }
        },
    )
}

// ---------------------------------------------------------------- proxies

@Composable
fun ProxiesScreen(config: AppConfig, checking: Set<Int>, onCheck: (ProxyEntry) -> Unit, onCheckAll: () -> Unit) {
    val ctx = LocalContext.current
    var input by remember { mutableStateOf("") }
    var note by remember { mutableStateOf("") }
    var renaming by remember { mutableStateOf<ProxyEntry?>(null) }
    fun add(text: String) {
        val parsed = ProxyParser.parse(text)
        if (parsed == null) { note = ctx.getString(R.string.proxies_bad); return }
        val url = parsed.url()
        if (config.proxies.any { it.url == url }) { note = ctx.getString(R.string.proxies_dup); return }
        ConfigStore.update { c -> c.copy(proxies = c.proxies + ProxyEntry(c.nextProxyId, url), nextProxyId = c.nextProxyId + 1) }
        input = ""; note = ""
    }
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 16.dp), contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 24.dp)) {
        item {
            Text(stringResource(R.string.proxies_title).uppercase(), style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(top = 12.dp))
            OutlinedTextField(
                input, { input = it; note = "" },
                placeholder = { Text("socks5://user:pass@host:port", color = Pb.muted) },
                singleLine = true, modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.None),
            )
            Text(stringResource(R.string.proxies_hint), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 4.dp))
            if (note.isNotBlank()) Text(note, color = Pb.danger, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 4.dp))
            Row(Modifier.padding(top = 8.dp)) {
                Button(onClick = { add(input) }, colors = ButtonDefaults.buttonColors(containerColor = Pb.blue)) { Text(stringResource(R.string.proxies_add)) }
                Spacer(Modifier.width(8.dp))
                OutlinedButton(onClick = {
                    val cm = ctx.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                    val t = cm.primaryClip?.getItemAt(0)?.coerceToText(ctx)?.toString() ?: ""
                    input = t.trim(); if (t.isNotBlank()) add(t)
                }) { Text(stringResource(R.string.proxies_paste)) }
                Spacer(Modifier.weight(1f))
                if (config.proxies.size > 1) TextButton(onClick = onCheckAll) { Text(stringResource(R.string.check_all)) }
            }
            if (config.proxies.isEmpty()) Text(stringResource(R.string.proxies_empty), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 16.dp))
        }
        items(config.proxies, key = { it.id }) { p ->
            Column(Modifier.fillMaxWidth().padding(top = 10.dp).background(Pb.surface, RoundedCornerShape(12.dp)).border(1.dp, Pb.line, RoundedCornerShape(12.dp)).padding(12.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f).clickable { renaming = p }) {
                        Text(p.title(), fontWeight = FontWeight.SemiBold, maxLines = 1)
                        Text("${p.kind()} · ${p.hostPort()}", style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, maxLines = 1)
                    }
                    IconButton(onClick = { onCheck(p) }, enabled = p.id !in checking) { Icon(Icons.Default.Refresh, stringResource(R.string.check), tint = Pb.cyan) }
                    IconButton(onClick = { ConfigStore.update { c -> c.copy(proxies = c.proxies.filter { it.id != p.id }) } }) { Icon(Icons.Default.Delete, stringResource(R.string.delete), tint = Pb.muted) }
                }
                CheckLine(p, p.id in checking)
            }
        }
    }
    renaming?.let { p ->
        var name by remember { mutableStateOf(p.label) }
        AlertDialog(
            onDismissRequest = { renaming = null },
            title = { Text(stringResource(R.string.rename)) },
            text = { OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.label)) }, singleLine = true) },
            confirmButton = { TextButton(onClick = { ConfigStore.update { c -> c.copy(proxies = c.proxies.map { if (it.id == p.id) it.copy(label = name.trim()) else it }) }; renaming = null }) { Text(stringResource(R.string.ok)) } },
            dismissButton = { TextButton(onClick = { renaming = null }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

@Composable
fun CheckLine(p: ProxyEntry, checking: Boolean) {
    val c = p.check
    val text = when {
        checking -> stringResource(R.string.checking)
        c == null -> stringResource(R.string.not_checked)
        !c.ok -> stringResource(R.string.check_failed) + if (c.error.isNotBlank()) ": ${c.error}" else ""
        else -> listOf(flag(c.countryCode), c.country, c.city, c.ip, "${c.ms} мс").filter { it.isNotBlank() }.joinToString(" · ")
    }
    val color = when { checking -> Pb.muted; c == null -> Pb.muted; !c.ok -> Pb.danger; else -> Pb.ok }
    Row(Modifier.padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(text, style = MaterialTheme.typography.bodySmall, color = color, modifier = Modifier.weight(1f))
        if (c != null && c.ok && p.kind() == "SOCKS5") {
            val udp = c.udp
            Text(
                if (udp) stringResource(R.string.udp_yes) else stringResource(R.string.udp_no),
                fontSize = 10.sp, color = if (udp) Pb.cyan else Pb.muted,
                modifier = Modifier.border(1.dp, if (udp) Pb.cyan else Pb.line, RoundedCornerShape(6.dp)).padding(horizontal = 6.dp, vertical = 2.dp),
            )
        }
    }
}

fun flag(cc: String): String {
    if (cc.length != 2) return ""
    return cc.uppercase().map { Character.toChars(0x1F1E6 + (it - 'A')) }.joinToString("") { String(it) }
}

// ---------------------------------------------------------------- settings

@Composable
fun SettingsScreen(
    config: AppConfig, appVersion: String, coreVersion: String,
    licenseBusy: Boolean, licenseNote: String, onActivate: (String) -> Unit, onDeactivate: () -> Unit, onLogs: () -> Unit,
) {
    var key by remember { mutableStateOf(config.license.key) }
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 16.dp), contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 24.dp)) {
        item {
            Text(stringResource(R.string.settings_title).uppercase(), style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(top = 12.dp))
            SectionTitle(stringResource(R.string.settings_udp))
            Card {
                for ((mode, title, hint) in listOf(
                    Triple("auto", stringResource(R.string.udp_auto), stringResource(R.string.udp_auto_hint)),
                    Triple("block", stringResource(R.string.udp_block), ""),
                    Triple("direct", stringResource(R.string.udp_direct), stringResource(R.string.udp_direct_hint)),
                )) {
                    Row(Modifier.fillMaxWidth().clickable { ConfigStore.update { it.copy(udpMode = mode) } }.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = config.udpMode == mode, onClick = { ConfigStore.update { it.copy(udpMode = mode) } })
                        Column { Text(title); if (hint.isNotBlank()) Text(hint, style = MaterialTheme.typography.bodySmall) }
                    }
                }
            }
            SectionTitle(stringResource(R.string.settings_dns))
            Card {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) { Text(stringResource(R.string.settings_dns)); Text(stringResource(R.string.settings_dns_hint), style = MaterialTheme.typography.bodySmall) }
                    Switch(checked = config.dnsViaProxy, onCheckedChange = { on -> ConfigStore.update { it.copy(dnsViaProxy = on) } })
                }
            }
            SectionTitle(stringResource(R.string.settings_license))
            Card {
                val l = config.license
                if (LicenseApi.isValid(l)) {
                    Text(stringResource(R.string.license_active_fmt, planText(l)), color = Pb.ok)
                    Text(l.key, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, modifier = Modifier.padding(top = 4.dp))
                    OutlinedButton(onClick = onDeactivate, enabled = !licenseBusy, modifier = Modifier.padding(top = 8.dp)) { Text(stringResource(R.string.license_deactivate)) }
                } else {
                    Text(stringResource(R.string.license_none), style = MaterialTheme.typography.bodySmall)
                    OutlinedTextField(key, { key = it.uppercase() }, placeholder = { Text(stringResource(R.string.license_key_hint), color = Pb.muted) }, singleLine = true, modifier = Modifier.fillMaxWidth().padding(top = 8.dp))
                    Button(onClick = { onActivate(key) }, enabled = !licenseBusy && key.length >= 10, modifier = Modifier.padding(top = 8.dp), colors = ButtonDefaults.buttonColors(containerColor = Pb.blue)) { Text(stringResource(R.string.license_activate)) }
                }
                if (licenseNote.isNotBlank()) Text(licenseNote, color = Pb.danger, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 6.dp))
            }
            SectionTitle(stringResource(R.string.settings_about))
            Card {
                Text(stringResource(R.string.version_fmt, appVersion, coreVersion))
                Text(stringResource(R.string.site), color = Pb.cyan, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 4.dp))
                TextButton(onClick = onLogs, contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) { Text(stringResource(R.string.logs)) }
            }
        }
    }
}

@Composable
fun planText(l: LicenseState): String {
    if (l.expiresAt.isBlank()) return stringResource(R.string.license_plan_lifetime)
    val date = runCatching { DateTimeFormatter.ofPattern("d.MM.yyyy").format(Instant.parse(l.expiresAt).atZone(ZoneId.systemDefault())) }.getOrDefault(l.expiresAt)
    return stringResource(R.string.license_until_fmt, date)
}

@Composable
fun LogDialog(text: String, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.ok)) } },
        title = { Text(stringResource(R.string.logs)) },
        text = {
            LazyColumn(Modifier.fillMaxWidth().height(420.dp)) {
                item { Text(text.ifBlank { "…" }, fontFamily = FontFamily.Monospace, fontSize = 10.sp, color = Color(0xFFB8C0D8)) }
            }
        },
    )
}
