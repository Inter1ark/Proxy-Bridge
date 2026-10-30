"""Telegram notifications to admins about paid orders.

Uses the Bot API only for sending, so the same bot token can be shared with
another bot process (sending does not conflict with polling).
Configured by TELEGRAM_BOT_TOKEN and TELEGRAM_ADMIN_IDS (comma separated).
"""

import logging
import threading
import time

import httpx

from .config import settings
from .plans import get_plan

logger = logging.getLogger("proxybridge.notify")


def _admin_ids() -> list[int]:
    ids = []
    for part in (settings.TELEGRAM_ADMIN_IDS or "").replace(";", ",").split(","):
        part = part.strip()
        if part.lstrip("-").isdigit():
            ids.append(int(part))
    return ids


def notify_paid_order(order, key: str) -> None:
    """Send a purchase summary to every admin. Never raises."""
    plan = get_plan(order.plan_id)
    title = plan.title if plan else order.plan_id
    price = f"{plan.price_rub} ₽" if plan else ""
    method = {"yookassa": "ЮKassa", "cryptobot": "CryptoBot"}.get(order.method, order.method)
    text = (
        "🟦 ProxyBridge: новая оплата\n"
        f"Тариф: {title} {price}\n"
        f"Способ: {method}\n"
        f"Email: {order.email}\n"
        f"Ключ: {key}\n"
        f"Заказ: {order.id}"
    )
    send_admin(text)


RETRY_DELAYS = (3, 10, 30)  # seconds between attempts; the SOCKS tunnel to Telegram sometimes drops a handshake


def _send_one(url: str, chat_id: int, text: str) -> None:
    """Deliver to one chat with retries. Network errors and 5xx/429 are retried, other 4xx are final."""
    for attempt in range(len(RETRY_DELAYS) + 1):
        try:
            # TELEGRAM_PROXY (e.g. socks5://127.0.0.1:1081) is used when the host cannot reach Telegram directly
            with httpx.Client(timeout=20, proxy=settings.TELEGRAM_PROXY or None) as client:
                resp = client.post(url, json={"chat_id": chat_id, "text": text})
            if resp.status_code == 200:
                if attempt:
                    logger.info("telegram notify to %s delivered on attempt %d", chat_id, attempt + 1)
                return
            if resp.status_code < 500 and resp.status_code != 429:
                logger.warning("telegram notify to %s failed: %s %s", chat_id, resp.status_code, resp.text[:200])
                return
            reason = f"{resp.status_code} {resp.text[:120]}"
        except Exception as exc:  # noqa: BLE001
            reason = str(exc)
        if attempt < len(RETRY_DELAYS):
            logger.info("telegram notify to %s attempt %d failed (%s), retrying", chat_id, attempt + 1, reason)
            time.sleep(RETRY_DELAYS[attempt])
        else:
            logger.warning("telegram notify to %s failed after %d attempts: %s", chat_id, attempt + 1, reason)


def send_admin(text: str) -> None:
    """Send a message to every admin in the background (with retries). Never raises, never blocks."""
    token = settings.TELEGRAM_BOT_TOKEN
    ids = _admin_ids()
    if not token or not ids:
        return
    url = f"https://api.telegram.org/bot{token}/sendMessage"
    for chat_id in ids:
        threading.Thread(target=_send_one, args=(url, chat_id, text), daemon=True,
                         name=f"tg-notify-{chat_id}").start()
