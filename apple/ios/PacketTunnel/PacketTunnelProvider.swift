import Darwin
import Foundation
import Network
import NetworkExtension

enum TunnelError: LocalizedError {
    case config, settings(String), tunDevice, engine(String)

    /// The app receives the error through the system, which keeps only NSError fields:
    /// the text goes into NSLocalizedDescriptionKey so it arrives intact.
    var nsError: NSError {
        NSError(domain: "org.proxybridge.tunnel", code: 1, userInfo: [NSLocalizedDescriptionKey: errorDescription ?? "error"])
    }

    var errorDescription: String? {
        let ru = (Locale.preferredLanguages.first ?? "ru").hasPrefix("ru")
        switch self {
        case .config: return ru ? "Нет настроек подключения. Открой ProxyBridge и выбери прокси." : "No connection settings. Open ProxyBridge and pick a proxy."
        case .settings(let m): return (ru ? "iOS не приняла настройки сети: " : "iOS rejected the network settings: ") + m
        case .tunDevice: return ru ? "Не удалось найти сетевой интерфейс туннеля." : "The tunnel network interface was not found."
        case .engine(let m): return m.isEmpty ? (ru ? "Не удалось запустить туннель." : "The tunnel did not start.") : m
        }
    }
}

private let tlog = SharedLog.tunnel

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
        guard let d = text.data(using: .utf8), let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return }
        let st = o["status"] as? String ?? ""
        core.lock.lock()
        switch st {
        case "up": core.state = "up"
        case "down": core.state = "down"
        case "error": core.state = "error"; core.message = o["message"] as? String ?? ""
        case "stats":
            core.up = (o["up"] as? NSNumber)?.int64Value ?? core.up
            core.down = (o["down"] as? NSNumber)?.int64Value ?? core.down
        default: break
        }
        core.lock.unlock()
        if st != "stats" { tlog.add("status: " + text, now: st == "error" || st == "down") }
        return
    }
    if level >= 1 {
        tlog.add(["D", "I", "W", "E"][Int(max(0, min(3, level)))] + " " + text, now: level >= 3)
    }
}

/// Resident memory of this process (what the system compares with the extension limit).
private func footprintMB() -> Double {
    var info = task_vm_info_data_t()
    var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
    let kr = withUnsafeMutablePointer(to: &info) {
        $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
            task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
        }
    }
    return kr == KERN_SUCCESS ? Double(info.phys_footprint) / 1_048_576 : -1
}

final class PacketTunnelProvider: NEPacketTunnelProvider {
    private var tunFd: Int32 = -1
    private let queue = DispatchQueue(label: "org.proxybridge.ios.tunnel")
    private var memTimer: DispatchSourceTimer?

