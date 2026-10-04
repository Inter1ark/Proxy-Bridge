"""YooKassa REST API v3 client (sync, httpx).

Only two calls are needed: create a redirect payment and fetch a payment by id.
The webhook handler never trusts the notification body; it calls get_payment.
"""

import logging
import uuid

import httpx

from ..config import settings

logger = logging.getLogger(__name__)

API_URL = "https://api.yookassa.ru/v3"
TIMEOUT = 20.0


class YooKassaError(Exception):
    pass


def _auth() -> tuple[str, str]:
    if not settings.YOOKASSA_SHOP_ID or not settings.YOOKASSA_SECRET_KEY:
        raise YooKassaError("YooKassa is not configured (YOOKASSA_SHOP_ID / YOOKASSA_SECRET_KEY)")
    return (settings.YOOKASSA_SHOP_ID, settings.YOOKASSA_SECRET_KEY)


def create_payment(amount_rub: int, description: str, order_token: str, return_url: str) -> dict:
    """Create a payment with redirect confirmation and automatic capture.

    Returns {"id": ..., "confirmation_url": ..., "status": ...}.
    """
    payload = {
        "amount": {"value": f"{amount_rub:.2f}", "currency": "RUB"},
        "capture": True,
        "confirmation": {"type": "redirect", "return_url": return_url},
        "description": description[:128],
        "metadata": {"order_token": order_token},
    }
    headers = {"Idempotence-Key": str(uuid.uuid4()), "Content-Type": "application/json"}
    try:
        with httpx.Client(auth=_auth(), timeout=TIMEOUT) as client:
            resp = client.post(f"{API_URL}/payments", json=payload, headers=headers)
            data = resp.json()
    except YooKassaError:
        raise
    except Exception as exc:
        raise YooKassaError(f"request failed: {exc}") from exc
    if resp.status_code not in (200, 201):
        raise YooKassaError(f"create payment failed: {resp.status_code} {data.get('description', data)}")
    confirmation_url = (data.get("confirmation") or {}).get("confirmation_url", "")
    if not data.get("id") or not confirmation_url:
        raise YooKassaError("create payment: response without id or confirmation_url")
    logger.info("YooKassa payment created id=%s status=%s", data["id"], data.get("status"))
    return {"id": data["id"], "confirmation_url": confirmation_url, "status": data.get("status", "pending")}


def get_payment(payment_id: str) -> dict:
    """Fetch a payment. Returns {"id", "status", "paid", "amount", "metadata"}."""
    try:
        with httpx.Client(auth=_auth(), timeout=TIMEOUT) as client:
            resp = client.get(f"{API_URL}/payments/{payment_id}")
            data = resp.json()
    except YooKassaError:
        raise
    except Exception as exc:
        raise YooKassaError(f"request failed: {exc}") from exc
    if resp.status_code != 200:
        raise YooKassaError(f"get payment failed: {resp.status_code} {data.get('description', data)}")
    return {
        "id": data.get("id"),
        "status": data.get("status"),
        "paid": bool(data.get("paid", False)),
        "amount": data.get("amount") or {},
        "metadata": data.get("metadata") or {},
    }
