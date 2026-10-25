import Foundation
import Network
import NetworkExtension

enum TunnelError: LocalizedError {
    case config, tunDevice, engine(String)

    var errorDescription: String? {
        switch self {
        case .config: return "Нет настроек подключения. Открой ProxyBridge и выбери прокси."
        case .tunDevice: return "Не удалось создать сетевой интерфейс."
        case .engine(let m): return m.isEmpty ? "Не удалось запустить туннель." : m
        }
    }
}

/// Log and status lines from the core (a C callback without context, so the state is global).
private final class CoreState: @unchecked Sendable {
    var state = "starting"
    var message = ""
    var up: Int64 = 0
    var down: Int64 = 0
    let lock = NSLock()
}

private let core = CoreState()

private let coreLogger: @convention(c) (Int32, UnsafePointer<CChar>?) -> Void = { level, msg in
    guard let msg = msg else { return }
    let text = String(cString: msg)
    if level == 9 {
        // {"status":"up"|"down"|"error"|"stats", "message":..., "up":n, "down":n}
        if let d = text.data(using: .utf8), let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any] {
            core.lock.lock()
            switch o["status"] as? String ?? "" {
            case "up": core.state = "up"
            case "down": core.state = "down"
            case "error": core.state = "error"; core.message = o["message"] as? String ?? ""
            case "stats":
                core.up = (o["up"] as? NSNumber)?.int64Value ?? core.up
                core.down = (o["down"] as? NSNumber)?.int64Value ?? core.down
            default: break
            }
            core.lock.unlock()
        }
        return
    }
    if level >= 1 { NSLog("pbcore: %@", text) }
}

final class PacketTunnelProvider: NEPacketTunnelProvider {
    private var tunFd: Int32 = -1
    private let queue = DispatchQueue(label: "org.proxybridge.ios.tunnel")

    override func startTunnel(options: [String: NSObject]?, completionHandler: @escaping (Error?) -> Void) {
        guard let proto = protocolConfiguration as? NETunnelProviderProtocol,
              let data = proto.providerConfiguration?[TunnelConfig.providerKey] as? Data,
              let cfg = TunnelConfig.decode(data),
              let request = cfg.requestJSON(),
              let proxy = cfg.active else {
            completionHandler(TunnelError.config)
            return
        }
        pbSetLogger(unsafeBitCast(coreLogger, to: UInt.self))
        queue.async {
            guard self.applySettings(remote: proxy.host) else {
                completionHandler(TunnelError.tunDevice)
                return
            }
            if self.tunFd < 0 { self.tunFd = self.tunnelFileDescriptor() ?? -1 }
            guard self.tunFd >= 0 else {
                completionHandler(TunnelError.tunDevice)
                return
            }
            core.lock.lock(); core.state = "starting"; core.message = ""; core.lock.unlock()
            // The extension's own sockets never enter its tunnel, so they are not bound to an
            // interface: a bound socket would break when the phone moves between Wi-Fi and cellular.
            NSLog("pbcore: starting on fd %d", self.tunFd)
            if pbStart(request, self.tunFd, "") != 0 {
                completionHandler(TunnelError.engine(self.currentStatus().message ?? ""))
                return
            }
            // the core reports "up" once the engine runs
            let deadline = Date().addingTimeInterval(20)
            while Date() < deadline {
                let st = self.currentStatus()
                if st.state == "up" { completionHandler(nil); return }
                if st.state == "error" || st.state == "down" {
                    pbStop()
                    completionHandler(TunnelError.engine(st.message ?? ""))
                    return
                }
                Thread.sleep(forTimeInterval: 0.2)
            }
            pbStop()
            completionHandler(TunnelError.engine("Ядро не ответило вовремя."))
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason, completionHandler: @escaping () -> Void) {
        queue.async {
            pbStop()
            completionHandler()
        }
    }

    override func handleAppMessage(_ messageData: Data, completionHandler: ((Data?) -> Void)?) {
        completionHandler?(try? JSONEncoder().encode(currentStatus()))
    }

    private func currentStatus() -> TunnelStatus {
        core.lock.lock(); defer { core.lock.unlock() }
        return TunnelStatus(state: core.state, message: core.message.isEmpty ? nil : core.message, up: core.up, down: core.down)
    }

    /// Network settings: the tunnel owns the default route and DNS; the proxy itself stays reachable
    /// through the core's own sockets, which are bound to the physical interface.
    private func applySettings(remote: String) -> Bool {
        let settings = NEPacketTunnelNetworkSettings(tunnelRemoteAddress: remote.isEmpty ? "127.0.0.1" : remote)
        let v4 = NEIPv4Settings(addresses: ["10.255.0.2"], subnetMasks: ["255.255.255.255"])
        v4.includedRoutes = [NEIPv4Route.default()]
        // the proxy itself stays outside, so the app's own proxy checks do not loop through it
        if IPv4Address(remote) != nil {
            v4.excludedRoutes = [NEIPv4Route(destinationAddress: remote, subnetMask: "255.255.255.255")]
        }
        settings.ipv4Settings = v4
        // IPv6 also enters the tunnel, so nothing bypasses the proxy; the core answers it with resets.
        let v6 = NEIPv6Settings(addresses: ["fd50:726f:7879::2"], networkPrefixLengths: [128])
        v6.includedRoutes = [NEIPv6Route.default()]
        settings.ipv6Settings = v6
        let dns = NEDNSSettings(servers: ["1.1.1.1"])
        dns.matchDomains = [""]
        settings.dnsSettings = dns
        settings.mtu = 1500
        let done = DispatchSemaphore(value: 0)
        var ok = false
        setTunnelNetworkSettings(settings) { error in
            if let error = error { NSLog("pbcore: network settings: %@", error.localizedDescription) }
            ok = error == nil
            done.signal()
        }
        return done.wait(timeout: .now() + 15) == .success && ok
    }

    /// The utun descriptor the system created for this extension.
    private func tunnelFileDescriptor() -> Int32? {
        var ctlInfo = ctl_info()
        withUnsafeMutablePointer(to: &ctlInfo.ctl_name) {
            $0.withMemoryRebound(to: CChar.self, capacity: MemoryLayout.size(ofValue: $0.pointee)) {
                _ = strcpy($0, "com.apple.net.utun_control")
            }
        }
        for fd: Int32 in 0...1024 {
            var addr = sockaddr_ctl()
            var ret: Int32 = -1
            var len = socklen_t(MemoryLayout.size(ofValue: addr))
            withUnsafeMutablePointer(to: &addr) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    ret = getpeername(fd, $0, &len)
                }
            }
            if ret != 0 || addr.sc_family != AF_SYSTEM { continue }
            if ctlInfo.ctl_id == 0 {
                ret = ioctl(fd, CTLIOCGINFO, &ctlInfo)
                if ret != 0 { continue }
            }
            if addr.sc_id == ctlInfo.ctl_id { return fd }
        }
        return nil
    }
}
