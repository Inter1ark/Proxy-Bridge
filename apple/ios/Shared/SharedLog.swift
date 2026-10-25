import Foundation
import Security

/// Small logs shared by the app and the tunnel extension. Each lives in a keychain item of a
/// shared access group (no App Group needed), so the app can show what happened in the
/// extension even after the system has stopped it. Each process writes only its own log.
final class SharedLog: @unchecked Sendable {
    static let tunnel = SharedLog(account: "tunnel-log")
    static let app = SharedLog(account: "app-log")

    private let account: String

    init(account: String) { self.account = account }

    private let maxBytes = 48_000
    private let lock = NSLock()
    private var lines: [String] = []
    private var loaded = false
    private var dirty = false
    private var flushScheduled = false
    private let queue = DispatchQueue(label: "org.proxybridge.log")
    private let stamp: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "dd.MM HH:mm:ss"
        return f
    }()

    private var group: String? {
        guard let prefix = Bundle.main.object(forInfoDictionaryKey: "AppIdentifierPrefix") as? String, !prefix.isEmpty else { return nil }
        return prefix + "org.proxybridge.shared"
    }

    private func query() -> [String: Any] {
        var q: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                kSecAttrService as String: "org.proxybridge.log",
                                kSecAttrAccount as String: account]
        if let g = group { q[kSecAttrAccessGroup as String] = g }
        return q
    }

    /// Starts a fresh log for a new connection attempt, keeping the tail of the previous one.
    func begin(_ title: String) {
        lock.lock()
        loadLocked()
        lines = Array(lines.suffix(60))
        lines.append("")
        lines.append("===== " + title + " =====")
        dirty = true
        lock.unlock()
        flush()
    }

    func add(_ text: String, now: Bool = false) {
        let line = stamp.string(from: Date()) + " " + text
        lock.lock()
        loadLocked()
        lines.append(line)
        var size = lines.reduce(0) { $0 + $1.utf8.count + 1 }
        while size > maxBytes, !lines.isEmpty {
            size -= lines.removeFirst().utf8.count + 1
        }
        dirty = true
        let schedule = !flushScheduled && !now
        if schedule { flushScheduled = true }
        lock.unlock()
        if now {
            flush()
        } else if schedule {
            queue.asyncAfter(deadline: .now() + 1) { self.flush() }
        }
    }

    private func loadLocked() {
        if !loaded {
            loaded = true
            lines = Self.split(readStored()) + lines
        }
    }

    func flush() {
        lock.lock()
        flushScheduled = false
        guard dirty else { lock.unlock(); return }
        dirty = false
        let text = lines.joined(separator: "\n")
        lock.unlock()
        write(text)
    }

    private func write(_ text: String) {
        let data = Data(text.utf8)
        let q = query()
        let status = SecItemUpdate(q as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var add = q
            add[kSecValueData as String] = data
            add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            SecItemAdd(add as CFDictionary, nil)
        }
    }

    private func readStored() -> String {
        var q = query()
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let d = out as? Data else { return "" }
        return String(data: d, encoding: .utf8) ?? ""
    }

    /// What the other process wrote (the app reads the extension's lines this way).
    func read() -> String {
        lock.lock()
        defer { lock.unlock() }
        if dirty { return lines.joined(separator: "\n") }
        let stored = readStored()
        return stored
    }

    func clear() {
        lock.lock()
        lines = []
        dirty = false
        loaded = true
        lock.unlock()
        SecItemDelete(query() as CFDictionary)
    }

    private static func split(_ s: String) -> [String] {
        s.isEmpty ? [] : s.components(separatedBy: "\n")
    }
}
