import SwiftUI
import UIKit

@main
struct ProxyBridgeApp: App {
    @StateObject private var state = AppState()
    @Environment(\.scenePhase) private var scenePhase

    init() {
        let tab = UITabBarAppearance()
        tab.configureWithOpaqueBackground()
        tab.backgroundColor = UIColor(red: 0x11 / 255, green: 0x1A / 255, blue: 0x2E / 255, alpha: 1)
        UITabBar.appearance().standardAppearance = tab
        UITabBar.appearance().scrollEdgeAppearance = tab
        let nav = UINavigationBarAppearance()
        nav.configureWithOpaqueBackground()
        nav.backgroundColor = UIColor(red: 0x0A / 255, green: 0x0F / 255, blue: 0x1E / 255, alpha: 1)
        nav.shadowColor = .clear
        nav.titleTextAttributes = [.foregroundColor: UIColor.white]
        nav.largeTitleTextAttributes = [.foregroundColor: UIColor.white]
        UINavigationBar.appearance().standardAppearance = nav
        UINavigationBar.appearance().scrollEdgeAppearance = nav
    }

    var body: some Scene {
        WindowGroup {
            RootView().environmentObject(state)
        }
        .onChange(of: scenePhase) { phase in
            if phase == .active {
                state.refreshStatus()
                Task { await state.verifyLicenseIfStale() }
            }
        }
    }
}
