"""Telegram Stars sales through a separate bot.

An order starts on the site (method "stars", the buyer then opens the bot with /start <token>)
or in the bot itself (plan buttons). handle_update() turns each Telegram update into
the one Bot API call that answers it (sendInvoice, answerPreCheckoutQuery, sendMessage with the
key). The poller below fetches updates by long polling and sends those calls; the webhook route
(routers/stars.py) can return them in its reply instead, for a host Telegram can reach.
"""

import logging
import secrets
import threading
from datetime import timedelta

import httpx
from sqlalchemy import select
from sqlalchemy.orm import Session

from .config import settings
from .models import License, Order, from_db, to_db, utcnow
from .orders import mark_order_paid
from .plans import PLANS, get_public_plan

logger = logging.getLogger(__name__)

METHOD = "stars"
CURRENCY = "XTR"
PENDING_TTL = timedelta(hours=24)  # an unpaid Stars order is dropped after this
SITE = "https://www.proxybridge.org"


def is_ru(lang: str | None) -> bool:
    return (lang or "").lower()[:2] in ("ru", "uk", "be", "kk")


def plan_title(plan, ru: bool) -> str:
    return plan.title if ru or not plan.title_en else plan.title_en


def bot_link(token: str) -> str:
    return f"https://t.me/{settings.STARS_BOT_USERNAME}?start={token}" if settings.STARS_BOT_USERNAME else ""


def new_order(db: Session, plan, email: str, chat_id: int | None = None, renew_key: str | None = None) -> Order:
    """A pending Stars order (committed). The chat id of a bot order sits in provider_payment_id
    until the payment replaces it with the Telegram charge id."""
    order = Order(token=secrets.token_urlsafe(24), plan_id=plan.id, email=email, method=METHOD,
                  amount_rub=round(plan.price_stars * settings.STARS_RUB_RATE), amount_stars=plan.price_stars,
                  provider_payment_id=f"tg:{chat_id}" if chat_id else None, renew_key=renew_key)
    db.add(order)
    db.commit()
    return order


def _bot_order(db: Session, plan, chat_id: int, renew_key: str | None = None) -> Order:
    """Reuse the chat's unpaid order for the same plan, so pressing a button again adds no rows."""
    order = db.scalar(select(Order).where(
        Order.method == METHOD, Order.status == "pending", Order.plan_id == plan.id,
        Order.provider_payment_id == f"tg:{chat_id}",
        (Order.renew_key == renew_key) if renew_key else Order.renew_key.is_(None),
        Order.created_at >= to_db(utcnow() - PENDING_TTL)).order_by(Order.id.desc()))
    return order or new_order(db, plan, "", chat_id, renew_key=renew_key)


def buyer_chat(db: Session, lic: License) -> int | None:
    """Telegram user who paid for the key in the bot (private chat id = user id), if any."""
    orders = db.scalars(select(Order).where(Order.method == METHOD, Order.status == "paid",
                                            Order.key == lic.key).order_by(Order.id.desc())).all()
    for order in orders:
        uid = (order.provider_payment_id or "").split(":", 1)[0]
        if uid.isdigit():
            return int(uid)
    return None


def renew_keyboard(lic: License, ru: bool) -> dict:
    """Plan buttons that extend this key (callback ren:<plan>:<license id>)."""
    word = "Продлить" if ru else "Renew"
    return {"inline_keyboard": [[{"text": f"{word}: {plan_title(p, ru)}, {p.price_stars} ⭐",
                                  "callback_data": f"ren:{p.id}:{lic.id}"}]
                                for p in PLANS.values() if p.price_stars]}


def end_text(lic: License, ru: bool) -> str:
    if lic.expires_at is None:
        return "без срока" if ru else "with no expiry"
    end = from_db(lic.expires_at) + timedelta(hours=3)  # Moscow time
    return f"до {end:%d.%m.%Y}" if ru else f"until {end:%Y-%m-%d}"


def expire_stale(db: Session) -> int:
    """Cancel Stars orders nobody paid within PENDING_TTL."""
    stale = db.scalars(select(Order).where(Order.method == METHOD, Order.status == "pending",
                                           Order.created_at < to_db(utcnow() - PENDING_TTL))).all()
    for order in stale:
        order.status = "canceled"
        order.fail_reason = "expired"
    if stale:
        db.commit()
    return len(stale)


# ---------------------------------------------------------------------------
# Texts
# ---------------------------------------------------------------------------

