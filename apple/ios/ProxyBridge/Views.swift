import NetworkExtension
import SwiftUI
import UIKit

// MARK: - the desktop palette

extension Color {
    static let pbBg = Color(red: 0x0A / 255, green: 0x0F / 255, blue: 0x1E / 255)
    static let pbSurface = Color(red: 0x11 / 255, green: 0x1A / 255, blue: 0x2E / 255)
    static let pbSurface2 = Color(red: 0x17 / 255, green: 0x22 / 255, blue: 0x38 / 255)
    static let pbLine = Color(red: 0x22 / 255, green: 0x30 / 255, blue: 0x50 / 255)
    static let pbBlue = Color(red: 0x3A / 255, green: 0x62 / 255, blue: 0xFF / 255)
    static let pbCyan = Color(red: 0x19 / 255, green: 0xD3 / 255, blue: 0xFF / 255)
    static let pbText = Color(red: 0xEE / 255, green: 0xF1 / 255, blue: 0xF8 / 255)
    static let pbMuted = Color(red: 0x8B / 255, green: 0x95 / 255, blue: 0xB0 / 255)
    static let pbDanger = Color(red: 0xFF / 255, green: 0x5C / 255, blue: 0x5C / 255)
    static let pbOk = Color(red: 0x34 / 255, green: 0xD3 / 255, blue: 0x99 / 255)
}

struct Card<Content: View>: View {
    var highlighted = false
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 16).fill(highlighted ? Color.pbBlue.opacity(0.12) : Color.pbSurface))
            .overlay(RoundedRectangle(cornerRadius: 16).stroke(highlighted ? Color.pbBlue : Color.pbLine, lineWidth: 1))
    }
}

struct PrimaryButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 16, weight: .semibold))
            .foregroundColor(.white)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 14)
            .background(RoundedRectangle(cornerRadius: 14).fill(Color.pbBlue))
            .opacity(configuration.isPressed ? 0.8 : 1)
    }
}

struct SecondaryButtonStyle: ButtonStyle {
    var danger = false
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 14, weight: .semibold))
            .foregroundColor(danger ? .pbDanger : .pbText)
            .padding(.vertical, 9)
            .padding(.horizontal, 14)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.pbSurface2.opacity(configuration.isPressed ? 0.6 : 1)))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color.pbLine, lineWidth: 1))
    }
}

func flag(_ code: String?) -> String {
    guard let code = code, code.count == 2 else { return "" }
    return code.uppercased().unicodeScalars.compactMap { UnicodeScalar(127_397 + $0.value) }.map { String($0) }.joined()
}

func formatRate(_ bytesPerSecond: Double) -> String {
    let mbit = bytesPerSecond * 8 / 1_000_000
    return mbit >= 10 ? String(format: "%.0f", mbit) : String(format: "%.1f", mbit)
}

func openURL(_ s: String) {
    if let url = URL(string: s) { UIApplication.shared.open(url) }
}

let siteURL = "https://proxybridge.org"

// MARK: - root

struct RootView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        TabView(selection: $state.tab) {
            HomeView()
                .tabItem { Label(tr("Главная", "Home"), systemImage: "power") }
                .tag(0)
            ProxiesView()
                .tabItem { Label(tr("Прокси", "Proxies"), systemImage: "server.rack") }
                .tag(1)
            SettingsView()
                .tabItem { Label(tr("Настройки", "Settings"), systemImage: "gearshape") }
                .tag(2)
        }
        .tint(.pbCyan)
        .preferredColorScheme(.dark)
    }
}

// MARK: - home

struct HomeView: View {
    @EnvironmentObject var state: AppState
    private let timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    private var statusText: String {
        switch state.status {
        case .connected: return tr("ВКЛЮЧЕНО", "ON")
        case .connecting, .reasserting: return tr("ПОДКЛЮЧЕНИЕ", "CONNECTING")
        case .disconnecting: return tr("ОТКЛЮЧЕНИЕ", "DISCONNECTING")
        default: return tr("ВЫКЛЮЧЕНО", "OFF")
        }
    }

