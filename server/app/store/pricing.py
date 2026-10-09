"""Store prices in RUB.

Dedicated proxies have a fixed price. Traffic is sold at our cost per GB,
converted at the central bank rate, plus the payment fee (and an optional
markup), rounded up to a whole ruble.
"""

import logging
import math
import threading
import time

import httpx

from ..config import settings

logger = logging.getLogger("proxybridge.store.pricing")

TRAFFIC_TYPES = ("mobile", "residential", "datacenter")
RATE_URL = "https://www.cbr-xml-daily.ru/daily_json.js"
RATE_TTL = 6 * 3600

_rate: dict = {"value": 0.0, "at": 0.0}
_lock = threading.Lock()


class PriceError(Exception):
    pass


def usd_rub() -> float:
    """USD to RUB rate of the central bank, cached; falls back to the last value or the configured one."""
    now = time.time()
    with _lock:
        if _rate["value"] and now - _rate["at"] < RATE_TTL:
            return _rate["value"]
    try:
        with httpx.Client(timeout=10) as client:
            data = client.get(RATE_URL).json()
        value = float(data["Valute"]["USD"]["Value"])
        if not 10 < value < 1000:
            raise ValueError(f"implausible rate {value}")
        with _lock:
            _rate.update(value=value, at=now)
        return value
    except Exception as exc:  # noqa: BLE001
        logger.warning("USD rate fetch failed: %s", exc)
    with _lock:
        if _rate["value"]:
            return _rate["value"]
    if settings.STORE_USD_RUB_FALLBACK > 0:
        return settings.STORE_USD_RUB_FALLBACK
    raise PriceError("usd rate unavailable")


def usd_per_gb() -> dict[str, float]:
    """Selling price basis per GB in USD, by type."""
    out = settings.parse_prices(settings.STORE_SX_USD_PER_GB)
    return {t: out[t] for t in TRAFFIC_TYPES if t in out and out[t] > 0}


def _factor() -> float:
    fee = min(max(settings.STORE_FEE_PCT, 0.0), 50.0)
    markup = max(settings.STORE_MARKUP_PCT, 0.0)
    return (1 + markup / 100) / (1 - fee / 100)


def gb_price_rub(ptype: str, rate: float | None = None) -> float:
    """Price of one GB before rounding (for display)."""
    usd = usd_per_gb().get(ptype)
    if usd is None:
        raise PriceError(f"unknown type {ptype}")
    return usd * (rate or usd_rub()) * _factor()


def traffic_price_rub(ptype: str, gb: int, rate: float | None = None) -> int:
    return int(math.ceil(gb_price_rub(ptype, rate) * gb - 1e-9))


def traffic_cost_usd(ptype: str, gb: int) -> float:
    """Upper bound of what the GB cost us (accounts above the price basis are never used)."""
    return usd_per_gb()[ptype] * gb


def dc_price_rub() -> int:
    return settings.STORE_DC_PRICE_RUB
