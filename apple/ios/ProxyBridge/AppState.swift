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

    init() {
        config = ConfigStore.load()
        license = Keychain.get("license").flatMap { try? JSONDecoder().decode(LicenseState.self, from: $0) } ?? LicenseState()
        Task { await setup() }
    }

    var isOn: Bool { status == .connected }
    var isBusy: Bool { busy || status == .connecting || status == .reasserting || status == .disconnecting }
    var licensed: Bool { license.isValid || LicenseAPI.allowUnlicensed }

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
        connectedSince = status == .connected ? (vpn.connection?.connectedDate ?? connectedSince ?? Date()) : nil
        if status != .connected {
            upRate = 0
            downRate = 0
            lastSample = nil
        }
        if expectConnect && old == .connecting && status == .disconnected {
            expectConnect = false
            message = tr("Не удалось подключиться через прокси. Проверь прокси во вкладке «Прокси».",
                         "Could not connect through the proxy. Check it on the Proxies tab.")
        }
        if status == .connected {
            expectConnect = false
            message = nil
        }
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
            vpn.stop()
        } else {
            await connect()
        }
    }

    func connect() async {
        message = nil
        guard licensed else {
            message = tr("Нужен ключ. Введи его в настройках: один ключ работает на ПК, Mac и телефоне.",
                         "A key is required. Enter it in Settings: one key works on PC, Mac and phone.")
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
        do {
            try await vpn.install(config)
            expectConnect = true
            try vpn.start()
        } catch {
            message = tr("Не удалось включить: ", "Could not turn on: ") + error.localizedDescription
        }
        refreshStatus()
    }

    private func reconnect() async {
        vpn.stop()
        for _ in 0..<30 {
            if vpn.status == .disconnected || vpn.status == .invalid { break }
            try? await Task.sleep(nanoseconds: 200_000_000)
        }
        await connect()
    }
}