    var body: some View {
        ZStack {
            Color.pbBg.ignoresSafeArea()
            ScrollView {
                VStack(spacing: 22) {
                    HStack(spacing: 10) {
                        Image("Logo").resizable().frame(width: 34, height: 34).clipShape(RoundedRectangle(cornerRadius: 8))
                        Text("ProxyBridge").font(.system(size: 22, weight: .bold)).foregroundColor(.pbText)
                        Spacer()
                    }
                    .padding(.top, 8)

                    powerButton.padding(.top, 18)

                    Text(statusText)
                        .font(.system(size: 13, weight: .semibold, design: .monospaced))
                        .tracking(2)
                        .foregroundColor(state.isOn ? .pbOk : .pbMuted)

                    if state.isOn {
                        HStack(spacing: 28) {
                            rate("arrow.down", state.downRate)
                            rate("arrow.up", state.upRate)
                        }
                    }

                    proxyCard

                    if let m = state.message {
                        Text(m)
                            .font(.system(size: 14))
                            .foregroundColor(.pbDanger)
                            .multilineTextAlignment(.center)
                            .frame(maxWidth: .infinity)
                    }

                    Text(tr("Весь трафик iPhone идёт через выбранный прокси. Правила для отдельных программ на iOS недоступны.",
                            "All iPhone traffic goes through the selected proxy. Per-app rules are not available on iOS."))
                        .font(.system(size: 12))
                        .foregroundColor(.pbMuted)
                        .multilineTextAlignment(.center)
                }
                .padding(20)
            }
        }
        .onReceive(timer) { _ in Task { await state.pollTunnel() } }
    }

    private var powerButton: some View {
        Button {
            Task { await state.toggle() }
        } label: {
            ZStack {
                Circle()
                    .fill(state.isOn ? Color.pbBlue : Color.pbSurface)
                    .frame(width: 170, height: 170)
                    .overlay(Circle().stroke(state.isOn ? Color.pbCyan : Color.pbLine, lineWidth: 2))
                    .shadow(color: state.isOn ? Color.pbBlue.opacity(0.55) : .clear, radius: 30)
                if state.isBusy {
                    ProgressView().progressViewStyle(.circular).tint(.pbText).scaleEffect(1.6)
                } else {
                    Image(systemName: "power").font(.system(size: 56, weight: .semibold)).foregroundColor(.pbText)
                }
            }
        }
        .buttonStyle(.plain)
        .disabled(state.status == .disconnecting)
    }

    private func rate(_ icon: String, _ value: Double) -> some View {
        HStack(spacing: 6) {
            Image(systemName: icon).foregroundColor(.pbCyan)
            Text(formatRate(value)).font(.system(size: 18, weight: .semibold, design: .monospaced)).foregroundColor(.pbText)
            Text(tr("Мбит/с", "Mbit/s")).font(.system(size: 12)).foregroundColor(.pbMuted)
        }
    }

    private var proxyCard: some View {
        Button { state.tab = 1 } label: {
            Card {
                HStack(spacing: 12) {
                    if let p = state.config.active {
                        Text(flag(p.check?.country_code)).font(.system(size: 28))
                        VStack(alignment: .leading, spacing: 3) {
                            Text(p.title).font(.system(size: 16, weight: .semibold)).foregroundColor(.pbText).lineLimit(1)
                            Text(subtitle(p)).font(.system(size: 13)).foregroundColor(.pbMuted).lineLimit(1)
                        }
                    } else {
                        Image(systemName: "plus.circle").font(.system(size: 26)).foregroundColor(.pbCyan)
                        Text(tr("Добавить прокси", "Add a proxy")).font(.system(size: 16, weight: .semibold)).foregroundColor(.pbText)
                    }
                    Spacer()
                    Image(systemName: "chevron.right").foregroundColor(.pbMuted)
                }
            }
        }
        .buttonStyle(.plain)
    }

    private func subtitle(_ p: ProxyItem) -> String {
        var parts = [p.kind]
        if let c = p.check, c.ok {
            if let country = c.country { parts.append(country) }
            if let ip = c.ip { parts.append(ip) }
        }
        return parts.joined(separator: " · ")
    }
}

// MARK: - proxies

struct ProxiesView: View {
    @EnvironmentObject var state: AppState
    @State private var adding = false
    @State private var renaming: ProxyItem?
    @State private var newName = ""

