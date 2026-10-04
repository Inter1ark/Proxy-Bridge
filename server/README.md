# ProxyBridge Server

Бэкенд сайта и системы лицензий ProxyBridge: оплата через ЮKassa и CryptoBot, выдача ключей
вида `PB-XXXX-XXXX-XXXX-XXXX`, активация и проверка ключей из программы, админ-эндпоинты.
Контракт API описан в `../docs/LICENSE_API.md`, сервер реализует его один в один.

Стек: Python 3.12, FastAPI, uvicorn, SQLAlchemy 2 (синхронный), SQLite, httpx.

## Структура

```
server/
  app/
    main.py            фабрика FastAPI, роутеры, статика сайта
    config.py          настройки из переменных окружения и .env
    db.py              движок SQLAlchemy и сессии
    models.py          таблицы Order, License, Device
    keys.py            генерация и нормализация ключей
    plans.py           тарифы
    orders.py          логика заказов: отметить оплаченным, выдать ключ, проверить у провайдера
    mailer.py          отправка ключа на почту (SMTP) или запись в лог
    providers/         клиенты ЮKassa и CryptoBot
    routers/           license, checkout, webhook, admin
  tests/               pytest, без сети (провайдеры подменяются)
  data/                файл базы proxybridge.db (в git не попадает)
  deploy/              nginx.conf и systemd unit
  Dockerfile, docker-compose.yml, run.ps1, run.sh, .env.example
```

## Быстрый старт локально

```
cd server
python -m venv .venv
.venv\Scripts\activate          (Linux: source .venv/bin/activate)
pip install -r requirements.txt
copy .env.example .env          (Linux: cp .env.example .env)
```

Заполните `.env` (см. ниже) и запустите:

```
.\run.ps1        или  ./run.sh
```

Это выполнит `uvicorn app.main:app --reload --port 8080`. Сайт откроется на
`http://127.0.0.1:8080/`, API на `http://127.0.0.1:8080/api/...`.

Тесты:

```
python -m pytest -q
```

## Переменные окружения

Файл `server/.env` читается при старте (без python-dotenv, простой парсер `KEY=VALUE`).
Переменные, уже заданные в окружении, имеют приоритет над `.env`.

| Переменная | Назначение |
|---|---|
| `BASE_URL` | Публичный адрес сайта без слэша в конце, например `https://www.proxybridge.org`. Используется для CORS и ссылок возврата после оплаты |
| `ADMIN_TOKEN` | Секрет для админ-эндпоинтов (заголовок `X-Admin-Token`). Если не задан, админ-эндпоинты отвечают 401 |
| `YOOKASSA_SHOP_ID` | Идентификатор магазина ЮKassa |
| `YOOKASSA_SECRET_KEY` | Секретный ключ ЮKassa |
| `YOOKASSA_RETURN_URL` | Куда вернуть покупателя после оплаты. По умолчанию `{BASE_URL}/success.html?token={token}`; плейсхолдер `{token}` заменяется на токен заказа |
| `CRYPTOPAY_API_TOKEN` | Токен приложения Crypto Pay (из @CryptoBot) |
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, `SMTP_FROM` | Необязательно. Если `SMTP_HOST` пуст, ключ не отправляется письмом, а только пишется в лог сервера. Порт 465 означает SSL, остальные порты STARTTLS |
| `DATABASE_URL` | По умолчанию `sqlite:///<server>/data/proxybridge.db` |
| `MAX_DEVICES` | Максимум устройств на один ключ, по умолчанию 2 |
| `LOG_LEVEL` | Уровень логирования, по умолчанию INFO |

Сгенерировать токен админа: `python -c "import secrets; print(secrets.token_urlsafe(32))"`.

## Тарифы

| id | Название | Цена | Срок |
|---|---|---|---|
| `month` | 1 месяц | 99 RUB | 30 дней |
| `3months` | 3 месяца | 249 RUB | 90 дней |
| `lifetime` | Навсегда | 699 RUB | без срока (`expires_at = null`) |

Тарифы заданы в `app/plans.py`.

## Как проходит оплата

Сайт вызывает `POST /api/checkout` с `plan_id`, `email` и `method` (`yookassa` или `cryptobot`).
Сервер создаёт заказ с токеном (32 символа) и платёж у провайдера, в метаданные платежа
кладётся токен заказа. Ответ содержит `order_token` и `pay_url`, куда нужно отправить покупателя.
Страница `success.html?token=...` опрашивает `GET /api/order/{token}` и показывает ключ,
когда `status` становится `paid`.

### ЮKassa: вебхук

