import CryptoKit
import Foundation
import NetworkExtension
import Security
import UIKit

/// Russian when the phone prefers Russian, English otherwise.
let isRussian = (Locale.preferredLanguages.first ?? "ru").hasPrefix("ru")

func tr(_ ru: String, _ en: String) -> String { isRussian ? ru : en }

// MARK: - storage

enum Keychain {
    private static let service = "org.proxybridge.ios"

    static func get(_ account: String) -> Data? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true,
                                kSecMatchLimit as String: kSecMatchLimitOne]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess else { return nil }
        return out as? Data
    }

    static func set(_ account: String, _ data: Data?) {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                   kSecAttrAccount as String: account]
        SecItemDelete(base as CFDictionary)
        guard let data = data else { return }
        var add = base
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(add as CFDictionary, nil)
    }
}

enum ConfigStore {
    private static let key = "config"

    static func load() -> TunnelConfig {
        guard let d = UserDefaults.standard.data(forKey: key), let c = TunnelConfig.decode(d) else { return TunnelConfig() }
        return c
    }

    static func save(_ c: TunnelConfig) {
        UserDefaults.standard.set(c.encoded(), forKey: key)
    }
}

// MARK: - license (the same keys and API as the desktop and Android apps)

struct LicenseState: Codable {
    var key: String = ""
    var plan: String = ""
    var expiresAt: String = ""
    var lastCheck: Date?

    var isEmpty: Bool { key.isEmpty }

    var expiryDate: Date? {
        guard !expiresAt.isEmpty else { return nil }
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = f.date(from: expiresAt) { return d }
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: expiresAt)
    }

    /// A key, not expired, and a server check within the last 7 days.
    var isValid: Bool {
        guard !key.isEmpty, let last = lastCheck, Date().timeIntervalSince(last) < 7 * 86_400 else { return false }
        if !expiresAt.isEmpty {
            guard let exp = expiryDate, exp > Date() else { return false }
        }
        return true
    }
}

enum LicenseAPI {
    static let base = "https://www.proxybridge.org"

    /// Connecting needs an activated key.
    static let allowUnlicensed = false

    enum Outcome {
        case ok(plan: String, expiresAt: String)
        case error(String) // not_found, expired, device_limit, revoked, network, other
    }

