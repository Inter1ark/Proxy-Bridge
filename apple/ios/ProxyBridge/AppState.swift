import Foundation
import NetworkExtension
import SwiftUI

@MainActor
final class AppState: ObservableObject {
    @Published var config: TunnelConfig
    @Published var license: LicenseState
    @Published var status: NEVPNStatus = .invalid
    @Published var connectedSince: Date?
    @Published var upRate: Double = 0
    @Published var downRate: Double = 0
    @Published var message: String?
    @Published var busy = false
    @Published var checking: Set<Int> = []
    @Published var licenseBusy = false
    @Published var licenseNote: String?
    @Published var tab = 0

    private let vpn = VPNController()
    private var observer: NSObjectProtocol?
    private var lastSample: (date: Date, up: Int64, down: Int64)?
    private var expectConnect = false
    private var sawConnecting = false
    private var userStopped = false
    private let applog = SharedLog.app

    init() {
        config = ConfigStore.load()
        license = Keychain.get("license").flatMap { try? JSONDecoder().decode(LicenseState.self, from: $0) } ?? LicenseState()
        Task { await setup() }
    }

    var isOn: Bool { status == .connected }
    var isBusy: Bool { busy || status == .connecting || status == .reasserting || status == .disconnecting }
    var licensed: Bool { license.isValid || LicenseAPI.allowUnlicensed }
    var needsKey: Bool { license.isEmpty && !LicenseAPI.allowUnlicensed }