def _plans_text(ru: bool) -> str:
    if ru:
        return ("ProxyBridge: программа, которая пускает выбранные приложения через прокси на Windows, macOS, "
                "Android и iPhone.\n\nВыбери тариф. Оплата в Telegram Stars, ключ придёт в этот чат сразу "
                f"после оплаты.\n\nСайт: {SITE}")
    return ("ProxyBridge routes the apps you choose through a proxy on Windows, macOS, Android and iPhone.\n\n"
            "Pick a plan. You pay with Telegram Stars and the key arrives in this chat right after the payment.\n\n"
            f"Website: {SITE}/?lang=en")


def _plans_keyboard(ru: bool) -> dict:
    return {"inline_keyboard": [[{"text": f"{plan_title(p, ru)}: {p.price_stars} ⭐", "callback_data": f"buy:{p.id}"}]
                                for p in PLANS.values() if p.price_stars]}


def _key_text(key: str, plan, ru: bool) -> str:
    if ru:
        return (f"Оплата получена, спасибо!\n\nТвой ключ ProxyBridge ({plan_title(plan, True)}):\n{key}\n\n"
                "1. Скачай программу: https://www.proxybridge.org/skachat/\n"
                "2. Вставь ключ в окно активации.\n"
                "Ключ работает на 2 устройствах. Сохрани это сообщение.\n\n"
                "Вопросы: /paysupport")
    return (f"Payment received, thank you!\n\nYour ProxyBridge key ({plan_title(plan, False)}):\n{key}\n\n"
            "1. Download the app: https://www.proxybridge.org/skachat/?lang=en\n"
            "2. Paste the key into the activation window.\n"
            "The key works on 2 devices. Keep this message.\n\n"
            "Questions: /paysupport")


def _support_text(ru: bool) -> str:
    if ru:
        return ("Поддержка по оплате: support@proxybridge.org или @inter1ark.\n"
                "Напиши номер заказа или ключ, ответим в течение суток. Если ключ не пришёл или не активируется, "
                "поможем или вернём звёзды. Условия возврата: https://www.proxybridge.org/legal/refund.html")
    return ("Payment support: support@proxybridge.org or @inter1ark.\n"
            "Send the key or the order details and we reply within a day. If the key did not arrive or does not "
            "activate, we help or refund the Stars. Refund terms: https://www.proxybridge.org/legal/refund.html")


def _terms_text(ru: bool) -> str:
    if ru:
        return ("Покупка ключа означает согласие с офертой: https://www.proxybridge.org/legal/offer.html\n"
                "Политика конфиденциальности: https://www.proxybridge.org/legal/privacy.html")
    return ("Buying a key means you accept the terms: https://www.proxybridge.org/legal/offer.html\n"
            "Privacy policy: https://www.proxybridge.org/legal/privacy.html")


def _invoice(chat_id: int, order: Order, ru: bool) -> dict:
    plan = get_public_plan(order.plan_id)
    days = (f"на {plan.days} дней" if ru else f"for {plan.days} days") if plan.days else ("без срока" if ru else "with no expiry")
    description = (f"Ключ лицензии ProxyBridge {days}, до 2 устройств. Ключ придёт в этот чат сразу после оплаты."
                   if ru else f"ProxyBridge license key {days}, up to 2 devices. The key arrives in this chat right after the payment.")
    if order.renew_key:
        description = (f"Продление ключа {order.renew_key} {days}. Срок добавится к текущему, вводить ключ заново не нужно."
                       if ru else f"Renewal of the key {order.renew_key} {days}. The time is added to the current end, "
                                  "no need to enter the key again.")
    return {"method": "sendInvoice", "chat_id": chat_id, "title": f"ProxyBridge: {plan_title(plan, ru)}"[:32],
            "description": description, "payload": order.token, "currency": CURRENCY, "provider_token": "",
            "prices": [{"label": plan_title(plan, ru), "amount": order.amount_stars}]}


def _message(chat_id: int, text: str, **extra) -> dict:
    return {"method": "sendMessage", "chat_id": chat_id, "text": text, "disable_web_page_preview": True, **extra}


# ---------------------------------------------------------------------------
# Update handling: returns the method call to put into the webhook reply (or {})
# ---------------------------------------------------------------------------

def handle_update(db: Session, update: dict) -> dict:
    if isinstance(update.get("pre_checkout_query"), dict):
        return _pre_checkout(db, update["pre_checkout_query"])
    if isinstance(update.get("callback_query"), dict):
        return _callback(db, update["callback_query"])
    msg = update.get("message")
    if not isinstance(msg, dict):
        return {}
    chat_id = (msg.get("chat") or {}).get("id")
    ru = is_ru((msg.get("from") or {}).get("language_code"))
    if isinstance(msg.get("successful_payment"), dict):
        return _paid(db, msg, chat_id, ru)
    if not chat_id or (msg.get("chat") or {}).get("type") != "private":
        return {}
    text = (msg.get("text") or "").strip()
    if text.startswith("/start"):
        arg = text[6:].strip()
        if arg:
            return _start_order(db, chat_id, arg, ru)
        return _message(chat_id, _plans_text(ru), reply_markup=_plans_keyboard(ru))
    if text.startswith("/paysupport") or text.startswith("/support"):
        return _message(chat_id, _support_text(ru))
    if text.startswith("/terms"):
        return _message(chat_id, _terms_text(ru))
    return _message(chat_id, _plans_text(ru), reply_markup=_plans_keyboard(ru))