    static var hwid: String {
        var seed = Keychain.get("device-seed").flatMap { String(data: $0, encoding: .utf8) }
        if seed == nil {
            let fresh = UUID().uuidString
            Keychain.set("device-seed", Data(fresh.utf8))
            seed = fresh
        }
        let digest = SHA256.hash(data: Data(("ios:" + (seed ?? "") + ":org.proxybridge.ios").utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    static var appVersion: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0" }

    /// Hardware identifier such as iPhone16,2 (the marketing name is not available to apps).
    static var deviceModel: String {
        var info = utsname()
        uname(&info)
        let machine = withUnsafeBytes(of: &info.machine) { raw in
            String(decoding: raw.prefix(while: { $0 != 0 }), as: UTF8.self)
        }
        return machine.isEmpty ? UIDevice.current.model : machine
    }

    static func activate(_ key: String) async -> Outcome { await call("activate", key) }
    static func verify(_ key: String) async -> Outcome { await call("verify", key) }

    static func deactivate(_ key: String) async {
        _ = await post("/api/license/deactivate", ["key": key, "hwid": hwid])
    }

    private static func call(_ op: String, _ key: String) async -> Outcome {
        let body: [String: Any] = ["key": key.trimmingCharacters(in: .whitespaces).uppercased(), "hwid": hwid, "app_version": appVersion,
                                   "platform": "ios", "os": "iOS " + UIDevice.current.systemVersion, "model": deviceModel]
        guard let (code, json) = await post("/api/license/\(op)", body) else { return .error("network") }
        if code == 200, json["ok"] as? Bool == true {
            return .ok(plan: json["plan"] as? String ?? "", expiresAt: json["expires_at"] as? String ?? "")
        }
        return .error((json["error"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "other")
    }

    private static func post(_ path: String, _ body: [String: Any]) async -> (Int, [String: Any])? {
        guard let url = URL(string: base + path) else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.timeoutInterval = 15
        req.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type")
        req.setValue("ProxyBridge-iOS", forHTTPHeaderField: "User-Agent")
        req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        do {
            let (data, resp) = try await URLSession.shared.data(for: req)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
            return (code, json)
        } catch {
            return nil
        }
    }

    static func message(_ code: String) -> String {
        switch code {
        case "not_found": return tr("Такого ключа нет.", "No such key.")
        case "expired": return tr("Срок ключа истёк.", "The key has expired.")
        case "device_limit": return tr("Ключ уже активирован на максимальном числе устройств.", "The key is already active on the maximum number of devices.")
        case "revoked": return tr("Ключ отозван.", "The key was revoked.")
        case "network": return tr("Нет связи с сервером. Проверь интернет и попробуй ещё раз.", "No connection to the server. Check the internet and try again.")
        default: return tr("Не удалось активировать: ", "Activation failed: ") + code
        }
    }
}

// MARK: - proxies

/// Accepts the same spellings as the desktop app: host:port, host:port:user:pass,
/// user:pass@host:port, host:port@user:pass and socks5:// or http:// URLs.
enum ProxyParser {
    static func parse(_ input: String) -> String? {
        var s = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.isEmpty || s.contains(" ") || s.contains("\t") { return nil }
        var type = "socks5"
        if let r = s.range(of: "://") {
            switch s[..<r.lowerBound].lowercased() {
            case "http", "https": type = "http"
            case "socks5", "socks5h", "socks": type = "socks5"
            default: return nil
            }
            s = String(s[r.upperBound...])
            while s.hasSuffix("/") { s.removeLast() }
        }
        var host = "", port = 0, user = "", pass = ""
        if let at = s.range(of: "@", options: .backwards) {
            var left = String(s[..<at.lowerBound]), right = String(s[at.upperBound...])
            var hp = splitHostPort(right)
            if hp == nil {
                guard let swapped = splitHostPort(left) else { return nil }
                hp = swapped
                swap(&left, &right)
            }
            (host, port) = hp!
            if let c = left.firstIndex(of: ":") {
                user = String(left[..<c]).removingPercentEncoding ?? String(left[..<c])
                let p = String(left[left.index(after: c)...])
                pass = p.removingPercentEncoding ?? p
            } else {
                user = left.removingPercentEncoding ?? left
            }
        } else if s.hasPrefix("[") {
            guard let hp = splitHostPort(s) else { return nil }
            (host, port) = hp
        } else {
            let parts = s.components(separatedBy: ":")
            guard parts.count >= 2, let p = Int(parts[1]), (1...65535).contains(p), !parts[0].isEmpty else { return nil }
            host = parts[0]; port = p
            if parts.count == 3 { pass = parts[2] }
            if parts.count >= 4 { user = parts[2]; pass = parts[3...].joined(separator: ":") }
        }
        let allowed = CharacterSet.urlUserAllowed.subtracting(CharacterSet(charactersIn: ":@/?#[]"))
        let enc = { (v: String) in v.addingPercentEncoding(withAllowedCharacters: allowed) ?? v }
        let auth = user.isEmpty && pass.isEmpty ? "" : "\(enc(user)):\(enc(pass))@"
        let h = host.contains(":") ? "[\(host)]" : host
        return "\(type)://\(auth)\(h):\(port)"
    }

    private static func splitHostPort(_ raw: String) -> (String, Int)? {
        let t = raw.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("[") {
            guard let end = t.firstIndex(of: "]") else { return nil }
            let rest = t[t.index(after: end)...]
            guard rest.hasPrefix(":"), let p = Int(rest.dropFirst()), (1...65535).contains(p) else { return nil }
            return (String(t[t.index(after: t.startIndex)..<end]), p)
        }
        let parts = t.components(separatedBy: ":")
        guard parts.count == 2, !parts[0].isEmpty, let p = Int(parts[1]), (1...65535).contains(p) else { return nil }
        return (parts[0], p)
    }
}

/// The proxy check of the core (the same as on the desktop): IP, country and UDP support.
enum ProxyCheck {
    static func run(_ url: String) async -> CheckInfo {
        await Task.detached(priority: .userInitiated) {
            guard let raw = pbCheckProxy(url, isRussian ? "ru" : "en") else {
                return CheckInfo(ok: false, ms: 0, udp: false, error: "core")
            }
            defer { free(raw) }
            let data = Data(String(cString: raw).utf8)
            return (try? JSONDecoder().decode(CheckInfo.self, from: data)) ?? CheckInfo(ok: false, ms: 0, udp: false, error: "bad answer")
        }.value
    }
}

// MARK: - the VPN profile in iOS settings

final class VPNController {
    private(set) var manager: NETunnelProviderManager?

    var connection: NEVPNConnection? { manager?.connection }
    var status: NEVPNStatus { manager?.connection.status ?? .invalid }

    func load() async {
        let all = (try? await NETunnelProviderManager.loadAllFromPreferences()) ?? []
        manager = all.first
    }

    func install(_ cfg: TunnelConfig) async throws {
        let m = manager ?? NETunnelProviderManager()
        let proto = NETunnelProviderProtocol()
        proto.providerBundleIdentifier = (Bundle.main.bundleIdentifier ?? "org.proxybridge.ios") + ".tunnel"
        proto.serverAddress = cfg.active?.hostPort ?? "ProxyBridge"
        proto.providerConfiguration = [TunnelConfig.providerKey: cfg.encoded() ?? Data()]
        proto.disconnectOnSleep = false
        m.protocolConfiguration = proto
        m.localizedDescription = "ProxyBridge"
        m.isEnabled = true
        try await m.saveToPreferences()
        try await m.loadFromPreferences()
        manager = m
    }

    func start() throws { try manager?.connection.startVPNTunnel() }

    func stop() { manager?.connection.stopVPNTunnel() }

    func tunnelStatus() async -> TunnelStatus? {
        guard let session = manager?.connection as? NETunnelProviderSession, session.status == .connected else { return nil }
        return await withCheckedContinuation { (cont: CheckedContinuation<TunnelStatus?, Never>) in
            do {
                try session.sendProviderMessage(Data([TunnelMessage.status])) { data in
                    cont.resume(returning: data.flatMap { try? JSONDecoder().decode(TunnelStatus.self, from: $0) })
                }
            } catch {
                cont.resume(returning: nil)
            }
        }
    }
}