    var body: some View {
        NavigationStack {
            ZStack {
                Color.pbBg.ignoresSafeArea()
                if state.config.proxies.isEmpty {
                    VStack(spacing: 14) {
                        Image(systemName: "server.rack").font(.system(size: 40)).foregroundColor(.pbMuted)
                        Text(tr("Прокси пока нет", "No proxies yet")).font(.system(size: 17, weight: .semibold)).foregroundColor(.pbText)
                        Text(tr("Вставь SOCKS5 или HTTP прокси в любом привычном виде, например host:port:login:password.",
                                "Paste a SOCKS5 or HTTP proxy in any usual form, for example host:port:login:password."))
                            .font(.system(size: 14)).foregroundColor(.pbMuted).multilineTextAlignment(.center)
                        Button(tr("Добавить прокси", "Add a proxy")) { adding = true }.buttonStyle(PrimaryButtonStyle())
                        Button(tr("Купить прокси", "Buy proxies")) { openURL(siteURL + "/kupit-proksi/") }.buttonStyle(SecondaryButtonStyle())
                    }
                    .padding(28)
                } else {
                    List {
                        ForEach(state.config.proxies) { p in
                            row(p)
                                .listRowBackground(Color.pbSurface)
                                .swipeActions(edge: .trailing) {
                                    Button(role: .destructive) { state.remove(p.id) } label: { Label(tr("Удалить", "Delete"), systemImage: "trash") }
                                    Button { newName = p.label; renaming = p } label: { Label(tr("Имя", "Name"), systemImage: "pencil") }.tint(.pbBlue)
                                }
                        }
                    }
                    .scrollContentBackground(.hidden)
                    .refreshable { await state.checkAll() }
                }
            }
            .navigationTitle(tr("Прокси", "Proxies"))
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    if !state.config.proxies.isEmpty {
                        Button(tr("Проверить все", "Check all")) { Task { await state.checkAll() } }
                    }
                }
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button { adding = true } label: { Image(systemName: "plus") }
                }
            }
            .sheet(isPresented: $adding) { AddProxySheet() }
            .alert(tr("Имя прокси", "Proxy name"), isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
                TextField(tr("Например, Германия", "For example, Germany"), text: $newName)
                Button(tr("Сохранить", "Save")) { if let p = renaming { state.rename(p.id, newName) }; renaming = nil }
                Button(tr("Отмена", "Cancel"), role: .cancel) { renaming = nil }
            }
        }
    }

    private func row(_ p: ProxyItem) -> some View {
        let active = state.config.active?.id == p.id
        return HStack(spacing: 12) {
            Image(systemName: active ? "largecircle.fill.circle" : "circle")
                .font(.system(size: 22))
                .foregroundColor(active ? .pbCyan : .pbMuted)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    if let f = p.check?.country_code { Text(flag(f)) }
                    Text(p.title).font(.system(size: 15, weight: .semibold)).foregroundColor(.pbText).lineLimit(1)
                }
                Text(details(p)).font(.system(size: 12)).foregroundColor(p.check.map { $0.ok ? Color.pbMuted : Color.pbDanger } ?? .pbMuted).lineLimit(2)
            }
            Spacer()
            if state.checking.contains(p.id) {
                ProgressView()
            } else {
                Button { Task { await state.check(p.id) } } label: {
                    Image(systemName: "arrow.clockwise").foregroundColor(.pbCyan)
                }
                .buttonStyle(.borderless)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { state.select(p.id) }
        .padding(.vertical, 4)
    }

    private func details(_ p: ProxyItem) -> String {
        var parts = [p.kind, p.label.isEmpty ? nil : p.hostPort].compactMap { $0 }
        if let c = p.check {
            if c.ok {
                if let ip = c.ip { parts.append(ip) }
                if let city = c.city, !city.isEmpty { parts.append(city) }
                parts.append("\(c.ms) ms")
                if p.kind == "SOCKS5" { parts.append(c.udp ? "UDP ✓" : tr("без UDP", "no UDP")) }
            } else {
                parts.append(tr("не работает: ", "not working: ") + (c.error ?? "?"))
            }
        }
        return parts.joined(separator: " · ")
    }
}