    override func startTunnel(options: [String: NSObject]?, completionHandler: @escaping (Error?) -> Void) {
        let version = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "?"
        tlog.begin("start, build \(version), iOS \(ProcessInfo.processInfo.operatingSystemVersionString)")
        pickUpCrash()
        guard let proto = protocolConfiguration as? NETunnelProviderProtocol,
              let data = proto.providerConfiguration?[TunnelConfig.providerKey] as? Data,
              let cfg = TunnelConfig.decode(data),
              let request = cfg.requestJSON(),
              let proxy = cfg.active else {
            tlog.add("E no configuration in the VPN profile", now: true)
            completionHandler(TunnelError.config.nsError)
            return
        }
        tlog.add("I proxy \(proxy.kind) \(proxy.hostPort), udp \(cfg.udpMode), dns via proxy \(cfg.dnsViaProxy)")
        pbSetLogger(unsafeBitCast(coreLogger, to: UInt.self))
        queue.async {
            let finish: (Error?) -> Void = { err in
                if let err = err {
                    tlog.add("E start failed: " + err.localizedDescription, now: true)
                } else {
                    tlog.add(String(format: "I connected, memory %.1f MB", footprintMB()), now: true)
                    self.startMemoryLog()
                }
                if let te = err as? TunnelError {
                    completionHandler(te.nsError)
                } else {
                    completionHandler(err)
                }
            }
            // iOS wants an IP address here, not a name: resolve the proxy host first.
            let remote = Self.resolveIPv4(proxy.host)
            tlog.add("I proxy address \(remote ?? "not resolved")")
            if let err = self.applySettings(remote: remote) {
                finish(TunnelError.settings(err))
                return
            }
            tlog.add("I network settings applied")
            if self.tunFd < 0 { self.tunFd = self.tunnelFileDescriptor() ?? -1 }
            guard self.tunFd >= 0 else {
                finish(TunnelError.tunDevice)
                return
            }
            core.lock.lock(); core.state = "starting"; core.message = ""; core.lock.unlock()
            // The extension's own sockets never enter its tunnel, so they are not bound to an
            // interface: a bound socket would break when the phone moves between Wi-Fi and cellular.
            tlog.add(String(format: "I starting the core on fd %d, memory %.1f MB", self.tunFd, footprintMB()))
            if pbStart(request, self.tunFd, "") != 0 {
                finish(TunnelError.engine(self.currentStatus().message ?? ""))
                return
            }
            // the core reports "up" once the engine runs
            let deadline = Date().addingTimeInterval(20)
            while Date() < deadline {
                let st = self.currentStatus()
                if st.state == "up" { finish(nil); return }
                if st.state == "error" || st.state == "down" {
                    pbStop()
                    finish(TunnelError.engine(st.message ?? ""))
                    return
                }
                Thread.sleep(forTimeInterval: 0.2)
            }
            pbStop()
            finish(TunnelError.engine("The core did not report readiness in 20 s."))
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason, completionHandler: @escaping () -> Void) {
        tlog.add("I stop requested, reason \(reason.rawValue)", now: true)
        memTimer?.cancel()
        memTimer = nil
        queue.async {
            pbStop()
            tlog.add("I stopped", now: true)
            completionHandler()
        }
    }

    override func handleAppMessage(_ messageData: Data, completionHandler: ((Data?) -> Void)?) {
        completionHandler?(try? JSONEncoder().encode(currentStatus()))
    }

    override func sleep(completionHandler: @escaping () -> Void) {
        tlog.add("I device sleep")
        completionHandler()
    }

    override func wake() {
        tlog.add("I device wake")
    }

    private func currentStatus() -> TunnelStatus {
        core.lock.lock(); defer { core.lock.unlock() }
        return TunnelStatus(state: core.state, message: core.message.isEmpty ? nil : core.message, up: core.up, down: core.down)
    }

    /// Memory and traffic every 15 s: the extension is killed above about 50 MB.
    private func startMemoryLog() {
        let t = DispatchSource.makeTimerSource(queue: DispatchQueue.global(qos: .utility))
        t.schedule(deadline: .now() + 15, repeating: 15)
        t.setEventHandler { [weak self] in
            guard let self = self else { return }
            let st = self.currentStatus()
            tlog.add(String(format: "I memory %.1f MB, up %lld KB, down %lld KB", footprintMB(), st.up / 1024, st.down / 1024))
        }
        t.resume()
        memTimer = t
    }

    /// A Go crash from the previous run (written by the core to a file in this extension's tmp).
    private func pickUpCrash() {
        let path = (NSTemporaryDirectory() as NSString).appendingPathComponent("pbcore-crash.txt")
        guard let text = try? String(contentsOfFile: path, encoding: .utf8), !text.isEmpty else { return }
        try? FileManager.default.removeItem(atPath: path)
        tlog.add("E previous run crashed:\n" + String(text.prefix(6000)), now: true)
    }

    /// Network settings: the tunnel owns the default route and DNS. Returns an error text or nil.
    private func applySettings(remote: String?) -> String? {
        let settings = NEPacketTunnelNetworkSettings(tunnelRemoteAddress: remote ?? "127.0.0.1")
        let v4 = NEIPv4Settings(addresses: ["10.255.0.2"], subnetMasks: ["255.255.255.255"])
        v4.includedRoutes = [NEIPv4Route.default()]
        // the proxy itself stays outside, so the app's own proxy checks do not loop through it
        if let remote = remote {
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
        var failure: String?
        setTunnelNetworkSettings(settings) { error in
            if let error = error { failure = error.localizedDescription }
            done.signal()
        }
        if done.wait(timeout: .now() + 15) != .success { return "timeout" }
        return failure
    }

    /// The first IPv4 address of a host name (an IPv4 literal is returned as is).
    static func resolveIPv4(_ host: String) -> String? {
        if host.isEmpty { return nil }
        if IPv4Address(host) != nil { return host }
        var hints = addrinfo()
        hints.ai_family = AF_INET
        hints.ai_socktype = SOCK_STREAM
        var res: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &res) == 0, let first = res else { return nil }
        defer { freeaddrinfo(res) }
        var ptr: UnsafeMutablePointer<addrinfo>? = first
        while let ai = ptr {
            if ai.pointee.ai_family == AF_INET, let sa = ai.pointee.ai_addr {
                var addr = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr }
                var buf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
                if inet_ntop(AF_INET, &addr, &buf, socklen_t(INET_ADDRSTRLEN)) != nil { return String(cString: buf) }
            }
            ptr = ai.pointee.ai_next
        }
        return nil
    }

    /// The utun descriptor the system created for this extension.
    private func tunnelFileDescriptor() -> Int32? {
        var ctlInfo = ctl_info()
        withUnsafeMutablePointer(to: &ctlInfo.ctl_name) {
            $0.withMemoryRebound(to: CChar.self, capacity: MemoryLayout.size(ofValue: $0.pointee)) {
                _ = strcpy($0, "com.apple.net.utun_control")
            }
        }
        var seen = 0
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
            seen += 1
            if ctlInfo.ctl_id == 0 {
                ret = ioctl(fd, CTLIOCGINFO, &ctlInfo)
                if ret != 0 {
                    tlog.add("W CTLIOCGINFO failed on fd \(fd): errno \(errno)")
                    continue
                }
            }
            if addr.sc_id == ctlInfo.ctl_id { return fd }
        }
        tlog.add("E no utun descriptor among \(seen) system sockets", now: true)
        return nil
    }
}