def _start_order(db: Session, chat_id: int, token: str, ru: bool) -> dict:
    """/start <order token> from the site: send the invoice for that order."""
    order = db.scalar(select(Order).where(Order.token == token, Order.method == METHOD))
    if order is None:
        return _message(chat_id, _plans_text(ru), reply_markup=_plans_keyboard(ru))
    plan = get_public_plan(order.plan_id)
    if order.status == "paid" and order.key and plan:
        return _message(chat_id, _paid_text(db, order, plan, ru))
    if order.status != "pending" or plan is None:
        return _message(chat_id, ("Этот заказ уже закрыт. Выбери тариф заново:" if ru else
                                  "This order is closed. Pick a plan again:"), reply_markup=_plans_keyboard(ru))
    return _invoice(chat_id, order, ru)


def _callback(db: Session, cq: dict) -> dict:
    data = cq.get("data") or ""
    user = cq.get("from") or {}
    chat_id = ((cq.get("message") or {}).get("chat") or {}).get("id") or user.get("id")
    ru = is_ru(user.get("language_code"))
    answer_callback_later(cq.get("id"))
    if data.startswith("ren:"):
        return _renew_callback(db, data, user, chat_id, ru)
    plan = get_public_plan(data[4:]) if data.startswith("buy:") else None
    if plan is None or not plan.price_stars or not chat_id:
        return {}
    return _invoice(chat_id, _bot_order(db, plan, chat_id), ru)


def _renew_callback(db: Session, data: str, user: dict, chat_id, ru: bool) -> dict:
    """A renewal button from the reminder: only the Telegram user who bought the key may use it."""
    parts = data.split(":")
    if len(parts) != 3 or not parts[2].isdigit():
        return {}
    plan = get_public_plan(parts[1])
    lic = db.get(License, int(parts[2]))
    if plan is None or not plan.price_stars or lic is None or not chat_id or lic.revoked:
        return {}
    if buyer_chat(db, lic) != user.get("id"):
        return _message(chat_id, _support_text(ru))
    if lic.expires_at is None and lic.plan_id != "trial":
        return _message(chat_id, "Этот ключ уже бессрочный, продлевать не нужно." if ru else
                        "This key never expires, no renewal needed.")
    return _invoice(chat_id, _bot_order(db, plan, chat_id, renew_key=lic.key), ru)


def _paid_text(db: Session, order: Order, plan, ru: bool) -> str:
    if not order.renew_key:
        return _key_text(order.key, plan, ru)
    lic = db.scalar(select(License).where(License.key == order.renew_key))
    end = end_text(lic, ru) if lic else ""
    if ru:
        return (f"Оплата получена, спасибо!\n\nКлюч {order.renew_key} продлён, теперь он действует {end}.\n"
                "Вводить ключ заново не нужно. Если программа показала, что срок истёк, перезапусти её "
                "или вставь этот же ключ в окно активации.\n\nВопросы: /paysupport")
    return (f"Payment received, thank you!\n\nThe key {order.renew_key} is renewed, it now works {end}.\n"
            "No need to enter it again. If the app said the key has expired, restart it or paste the same key "
            "into the activation window.\n\nQuestions: /paysupport")


def _pre_checkout(db: Session, q: dict) -> dict:
    """Telegram asks whether to take the money; it must be answered within 10 seconds."""
    ru = is_ru((q.get("from") or {}).get("language_code"))
    order = db.scalar(select(Order).where(Order.token == (q.get("invoice_payload") or ""), Order.method == METHOD))
    ok = (order is not None and order.status == "pending" and q.get("currency") == CURRENCY
          and q.get("total_amount") == order.amount_stars)
    reply = {"method": "answerPreCheckoutQuery", "pre_checkout_query_id": q.get("id"), "ok": ok}
    if not ok:
        logger.warning("stars pre-checkout rejected: payload=%s amount=%s", q.get("invoice_payload"), q.get("total_amount"))
        reply["error_message"] = ("Заказ устарел. Нажми /start и выбери тариф заново." if ru else
                                  "This order is out of date. Press /start and pick a plan again.")
    return reply


