# 🌐 ProxyBridge

**Universal Proxy Client for Windows** - Route all your traffic through HTTP/SOCKS5 proxies with ease!

[![Version](https://img.shields.io/badge/version-3.0.0-blue.svg)](https://github.com/Inter1ark/Proxy-Bridge/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey.svg)](https://github.com/Inter1ark/Proxy-Bridge)
[![License](https://img.shields.io/badge/license-GPL--3.0-green.svg)](LICENSE)

---

## ✨ Features

- 🌍 **HTTP & SOCKS5 Support** - Works with any proxy type
- 🔐 **Full System Routing** - All applications use the proxy automatically
- 🌎 **GEO Verification** - Check proxy location with country flags
- 💾 **Proxy History** - Auto-save recently used proxies
- 🎨 **Modern UI** - Clean Avalonia-based interface
- 🚀 **TCP-Only Mode** - Maximum stability (no UDP errors)
- 🔒 **DNS Bypass** - Prevent DNS leaks

---

## 📦 Installation

### Option 1: Installer (Recommended)

1. Download **[ProxyBridge-Setup-3.0.0.exe](https://github.com/Inter1ark/Proxy-Bridge/releases/latest)**
2. Run installer as Administrator
3. Launch ProxyBridge from Start Menu

### Option 2: Build from Source

**Requirements:**
- Windows 10/11 (64-bit)
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [MinGW-w64 GCC](https://www.msys2.org/)
- [WinDivert 2.2.2-A](https://reqrypt.org/windivert.html)

**Build steps:**

```powershell
# 1. Clone repository
git clone https://github.com/Inter1ark/Proxy-Bridge.git
cd Proxy-Bridge

# 2. Build GUI
cd gui
dotnet build -c Release

# 3. Compile native DLL
$env:PATH = "C:\msys64\mingw64\bin;$env:PATH"
cd ..
gcc -shared -o ProxyBridgeCore.dll -O2 -DPROXYBRIDGE_EXPORTS src\ProxyBridge.c -IC:\WinDivert-2.2.2-A\include -LC:\WinDivert-2.2.2-A\x64 -lWinDivert -lws2_32 -liphlpapi

# 4. Copy DLL to build folder
Copy-Item ProxyBridgeCore.dll gui\bin\Release\net9.0-windows\ -Force

# 5. Run
cd gui\bin\Release\net9.0-windows
.\ProxyBridge.exe
```

---

## 🚀 Quick Start

### 1. Enter Proxy

Support formats:
- `http://user:pass@ip:port`
- `socks5://user:pass@ip:port`
- `ip:port:user:pass` (legacy)

**Example:**
```
http://myuser:mypass@89.39.104.79:14973
```

### 2. Verify Proxy (Optional)

Click **VERIFY** to test connection and see:
- 🌍 Country and city
- 🚩 Country flag
- 📍 IP address
- ✅ Connection status

### 3. Connect

Click **CONNECT** - all system traffic now routes through proxy!

### 4. Disconnect

Click **DISCONNECT** to restore direct connection.

---

## 🎯 How It Works

ProxyBridge uses **WinDivert** to intercept network packets at kernel level:

1. **Packet Capture** - Intercepts all outgoing TCP traffic
2. **Proxy Routing** - Redirects through local SOCKS5/HTTP relay
3. **Direct Bypass** - Local IPs and DNS go direct (configurable)
4. **Transparent** - Applications don't need proxy settings

**Architecture:**
```
Application → WinDivert → ProxyBridge → HTTP/SOCKS5 Proxy → Internet
                 ↓
            Direct (DNS, local IPs)
```

---

## ⚙️ Configuration

### Auto-Configured (No UI controls)

- **TCP-Only Mode** - Enabled by default (prevents UDP errors)
- **DNS Bypass** - Enabled by default (DNS goes direct)

### Manual Settings (Advanced)

Edit `%APPDATA%\ProxyBridge\proxy_history.json` to manage saved proxies.

---

## 🛠️ Building Installer

**Requirements:**
- [NSIS](https://nsis.sourceforge.io/Download)

**Run:**
```powershell
.\build-installer.ps1
```

Output: `output\ProxyBridge-Setup-3.0.0.exe`

---

## 📋 System Requirements

- **OS:** Windows 10/11 (64-bit)
- **RAM:** 100 MB minimum
- **Privileges:** Administrator (for WinDivert driver)
- **Dependencies:** .NET 9 Runtime (included in installer)

---

## 🔧 Troubleshooting

### "Proxy is not reachable"
- Check proxy credentials
- Verify proxy is online
- Try VERIFY first

### "Failed to start WinDivert driver"
- Run as Administrator
- Disable antivirus temporarily
- Check Windows Defender exclusions

### "Some websites don't load"
- Proxy provider may block certain IPs
- Try different proxy
- Check proxy speed/stability

### Build errors
- Ensure GCC is in PATH: `$env:PATH = "C:\msys64\mingw64\bin;$env:PATH"`
- Install WinDivert to `C:\WinDivert-2.2.2-A\`
- Use .NET 9 SDK

---

## 🤝 Contributing

Contributions welcome! Please:

1. Fork repository
2. Create feature branch (`git checkout -b feature/amazing`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing`)
5. Open Pull Request

---

## 📄 License

This project is licensed under the **GPL-3.0 License** - see [LICENSE](LICENSE) file.

---

## 🙏 Credits

- **WinDivert** - Basil ([@basil00](https://github.com/basil00))
- **Avalonia** - [AvaloniaUI Team](https://avaloniaui.net/)
- **.NET** - Microsoft

---

## 📞 Support

- **Issues:** [GitHub Issues](https://github.com/Inter1ark/Proxy-Bridge/issues)
- **Discussions:** [GitHub Discussions](https://github.com/Inter1ark/Proxy-Bridge/discussions)

---

**Made with ❤️ for the proxy community**