struct AddProxySheet: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    @State private var note: String?

    var body: some View {
        NavigationStack {
            ZStack {
                Color.pbBg.ignoresSafeArea()
                VStack(alignment: .leading, spacing: 14) {
                    Text(tr("По одному прокси в строке. Подходит host:port, host:port:login:password, login:password@host:port, ссылки socks5:// и http://.",
                            "One proxy per line. Accepted: host:port, host:port:login:password, login:password@host:port, socks5:// and http:// links."))
                        .font(.system(size: 13)).foregroundColor(.pbMuted)
                    TextEditor(text: $text)
                        .font(.system(size: 14, design: .monospaced))
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        .scrollContentBackground(.hidden)
                        .padding(10)
                        .frame(minHeight: 160)
                        .background(RoundedRectangle(cornerRadius: 12).fill(Color.pbSurface))
                        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color.pbLine, lineWidth: 1))
                    Button(tr("Вставить из буфера", "Paste")) {
                        if let s = UIPasteboard.general.string { text = text.isEmpty ? s : text + "\n" + s }
                    }
                    .buttonStyle(SecondaryButtonStyle())
                    if let n = note { Text(n).font(.system(size: 13)).foregroundColor(.pbDanger) }
                    Button(tr("Добавить", "Add")) {
                        let before = state.config.proxies.count
                        let bad = state.addProxies(text)
                        if bad > 0 && state.config.proxies.count == before {
                            note = tr("Не удалось разобрать ни одной строки.", "None of the lines could be read.")
                        } else {
                            dismiss()
                        }
                    }
                    .buttonStyle(PrimaryButtonStyle())
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    Spacer()
                }
                .padding(20)
            }
            .navigationTitle(tr("Новый прокси", "New proxy"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(tr("Отмена", "Cancel")) { dismiss() } }
            }
        }
        .preferredColorScheme(.dark)
    }
}

// MARK: - settings

struct SettingsView: View {
    @EnvironmentObject var state: AppState
    @State private var key = ""

    private var coreVersion: String {
        guard let v = pbVersion() else { return "?" }
        defer { free(v) }
        return String(cString: v)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section(tr("Лицензия", "License")) {
                    if state.license.isEmpty {
                        TextField("PB-XXXX-XXXX-XXXX-XXXX", text: $key)
                            .font(.system(size: 15, design: .monospaced))
                            .textInputAutocapitalization(.characters)
                            .autocorrectionDisabled()
                        Button(tr("Активировать", "Activate")) { Task { await state.activate(key) } }
                            .disabled(state.licenseBusy || key.trimmingCharacters(in: .whitespaces).isEmpty)
                        Text(tr("Ключ не введён. Купить можно на proxybridge.org.", "No key yet. You can buy one at proxybridge.org."))
                            .font(.system(size: 13)).foregroundColor(.pbMuted)
                    } else {
                        Text(state.license.key).font(.system(size: 15, design: .monospaced))
                        Text(licenseLine).foregroundColor(state.license.isValid ? .pbOk : .pbMuted)
                        Button(tr("Освободить устройство", "Release this device"), role: .destructive) { Task { await state.deactivate() } }
                            .disabled(state.licenseBusy)
                    }
                    if let n = state.licenseNote { Text(n).font(.system(size: 13)).foregroundColor(.pbDanger) }
                }

                Section {
                    Picker("UDP", selection: Binding(get: { state.config.udpMode }, set: { state.setUDP($0) })) {
                        Text(tr("Через прокси", "Through the proxy")).tag("auto")
                        Text(tr("Напрямую", "Direct")).tag("direct")
                        Text(tr("Блокировать", "Block")).tag("block")
                    }
                    Toggle(tr("DNS через прокси", "DNS through the proxy"), isOn: Binding(get: { state.config.dnsViaProxy }, set: { state.setDNSViaProxy($0) }))
                } header: {
                    Text(tr("Сеть", "Network"))
                } footer: {
                    Text(tr("UDP нужен звонкам и играм. Если прокси его не поддерживает, UDP блокируется, чтобы не было утечек.",
                            "UDP is used by calls and games. When the proxy has no UDP support, UDP is blocked so nothing leaks."))
                }

                Section(tr("О программе", "About")) {
                    LabeledContent(tr("Версия", "Version"), value: LicenseAPI.appVersion)
                    LabeledContent(tr("Ядро", "Core"), value: coreVersion)
                    Button("proxybridge.org") { openURL(siteURL) }
                }
            }
            .scrollContentBackground(.hidden)
            .background(Color.pbBg.ignoresSafeArea())
            .navigationTitle(tr("Настройки", "Settings"))
        }
    }

    private var licenseLine: String {
        let l = state.license
        if !l.isValid { return tr("Нужна проверка ключа: подключись к интернету.", "The key needs a check: connect to the internet.") }
        guard let d = l.expiryDate else { return tr("Активна навсегда", "Active forever") }
        let f = DateFormatter()
        f.locale = Locale(identifier: isRussian ? "ru_RU" : "en_US")
        f.dateStyle = .long
        return tr("Активна до ", "Active until ") + f.string(from: d)
    }
}