def _paid(db: Session, msg: dict, chat_id: int, ru: bool) -> dict:
    sp = msg["successful_payment"]
    user_id = (msg.get("from") or {}).get("id")
    order = db.scalar(select(Order).where(Order.token == (sp.get("invoice_payload") or ""), Order.method == METHOD))
    if order is None or sp.get("currency") != CURRENCY or sp.get("total_amount") != order.amount_stars:
        logger.error("stars payment without a matching order: %s", sp)
        from . import notify
        notify.send_admin(f"⚠️ Оплата звёздами без заказа: {sp.get('total_amount')} ⭐, "
                          f"charge {sp.get('telegram_payment_charge_id')}, user {user_id}")
        return _message(chat_id, _support_text(ru)) if chat_id else {}
    if order.status != "paid":
        # user id and charge id are what refundStarPayment needs
        order.provider_payment_id = f"{user_id}:{sp.get('telegram_payment_charge_id')}"[:128]
        order.pay_type = CURRENCY
        order.income_rub = round(order.amount_stars * settings.STARS_RUB_RATE, 2)
        order.checked_at = to_db(utcnow())
    mark_order_paid(db, order)
    plan = get_public_plan(order.plan_id)
    return _message(chat_id, _paid_text(db, order, plan, ru)) if chat_id and plan else {}


# ---------------------------------------------------------------------------
# Outgoing calls (best effort, through TELEGRAM_PROXY)
# ---------------------------------------------------------------------------

def api(method: str, payload: dict | None = None, timeout: float = 20) -> dict:
    url = f"https://api.telegram.org/bot{settings.STARS_BOT_TOKEN}/{method}"
    with httpx.Client(timeout=timeout, proxy=settings.TELEGRAM_PROXY or None) as client:
        return client.post(url, json=payload or {}).json()


def poller(stop: threading.Event) -> None:
    """Long polling: Telegram cannot open connections to this server, so the bot fetches its
    updates through TELEGRAM_PROXY and sends each answer with the same kept-alive connection
    (about 0.3 s a call; a pre-checkout query must be answered within 10 s)."""
    from . import db as dbmod

    offset = None
    client = None
    webhook_dropped = False
    while not stop.is_set():
        try:
            if client is None:
                client = httpx.Client(timeout=httpx.Timeout(45, connect=15), proxy=settings.TELEGRAM_PROXY or None)
            base = f"https://api.telegram.org/bot{settings.STARS_BOT_TOKEN}/"
            if not webhook_dropped:
                client.post(base + "deleteWebhook", json={"drop_pending_updates": False})
                webhook_dropped = True
            params = {"timeout": 25, "allowed_updates": ["message", "callback_query", "pre_checkout_query"]}
            if offset is not None:
                params["offset"] = offset
            data = client.post(base + "getUpdates", json=params).json()
            if not data.get("ok"):
                logger.warning("stars getUpdates: %s", data.get("description"))
                stop.wait(5)
                continue
            for update in data.get("result") or []:
                with dbmod.SessionLocal() as session:
                    try:
                        reply = handle_update(session, update)
                    except Exception:  # noqa: BLE001
                        session.rollback()
                        logger.exception("stars update %s failed", update.get("update_id"))
                        if isinstance((update.get("message") or {}).get("successful_payment"), dict):
                            raise  # keep the offset: the paid update comes again on the next call
                        reply = {}
                if reply:
                    _send(client, base, reply)
                offset = update["update_id"] + 1
        except Exception as exc:  # noqa: BLE001
            logger.warning("stars poller: %s", exc)
            if client is not None:
                client.close()
                client = None
            stop.wait(3)
    if client is not None:
        client.close()


def _send(client: httpx.Client, base: str, reply: dict) -> None:
    payload = dict(reply)
    method = payload.pop("method")
    for attempt in range(3):
        try:
            r = client.post(base + method, json=payload).json()
            if not r.get("ok"):
                logger.warning("stars %s: %s", method, r.get("description"))
            return
        except Exception as exc:  # noqa: BLE001
            logger.info("stars %s attempt %d failed: %s", method, attempt + 1, exc)
    logger.error("stars %s failed 3 times", method)


def answer_callback_later(callback_id: str | None) -> None:
    """Stops the button spinner. Not needed for the purchase, so it runs in the background."""
    if not callback_id or not settings.STARS_BOT_TOKEN:
        return

    def run():
        try:
            api("answerCallbackQuery", {"callback_query_id": callback_id})
        except Exception as exc:  # noqa: BLE001
            logger.info("answerCallbackQuery failed: %s", exc)

    threading.Thread(target=run, daemon=True, name="stars-callback").start()
