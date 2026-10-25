import Foundation

/// A proxy the person added. The check result is what the Proxies screen shows.
struct ProxyItem: Codable, Identifiable, Hashable {
    var id: Int
    var url: String
    var label: String = ""
    var check: CheckInfo?

    var title: String { label.isEmpty ? hostPort : label }
    var hostPort: String {
        guard let u = URL(string: url), let h = u.host else { return url }
        return "\(h):\(u.port ?? 0)"
    }
    var kind: String { url.hasPrefix("http") ? "HTTP" : "SOCKS5" }
    var host: String { URL(string: url)?.host ?? "" }
}

struct CheckInfo: Codable, Hashable {
    var ok: Bool
    var ip: String?
    var country: String?
    var country_code: String?
    var city: String?
    var ms: Int
    var udp: Bool
    var error: String?
}

/// Everything the tunnel needs. The app stores it in providerConfiguration; the extension
/// reads it at start. iOS has no per-app rules for consumer apps, so the whole phone goes
/// through the active proxy.
struct TunnelConfig: Codable {
    var proxies: [ProxyItem] = []
    var activeProxyId: Int = 0
    var udpMode: String = "auto"      // auto | block | direct
    var dnsViaProxy: Bool = true
    var nextId: Int = 1

    static let providerKey = "config"

    var active: ProxyItem? { proxies.first { $0.id == activeProxyId } ?? proxies.first }

    func encoded() -> Data? { try? JSONEncoder().encode(self) }
    static func decode(_ data: Data) -> TunnelConfig? { try? JSONDecoder().decode(TunnelConfig.self, from: data) }

    /// The start request of the core (core-go/session.go): one catch-all rule to the active proxy.
    func requestJSON() -> String? {
        guard let p = active else { return nil }
        let udp = udpMode == "block" ? "block" : (udpMode == "direct" ? "direct" : "proxy")
        let req: [String: Any] = [
            "proxies": [["id": p.id, "url": p.url]],
            "rules": [["process": "*", "action": "proxy", "proxy": p.id]],
            "udp": udp,
            "dns_via_proxy": dnsViaProxy,
            "dns": "1.1.1.1",
            "dns_proxy": p.id,
        ]
        guard let data = try? JSONSerialization.data(withJSONObject: req) else { return nil }
        return String(data: data, encoding: .utf8)
    }
}

/// What the extension answers to the app's status request.
struct TunnelStatus: Codable {
    var state: String      // starting | up | down | error
    var message: String?
    var up: Int64
    var down: Int64
}

enum TunnelMessage {
    static let status: UInt8 = 1
}
