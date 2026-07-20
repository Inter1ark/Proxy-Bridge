# 🌐 ProxyBridge

**Универсальный прокси-клиент для Windows** - направляйте весь трафик через HTTP/SOCKS5 прокси!

[![Версия](https://img.shields.io/badge/версия-3.0.0-blue.svg)](https://github.com/Inter1ark/Proxy-Bridge/releases)
[![Платформа](https://img.shields.io/badge/платформа-Windows%2010%2F11-lightgrey.svg)](https://github.com/Inter1ark/Proxy-Bridge)
[![Лицензия](https://img.shields.io/badge/лицензия-GPL--3.0-green.svg)](LICENSE)

---

## ✨ Возможности

- 🌍 **Поддержка HTTP и SOCKS5** - работает с любым типом прокси
- 🔐 **Полная маршрутизация системы** - все приложения автоматически используют прокси
- 🌎 **GEO-проверка** - проверяйте местоположение прокси с флагом страны
- 💾 **История прокси** - автоматическое сохранение недавно использованных прокси
- 🎨 **Современный интерфейс** - чистый UI на основе Avalonia
- 🚀 **Режим только TCP** - максимальная стабильность (без ошибок UDP)
- 🔒 **Обход DNS** - предотвращение утечек DNS

---

## 📦 Установка

### ⚡ Быстрая установка (Рекомендуется)

1. **Скачайте установщик:** [ProxyBridge-Setup.exe](https://github.com/Inter1ark/Proxy-Bridge/releases/latest/download/ProxyBridge-Setup.exe)
2. **Запустите** установщик от имени Администратора
3. **Готово!** Программа установлена и готова к работе

> ⚠️ **Важно:** Требуются права Администратора для установки драйвера WinDivert

---

## 🚀 Быстрый старт

### 1. Введите прокси

Поддерживаемые форматы:
- `http://user:pass@ip:port`
- `socks5://user:pass@ip:port`  
- `ip:port:user:pass` (устаревший)

**Пример:**
```
http://myuser:mypass@89.39.104.79:14973
```

![Главное окно](screenshots/Dashboard.png)

### 2. Проверьте прокси (Опционально)

Нажмите **VERIFY** чтобы проверить подключение и увидеть:
- 🌍 Страну и город
- 🚩 Флаг страны
- 📍 IP адрес
- ✅ Статус подключения

### 3. Подключитесь

Нажмите **CONNECT** - весь системный трафик теперь идет через прокси!

### 4. Отключитесь

Нажмите **DISCONNECT** для восстановления прямого подключения.

---

## 🎯 Как это работает

ProxyBridge использует **WinDivert** для перехвата сетевых пакетов на уровне ядра:

1. **Захват пакетов** - перехватывает весь исходящий TCP трафик
2. **Маршрутизация через прокси** - перенаправляет через локальный SOCKS5/HTTP relay
3. **Прямой обход** - локальные IP и DNS идут напрямую (настраивается)
4. **Прозрачность** - приложениям не нужны настройки прокси

**Архитектура:**
```
Приложение → WinDivert → ProxyBridge → HTTP/SOCKS5 Прокси → Интернет
                 ↓
          Напрямую (DNS, локальные IP)
```

---

## ⚙️ Настройки

### Автоматически настроенные

- **Режим только TCP** - Включен по умолчанию (предотвращает ошибки UDP)
- **Обход DNS** - Включен по умолчанию (DNS идет напрямую)

### Дополнительные настройки

Откройте **Settings** для дополнительных опций:
- Minimize to Tray - сворачивать в трей
- Start with Windows - запуск с Windows
- Auto-connect Last Proxy - автоподключение
- Show Notifications - уведомления

![Настройки](screenshots/Settings.png)

---

## 📸 Скриншоты

| Главное окно | Настройки |
|--------------|-----------|
| ![Dashboard](screenshots/Dashboard.png) | ![Settings](screenshots/Settings.png) |

---

## 🛠️ Сборка из исходников

**Требования:**
- Windows 10/11 (64-bit)
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [MinGW-w64 GCC](https://www.msys2.org/)
- [WinDivert 2.2.2-A](https://reqrypt.org/windivert.html)

**Шаги сборки:**

```powershell
# 1. Клонируйте репозиторий
git clone https://github.com/Inter1ark/Proxy-Bridge.git
cd Proxy-Bridge

# 2. Соберите GUI (self-contained)
cd gui
dotnet publish -c Release -r win-x64 --self-contained

# 3. Скомпилируйте нативную DLL
$env:PATH = "C:\msys64\mingw64\bin;$env:PATH"
cd ..
gcc -shared -o ProxyBridgeCore.dll -O2 -DPROXYBRIDGE_EXPORTS src\ProxyBridge.c -IC:\WinDivert-2.2.2-A\include -LC:\WinDivert-2.2.2-A\x64 -lWinDivert -lws2_32 -liphlpapi

# 4. Скопируйте DLL
Copy-Item ProxyBridgeCore.dll gui\bin\Release\net9.0-windows\win-x64\publish\ -Force

# 5. Запустите
cd gui\bin\Release\net9.0-windows\win-x64\publish
.\ProxyBridge.exe
```

---

## 🔧 Создание установщика

**Требования:**
- [NSIS](https://nsis.sourceforge.io/Download)

**Запустите:**
```powershell
.\build-installer.ps1
```

Результат: `output\ProxyBridge-Setup-3.0.0.exe`

---

## 📋 Системные требования

- **ОС:** Windows 10/11 (64-bit)
- **ОЗУ:** минимум 100 МБ
- **Права:** Администратор (для драйвера WinDivert)
- **Зависимости:** .NET 9 Runtime (включен в установщик)

---

## 🔧 Решение проблем

### "Proxy is not reachable"
- Проверьте учетные данные прокси
- Убедитесь что прокси онлайн
- Сначала нажмите VERIFY

### "Failed to start WinDivert driver"
- Запустите от имени Администратора
- Временно отключите антивирус
- Проверьте исключения Windows Defender

### "Некоторые сайты не загружаются"
- Провайдер прокси может блокировать определенные IP
- Попробуйте другой прокси
- Проверьте скорость/стабильность прокси

---

## 🤝 Участие в разработке

Вклад приветствуется! Пожалуйста:

1. Сделайте Fork репозитория
2. Создайте ветку функции (`git checkout -b feature/amazing`)
3. Закоммитьте изменения (`git commit -m 'Добавить потрясающую функцию'`)
4. Запушьте в ветку (`git push origin feature/amazing`)
5. Откройте Pull Request

---

## 📄 Лицензия

Этот проект лицензирован под **GPL-3.0 License** - смотрите файл [LICENSE](LICENSE).

---

## 🙏 Благодарности

- **WinDivert** - Basil ([@basil00](https://github.com/basil00))
- **Avalonia** - [AvaloniaUI Team](https://avaloniaui.net/)
- **.NET** - Microsoft

---

## 📞 Поддержка

- **Проблемы:** [GitHub Issues](https://github.com/Inter1ark/Proxy-Bridge/issues)
- **Обсуждения:** [GitHub Discussions](https://github.com/Inter1ark/Proxy-Bridge/discussions)

---

**Сделано с ❤️ для прокси-сообщества**
