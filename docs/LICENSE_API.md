# ProxyBridge: контракт API лицензий и оплаты

Base URL в продакшене: `https://www.proxybridge.org`. Все тела запросов и ответов в JSON (UTF-8).

## Тарифы

| id | Название | Цена | Срок |
|---|---|---|---|
| `month` | 1 месяц | 99 ₽ | 30 дней |
| `3months` | 3 месяца | 249 ₽ | 90 дней |
| `lifetime` | Навсегда | 699 ₽ | без срока |

Ключ лицензии: строка вида `PB-XXXX-XXXX-XXXX-XXXX`, заглавные латинские буквы и цифры без 0/O/1/I. Один ключ можно активировать максимум на 2 устройствах (`max_devices = 2`).

## Лицензии (используются программой)

### POST /api/license/activate
Запрос: `{"key": "PB-....", "hwid": "<hex sha256, 64 символа>", "app_version": "3.2.0"}`

Ответ 200:
```json
{"ok": true, "plan": "month", "expires_at": "2026-11-02T20:15:00Z", "activated_devices": 1, "max_devices": 2}
```
Для `lifetime` поле `expires_at` равно `null`.

Ошибки (HTTP 400/404/403/410, тело всегда `{"ok": false, "error": "<код>"}`):
- `not_found` (404): ключа нет
- `expired` (410): срок истёк
- `device_limit` (403): превышен лимит устройств
- `revoked` (403): ключ отозван
- `bad_request` (400): неверный формат

### POST /api/license/verify
Тот же запрос и ответ, что и `activate`, но новое устройство не добавляется. Если `hwid` не привязан к ключу, ответ 403 `{"ok": false, "error": "not_activated"}`.

### POST /api/license/deactivate
Запрос: `{"key": "...", "hwid": "..."}`. Ответ 200: `{"ok": true}`. Освобождает слот устройства.

Поведение клиента: результат успешной проверки кэшируется локально вместе с временем проверки. Если сервер недоступен, кэш считается действительным 7 дней с момента последней удачной проверки (для `lifetime` тоже). Если сервер вернул `ok: false`, кэш сбрасывается и показывается окно активации.

## Оплата (используется сайтом)

### GET /api/plans
Ответ: `[{"id": "month", "title": "1 месяц", "price_rub": 99, "days": 30}, ..., {"id": "lifetime", "title": "Навсегда", "price_rub": 699, "days": null}]`

### POST /api/checkout
Запрос: `{"plan_id": "month", "email": "user@example.com", "method": "yookassa" | "cryptobot"}`
Ответ 200: `{"order_token": "<urlsafe 32>", "pay_url": "https://..."}`
Ошибки 400: `{"ok": false, "error": "bad_plan" | "bad_email" | "bad_method" | "provider_error"}`

### GET /api/order/{token}
Ответ: `{"status": "pending" | "paid" | "canceled", "plan_id": "month", "email": "u@e.com", "key": "PB-..." | null, "method": "yookassa"}`
Ключ появляется только при `status == "paid"`.

### POST /api/order/{token}/check
Принудительно перепроверяет статус у провайдера (нужно для CryptoBot, у которого нет вебхука). Ответ как у GET.

### POST /webhook/yookassa
Уведомление ЮKassa `payment.succeeded`. Сервер НЕ доверяет телу: берёт `object.id`, запрашивает `GET https://api.yookassa.ru/v3/payments/{id}` и только при `status == "succeeded"` и совпадении суммы помечает заказ оплаченным, генерирует ключ и отправляет письмо (если настроен SMTP).

## Админ

Заголовок `X-Admin-Token: <ADMIN_TOKEN>`.
- `POST /api/admin/keys` `{"plan_id": "lifetime", "email": "optional"}` → `{"key": "PB-..."}`
- `GET /api/admin/orders?limit=50` → список заказов
- `POST /api/admin/keys/{key}/revoke` → `{"ok": true}`

## Статика

Сервер отдаёт папку `site/` как корень сайта: `/` → `site/index.html`, `/buy.html`, `/success.html`, `/legal/*.html`, `/images/*`.