    private func setup() async {
        await vpn.load()
        refreshStatus()
        observer = NotificationCenter.default.addObserver(forName: .NEVPNStatusDidChange, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.refreshStatus() }
        }
        await verifyLicense()
    }

    func refreshStatus() {
        let old = status
        status = vpn.status
        if status != old { applog.add("status \(old.name) -> \(status.name)") }
        connectedSince = status == .connected ? (vpn.connection?.connectedDate ?? connectedSince ?? Date()) : nil
        if status != .connected {
            upRate = 0
            downRate = 0
            lastSample = nil
        }
        if expectConnect && (status == .connecting || status == .reasserting) { sawConnecting = true }
        if status == .connected {
            expectConnect = false
            sawConnecting = false
            message = nil
        }
        if status == .disconnected && old != .disconnected {
            if expectConnect && sawConnecting {
                expectConnect = false
                sawConnecting = false
                Task { await explainDisconnect(tr("Не удалось подключиться.", "Could not connect.")) }
            } else if old == .connected || old == .reasserting, !userStopped {
                Task { await explainDisconnect(tr("Соединение прервалось.", "The connection dropped.")) }
            }
            userStopped = false
        }
    }

    /// Shows why iOS stopped the tunnel (the extension's error comes back through the system).
    private func explainDisconnect(_ head: String) async {
        var reason = ""
        if let conn = vpn.connection {
            let err: Error? = await withCheckedContinuation { (c: CheckedContinuation<Error?, Never>) in
                conn.fetchLastDisconnectError { c.resume(returning: $0) }
            }
            if let e = err as NSError? {
                let under = e.userInfo[NSUnderlyingErrorKey] as? NSError
                reason = under?.localizedDescription ?? e.localizedDescription
                applog.add("disconnect error: \(e.domain) \(e.code) \(e.localizedDescription)" + (under.map { " / \($0.domain) \($0.code) \($0.localizedDescription)" } ?? ""), now: true)
            } else {
                applog.add("disconnect without an error from the system", now: true)
            }
        }
        message = head + (reason.isEmpty ? "" : " " + reason) + " " +
            tr("Подробности в Настройках, раздел «Журнал».", "Details: Settings, Log.")
    }

    /// Traffic counters from the extension, turned into speeds.
    func pollTunnel() async {
        guard status == .connected, let st = await vpn.tunnelStatus() else { return }
        let now = Date()
        if let last = lastSample {
            let dt = now.timeIntervalSince(last.date)
            if dt > 0 {
                upRate = max(0, Double(st.up - last.up) / dt)
                downRate = max(0, Double(st.down - last.down) / dt)
            }
        }
        lastSample = (now, st.up, st.down)
        if st.state == "error", let m = st.message { message = m }
    }

    // MARK: - proxies

    private func save() { ConfigStore.save(config) }

    /// Adds one proxy per line; returns how many lines were not understood.
    func addProxies(_ text: String) -> Int {
        var bad = 0
        var firstNew: Int?
        for line in text.split(whereSeparator: \.isNewline) {
            let raw = String(line).trimmingCharacters(in: .whitespaces)
            if raw.isEmpty { continue }
            guard let url = ProxyParser.parse(raw) else { bad += 1; continue }
            if config.proxies.contains(where: { $0.url == url }) { continue }
            let item = ProxyItem(id: config.nextId, url: url)
            config.nextId += 1
            config.proxies.append(item)
            if firstNew == nil { firstNew = item.id }
        }
        if !config.proxies.contains(where: { $0.id == config.activeProxyId }), let id = firstNew { config.activeProxyId = id }
        save()
        if let id = firstNew { Task { await check(id) } }
        return bad
    }

    func remove(_ id: Int) {
        config.proxies.removeAll { $0.id == id }
        if config.activeProxyId == id { config.activeProxyId = config.proxies.first?.id ?? 0 }
        save()
    }

    func rename(_ id: Int, _ label: String) {
        guard let i = config.proxies.firstIndex(where: { $0.id == id }) else { return }
        config.proxies[i].label = label.trimmingCharacters(in: .whitespaces)
        save()
    }

    func select(_ id: Int) {
        guard config.activeProxyId != id else { return }
        config.activeProxyId = id
        save()
        if status == .connected || status == .connecting { Task { await reconnect() } }
    }

    func check(_ id: Int) async {
        guard let p = config.proxies.first(where: { $0.id == id }), !checking.contains(id) else { return }
        checking.insert(id)
        let result = await ProxyCheck.run(p.url)
        checking.remove(id)
        if let i = config.proxies.firstIndex(where: { $0.id == id }) {
            config.proxies[i].check = result
            save()
        }
    }

    func checkAll() async {
        await withTaskGroup(of: Void.self) { g in
            for p in config.proxies { g.addTask { await self.check(p.id) } }
        }
    }

    func setUDP(_ mode: String) {
        config.udpMode = mode
        save()
        if isOn { Task { await reconnect() } }
    }

    func setDNSViaProxy(_ on: Bool) {
        config.dnsViaProxy = on
        save()
        if isOn { Task { await reconnect() } }
    }

    // MARK: - license

    private func storeLicense(_ l: LicenseState) {
        license = l
        let data: Data? = l.isEmpty ? nil : (try? JSONEncoder().encode(l))
        Keychain.set("license", data)
    }

    func activate(_ key: String) async {
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        guard !k.isEmpty else { return }
        licenseBusy = true
        licenseNote = nil
        switch await LicenseAPI.activate(k) {
        case .ok(let plan, let exp):
            storeLicense(LicenseState(key: k, plan: plan, expiresAt: exp, lastCheck: Date()))
        case .error(let code):
            licenseNote = LicenseAPI.message(code)
        }
        licenseBusy = false
    }

    func verifyLicense() async {
        guard !license.isEmpty else { return }
        switch await LicenseAPI.verify(license.key) {
        case .ok(let plan, let exp):
            storeLicense(LicenseState(key: license.key, plan: plan, expiresAt: exp, lastCheck: Date()))
        case .error(let code):
            if ["not_found", "expired", "revoked", "device_limit"].contains(code) {
                licenseNote = LicenseAPI.message(code)
                storeLicense(LicenseState())
            }
        }
    }

    /// Re-checks the key at most once a day when the app comes to the foreground.
    func verifyLicenseIfStale() async {
        guard !license.isEmpty else { return }
        if let last = license.lastCheck, Date().timeIntervalSince(last) < 86_400 { return }
        await verifyLicense()
    }

    func deactivate() async {
        licenseBusy = true
        await LicenseAPI.deactivate(license.key)
        storeLicense(LicenseState())
        licenseBusy = false
    }

    // MARK: - connection

    func toggle() async {
        if status == .connected || status == .connecting || status == .reasserting {
            expectConnect = false
            userStopped = true
            applog.add("stop pressed")
            vpn.stop()
        } else {
            await connect()
        }
    }

    func connect() async {
        message = nil
        guard licensed else {
            message = license.isEmpty
                ? tr("Нужен ключ. Введи его в настройках: один ключ работает на ПК, Mac и телефоне.",
                     "A key is required. Enter it in Settings: one key works on PC, Mac and phone.")
                : tr("Ключ не подтверждён. Подключись к интернету без VPN и открой приложение снова, или проверь срок ключа в настройках.",
                     "The key is not confirmed. Connect to the internet without VPN and reopen the app, or check the key in Settings.")
            tab = 2
            return
        }
        guard config.active != nil else {
            message = tr("Добавь прокси во вкладке «Прокси».", "Add a proxy on the Proxies tab.")
            tab = 1
            return
        }
        busy = true
        defer { busy = false }
        applog.add("connect: \(config.active?.kind ?? "?") \(config.active?.hostPort ?? "?"), udp \(config.udpMode)")
        do {
            try await vpn.install(config)
            applog.add("VPN profile saved")
            expectConnect = true
            sawConnecting = false
            userStopped = false
            try vpn.start()
            applog.add("start requested")
        } catch {
            applog.add("connect failed: \(error.localizedDescription)", now: true)
            message = tr("Не удалось включить: ", "Could not turn on: ") + error.localizedDescription
        }
        refreshStatus()
    }

    private func reconnect() async {
        userStopped = true
        vpn.stop()
        for _ in 0..<30 {
            if vpn.status == .disconnected || vpn.status == .invalid { break }
            try? await Task.sleep(nanoseconds: 200_000_000)
        }
        await connect()
    }
}

extension NEVPNStatus {
    var name: String {
        switch self {
        case .invalid: return "invalid"
        case .disconnected: return "disconnected"
        case .connecting: return "connecting"
        case .connected: return "connected"
        case .reasserting: return "reasserting"
        case .disconnecting: return "disconnecting"
        @unknown default: return "unknown"
        }
    }
}
