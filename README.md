# ProxyBridge

Прокси-клиент для Windows. Направляет TCP-трафик всей системы или отдельных программ через HTTP и SOCKS5 прокси на уровне ядра (WinDivert), без настройки каждого приложения.

[![Версия](https://img.shields.io/badge/версия-3.2.0-blue.svg)](https://github.com/Inter1ark/Proxy-Bridge/releases)
[![Платформа](https://img.shields.io/badge/платформа-Windows%2010%2F11-lightgrey.svg)](https://github.com/Inter1ark/Proxy-Bridge)
[![Лицензия](https://img.shields.io/badge/код-MIT-green.svg)](LICENSE)

Сайт: [proxybridge.org](https://www.proxybridge.org) · Telegram: [@inter1ark](https://t.me/inter1ark) · Почта: support@proxybridge.org

## Что внутри репозитория

| Папка | Что это |
|---|---|
| `gui/` | Приложение на Avalonia (.NET 9): окно активации, Dashboard, Proxy List, Split Tunnel, Settings |
| `src/` | Нативное ядро `ProxyBridgeCore.dll` на C: перехват пакетов WinDivert, локальный relay, правила |
| `cli/` | Консольная версия для скриптов и отладки |
| `installer/` | Скрипт NSIS для `ProxyBridge-Setup-x.y.z.exe` |
| `site/` | Сайт [proxybridge.org](https://www.proxybridge.org) |
| `server/` | Сервер лицензий и оплаты |
| `docs/` | Контракт API между программой, сайтом и сервером |

## Возможности

- HTTP и SOCKS5 прокси с авторизацией, форматы `scheme://user:pass@ip:port` и `ip:port:user:pass`
- Системный режим: весь TCP-трафик компьютера через выбранный прокси
- Split Tunnel: отдельный прокси для каждой программы, остальной трафик напрямую
- Проверка прокси (VERIFY): страна, город, флаг, доступность
- История прокси и импорт списков из файла
- Автозапуск с Windows, работа из трея, автоподключение к последнему прокси
- DNS в обход прокси (защита от утечек), режим только TCP

## Установка и запуск

1. Скачай `ProxyBridge-Setup-3.2.0.exe` на [странице релизов](https://github.com/Inter1ark/Proxy-Bridge/releases/latest).
2. Запусти установщик от имени администратора: права нужны драйверу WinDivert.
3. При первом запуске программа попросит ключ лицензии. Ключ покупается на [proxybridge.org/buy.html](https://www.proxybridge.org/buy.html) и приходит на страницу после оплаты и на email.
4. Вставь прокси, нажми VERIFY, затем CONNECT.

### Подписка

| Тариф | Цена | Срок |
|---|---|---|
| 1 месяц | 99 ₽ | 30 дней |
| 3 месяца | 249 ₽ | 90 дней |
| Навсегда | 699 ₽ | без срока |

Один ключ работает на 2 устройствах. Отвязать устройство можно в Settings. Оплата рублями (карта, СБП через ЮKassa) или криптовалютой (CryptoBot). Без автосписаний.

### Важно про совместимость

- Выключи VPN-клиенты (WireGuard, AmneziaWG, OpenVPN и подобные) на время работы ProxyBridge. Два перехватчика трафика одновременно ломают маршрутизацию, вплоть до полной потери интернета до отключения одного из них.
- Антивирус может блокировать установку драйвера WinDivert. Добавь папку программы в исключения.
- Прокси-сервер программа не предоставляет. Подборка провайдеров: [proxybridge.org/partners.html](https://www.proxybridge.org/partners.html).

## Как это работает

```
Приложение -> WinDivert (ядро) -> локальный relay ProxyBridge -> HTTP/SOCKS5 прокси -> интернет
                  |
            напрямую: DNS, локальные сети, IP самого прокси, процессы с правилом DIRECT
```

Ядро перехватывает исходящие TCP-соединения, подменяет адрес назначения на локальный relay и восстанавливает исходный адрес по таблице соединений. Relay устанавливает соединение с прокси и прокачивает данные в обе стороны. В режиме Split Tunnel правила привязаны к имени процесса.

## Лицензирование в программе

- При запуске программа проверяет ключ: `POST /api/license/verify`. Успешная проверка кэшируется на 7 дней, поэтому без интернета программа продолжит работать неделю.
- Активация на новом устройстве: `POST /api/license/activate`, лимит 2 устройства на ключ.
- Адрес сервера лицензий по умолчанию `https://www.proxybridge.org/api/license`. Для тестов его можно переопределить переменной окружения `PROXYBRIDGE_API_BASE`.
- Полный контракт: [docs/LICENSE_API.md](docs/LICENSE_API.md).

## Сборка из исходников

Требования: Windows 10/11 x64, .NET 9 SDK, MinGW-w64 (gcc), WinDivert 2.2.2-A, NSIS для установщика.

```powershell
git clone https://github.com/Inter1ark/Proxy-Bridge.git
cd Proxy-Bridge

# Нативное ядро
gcc -shared -o ProxyBridgeCore.dll -O2 -DPROXYBRIDGE_EXPORTS src\ProxyBridge.c -IC:\WinDivert-2.2.2-A\include -LC:\WinDivert-2.2.2-A\x64 -lWinDivert -lws2_32 -liphlpapi

# GUI (self-contained, ядро и драйвер копируются автоматически)
dotnet publish gui\ProxyBridge.GUI.csproj -c Release -r win-x64 --self-contained

# Установщик
.\build-installer.ps1
```

Результат: `output\ProxyBridge-Setup-3.2.0.exe`.


## Решение проблем

| Симптом | Что делать |
|---|---|
| «Failed to start WinDivert» | Запусти от имени администратора, проверь антивирус |
| «Proxy is not reachable» | Проверь логин, пароль и что прокси жив, нажми VERIFY |
| Пропал интернет после CONNECT или Split Tunnel | Нажми DISCONNECT или STOP, выключи VPN, затем подключись снова |
| «Нет связи с сервером лицензий» | Проверь интернет; с действующим кэшем программа работает 7 дней офлайн |
| Ключ «уже используется на максимальном числе устройств» | Отвяжи старое устройство в Settings или напиши в поддержку |

## Лицензия кода

Исходный код распространяется под лицензией MIT, см. [LICENSE](LICENSE). Готовые сборки с сайта и ключи активации предоставляются на условиях [публичной оферты](https://www.proxybridge.org/legal/offer.html).