Сервер принимает уведомления на `POST /webhook/yookassa`. Телу уведомления сервер не доверяет:
берёт только `object.id`, запрашивает платёж через `GET https://api.yookassa.ru/v3/payments/{id}`
и только при `status == succeeded` и совпадении суммы с ценой тарифа помечает заказ оплаченным,
выдаёт ключ и отправляет письмо. Повторные уведомления безопасны: второй ключ не создаётся.

Регистрация вебхука в личном кабинете ЮKassa:

- Откройте раздел Интеграция, затем HTTP-уведомления.
- URL для уведомлений: `https://www.proxybridge.org/webhook/yookassa`.
- Отметьте событие `payment.succeeded` (при желании также `payment.canceled`, остальные события сервер игнорирует).
- Сохраните. ЮKassa присылает уведомления только на HTTPS, поэтому сначала настройте сертификат.

Если сервер ответил не 2xx (например, ЮKassa временно недоступна), ЮKassa повторит уведомление позже.

### CryptoBot: опрос статуса

У CryptoBot в этой схеме нет вебхука. При оформлении создаётся счёт (`createInvoice`,
`currency_type = fiat`, `fiat = RUB`, `expires_in = 3600`, в `payload` кладётся токен заказа).
Страница успеха периодически вызывает `POST /api/order/{token}/check`: сервер запрашивает
`getInvoices` по `invoice_id`, при статусе `paid` помечает заказ оплаченным и выдаёт ключ,
при статусе `expired` помечает заказ как `canceled`. Тот же эндпоинт работает и для ЮKassa
(полезно, если вебхук не дошёл).

## Лицензии

- `POST /api/license/activate` привязывает устройство (`hwid`, 64 hex-символа) к ключу, если лимит устройств не превышен.
- `POST /api/license/verify` проверяет ключ, но новое устройство не добавляет; для непривязанного `hwid` отвечает 403 `not_activated`.
- `POST /api/license/deactivate` освобождает слот устройства.

Коды ошибок: `not_found` (404), `expired` (410), `device_limit` (403), `revoked` (403),
`bad_request` (400). Ключ принимается в любом написании: регистр, пробелы и дефисы не важны.
Срок хранится в UTC, в ответах формат `2026-11-02T20:15:00Z`.

## Админ-эндпоинты

Все запросы с заголовком `X-Admin-Token: <ADMIN_TOKEN>`.

Выдать ключ вручную:

```
curl -X POST https://www.proxybridge.org/api/admin/keys \
  -H "X-Admin-Token: ВАШ_ТОКЕН" \
  -H "Content-Type: application/json" \
  -d '{"plan_id": "lifetime", "email": "user@example.com"}'
```

Ответ: `{"key": "PB-XXXX-XXXX-XXXX-XXXX"}`. Если указан email и настроен SMTP, ключ также
отправляется письмом.

Список заказов:

```
curl https://www.proxybridge.org/api/admin/orders?limit=50 -H "X-Admin-Token: ВАШ_ТОКЕН"
```

Отозвать ключ:

```
curl -X POST https://www.proxybridge.org/api/admin/keys/PB-XXXX-XXXX-XXXX-XXXX/revoke \
  -H "X-Admin-Token: ВАШ_ТОКЕН"
```

## Статика сайта

Папка `../site` (рядом с `server/`) монтируется как корень сайта: `/` отдаёт `site/index.html`,
`/buy.html`, `/success.html`, `/legal/*.html`, `/images/*`. Статика подключается последней,
поэтому маршруты `/api/*` и `/webhook/*` всегда имеют приоритет. Если папки `site` нет,
сервер пишет предупреждение в лог и отдаёт только API.

## Деплой на сервер

Вариант с systemd:

- Склонируйте репозиторий в `/opt/proxybridge`, создайте пользователя `proxybridge`.
- В `server/` создайте venv, установите зависимости, заполните `.env`.
- Скопируйте `deploy/proxybridge.service` в `/etc/systemd/system/`, затем `systemctl enable --now proxybridge`.
- Скопируйте `deploy/nginx.conf` в `/etc/nginx/sites-available/proxybridge.org`, включите сайт и получите сертификат: `certbot --nginx -d proxybridge.org -d www.proxybridge.org`.

Вариант с Docker (из папки `server/`):

```
docker compose up -d --build
```

Контейнер слушает `127.0.0.1:8080`, база лежит в `server/data/` (volume), сайт копируется из `../site`
при сборке образа. nginx проксирует на `127.0.0.1:8080` в обоих вариантах.

## Безопасность

- Вебхук ЮKassa никогда не доверяет телу запроса, статус и сумма проверяются через API.
- Повторная отметка заказа оплаченным не создаёт второй ключ (уникальная связь заказ-лицензия в базе).
- Админ-эндпоинты недоступны, пока не задан `ADMIN_TOKEN`; сравнение токена постоянное по времени.
- Секреты хранятся только в `.env`, который исключён из git.
