"""Crypto Pay (CryptoBot) API client (sync, httpx).

CryptoBot has no webhook in this setup, so the site polls
POST /api/order/{token}/check which calls get_invoice.
"""

import logging

import httpx

from ..config import settings

logger = logging.getLogger(__name__)

API_URL = "https://pay.crypt.bot/api"
TIMEOUT = 20.0


class CryptoPayError(Exception):
    pass


def _headers() -> dict:
    if not settings.CRYPTOPAY_API_TOKEN:
        raise CryptoPayError("CryptoBot is not configured (CRYPTOPAY_API_TOKEN)")
    return {"Crypto-Pay-API-Token": settings.CRYPTOPAY_API_TOKEN}


def _request(method: str, params: dict | None = None):
    try:
        with httpx.Client(timeout=TIMEOUT) as client:
            resp = client.get(f"{API_URL}/{method}", headers=_headers(), params=params or {})
            data = resp.json()
    except CryptoPayError:
        raise
    except Exception as exc:
        raise CryptoPayError(f"request failed: {exc}") from exc
    if not data.get("ok"):
        raise CryptoPayError(f"{method} failed: {data.get('error')}")
    return data.get("result")


def create_invoice(amount_rub: int, description: str, payload: str, expires_in: int = 3600,
                   paid_btn_url: str | None = None) -> dict:
    """Create a fiat RUB invoice. Returns {"invoice_id", "pay_url", "status"}."""
    params = {
        "currency_type": "fiat",
        "fiat": "RUB",
        "amount": str(amount_rub),
        "description": description[:1024],
        "payload": payload,
        "expires_in": str(expires_in),
        "allow_comments": "false",
        "allow_anonymous": "true",
    }
    # CryptoBot accepts only https links for the "paid" button.
    if paid_btn_url and paid_btn_url.startswith("https://"):
        params["paid_btn_name"] = "callback"
        params["paid_btn_url"] = paid_btn_url
    result = _request("createInvoice", params) or {}
    pay_url = result.get("bot_invoice_url") or result.get("pay_url") or ""
    if not result.get("invoice_id") or not pay_url:
        raise CryptoPayError("createInvoice: response without invoice_id or url")
    logger.info("CryptoBot invoice created id=%s", result["invoice_id"])
    return {"invoice_id": str(result["invoice_id"]), "pay_url": pay_url, "status": result.get("status", "active")}


def get_invoice(invoice_id: str) -> dict | None:
    """Return the invoice dict for the given id, or None if not found.

    The dict always has "paid_asset" (crypto asset used to pay, e.g. USDT, or None).
    """
    result = _request("getInvoices", {"invoice_ids": str(invoice_id)})
    items = result.get("items", []) if isinstance(result, dict) else (result or [])
    for inv in items:
        if str(inv.get("invoice_id")) == str(invoice_id):
            inv = dict(inv)
            inv["paid_asset"] = inv.get("paid_asset") or None
            return inv
    return None
