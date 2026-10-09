"""Dedicated proxy vendor client (fixed term, unlimited traffic).

Auth is the X-Api-Key header. Requests may go through CY_HTTP_PROXY because the
API is not reachable from every network. Purchases are NOT idempotent: a buy
call is made at most once per order, the caller records the result first.
"""

import hashlib
import logging
import threading
import time

import httpx

from ..config import settings

logger = logging.getLogger("proxybridge.store.cy")

BASE_URL = "https://app.cyberyozh.com/api"
TIMEOUT = 40.0


class CyError(Exception):
    def __init__(self, message: str, status: int | None = None, sent: bool = False):
        super().__init__(message)
        self.status = status
        # True when the request may have reached the vendor (outcome unknown).
        self.sent = sent


class Account:
    def __init__(self, key: str):
        self.key = key
        self.fp = hashlib.sha256(key.encode()).hexdigest()[:12]

    def __repr__(self) -> str:
        return f"<cy {self.fp}>"


def accounts() -> list[Account]:
    return [Account(k) for k in settings.cy_keys]


def account_by_fp(fp: str) -> Account | None:
    return next((a for a in accounts() if a.fp == fp), None)


def _request(acc: Account, method: str, path: str, params: dict | None = None, body=None):
    headers = {"X-Api-Key": acc.key, "Accept": "application/json"}
    sent = False
    try:
        with httpx.Client(timeout=TIMEOUT, proxy=settings.CY_HTTP_PROXY or None) as client:
            req = client.build_request(method, BASE_URL + path, params=params, json=body, headers=headers)
            sent = True
            resp = client.send(req)
    except httpx.ConnectError as exc:
        raise CyError(f"{method} {path}: connect failed", sent=False) from exc
    except Exception as exc:  # noqa: BLE001
        raise CyError(f"{method} {path}: {type(exc).__name__}", sent=sent) from exc
    try:
        data = resp.json()
    except ValueError:
        data = None
    if resp.status_code >= 400:
        detail = data.get("detail") if isinstance(data, dict) else ""
        raise CyError(f"{method} {path}: {resp.status_code} {detail}"[:250], resp.status_code)
    return data


def balance(acc: Account) -> float:
    data = _request(acc, "GET", "/v2/users/balance/")
    if isinstance(data, dict):
        data = data.get("balance", 0)
    return float(data or 0)


_cache: dict[str, tuple[float, object]] = {}
_cache_lock = threading.Lock()


def _products() -> list[dict]:
    """Private datacenter products in stock, cached for 15 minutes."""
    now = time.time()
    with _cache_lock:
        hit = _cache.get("dc")
        if hit and now - hit[0] < 900:
            return hit[1]
    accs = accounts()
    if not accs:
        raise CyError("no accounts configured")
    out = []
    params = {"proxy_category": "datacenter", "access_type": "private", "page_size": 100, "page": 1}
    for _ in range(20):
        data = _request(accs[0], "GET", "/v1/proxies/shop/", params) or {}
        for group in data.get("results", []):
            if group.get("access_type") != "private":
                continue
            for p in group.get("proxy_products", []):
                if p.get("stock_status") == "in_stock" and p.get("proxy_category") == "datacenter":
                    out.append(p)
        if not data.get("next"):
            break
        params["page"] += 1
    with _cache_lock:
        _cache["dc"] = (now, out)
    return out


def _price(p: dict) -> float:
    return float(p.get("discounted_price") or p.get("price_usd") or 0)


def dc_countries(days: int) -> list[str]:
    """ISO codes that have a private datacenter product for the term."""
    codes = {str(p.get("location_country_code") or "").upper() for p in _products() if p.get("days") == days}
    return sorted(c for c in codes if len(c) == 2)


def dc_product(country_code: str, days: int) -> dict | None:
    """Cheapest in-stock private datacenter product for the country and term."""
    code = (country_code or "").upper()
    items = [p for p in _products()
             if p.get("days") == days and str(p.get("location_country_code") or "").upper() == code]
    return min(items, key=_price) if items else None


def product_price(p: dict) -> float:
    return _price(p)


def buy(acc: Account, product_id: str) -> str:
    """Buy one proxy without auto renewal. Returns the vendor order id.

    Raises CyError with sent=True when the outcome is unknown: never retry then.
    """
    data = _request(acc, "POST", "/v1/proxies/shop/buy_proxies/",
                    body=[{"id": product_id, "auto_renew": False, "quantity": 1}])
    item = data[0] if isinstance(data, list) and data else {}
    if item.get("status") == "canceled":
        raise CyError(f"buy canceled: {item.get('code') or item.get('message')}"[:250], 400)
    ids = item.get("order_ids") or []
    if not ids:
        raise CyError(f"buy: no order id ({item.get('status')})", sent=True)
    return str(ids[0])


def find_order(acc: Account, order_id: str, max_pages: int = 10) -> dict | None:
    """The purchased proxy record by vendor order id (newest first in history)."""
    params = {"page": 1, "page_size": 100}
    for _ in range(max_pages):
        data = _request(acc, "GET", "/v1/proxies/history/", params) or {}
        for item in data.get("results", []):
            if str(item.get("id")) == str(order_id):
                return item
        if not data.get("next"):
            return None
        params["page"] += 1
    return None


def set_auto_renew(acc: Account, order_id: str, enabled: bool) -> None:
    _request(acc, "PATCH", f"/v1/proxies/history/{order_id}/", body={"auto_renew_request": bool(enabled)})
