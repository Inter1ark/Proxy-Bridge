"""In-app proxy store: catalog, orders, payments, fulfillment and usage sync.

Products:
  dc        dedicated datacenter proxy for a fixed term, unlimited traffic
  dc_renew  one more term for a dedicated proxy (vendor auto renewal is armed once)
  traffic   pay per GB port: country, optional region and city, type, rotation
  topup     more GB for an existing traffic port

Flow: order -> payment (YooKassa / CryptoBot) -> fulfill = queued -> the store
worker creates the proxy at the vendor -> done. Vendor names never reach the
client and proxies are created without a name.
"""

import json
import logging
import re
import secrets
import time
from datetime import timedelta
from decimal import Decimal, InvalidOperation
from pathlib import Path
from urllib.parse import quote

import httpx
from sqlalchemy import select, update
from sqlalchemy.orm import Session

from .. import notify
from ..config import settings
from ..errors import ApiError
from ..keys import normalize_key
from ..models import License, StoreOrder, StoreProxy, from_db, iso_z, to_db, utcnow
from ..providers import cryptopay, yookassa
from . import cy, pricing, sx

logger = logging.getLogger("proxybridge.store")

PRODUCTS = ("dc", "dc_renew", "traffic", "topup")
METHODS = ("yookassa", "cryptobot")
ROTATIONS = ("static", "interval", "request")
TTL_MAX = 1440
PAYMENT_MAX_AGE = timedelta(days=2)
PROBE_URL = "http://ip-api.com/json/?fields=status,countryCode"
PROBE_GIVE_UP = timedelta(minutes=3)
CY_GIVE_UP = timedelta(minutes=30)
SYNC_EVERY_SEC = 600

_HWID_RE = re.compile(r"^[0-9a-f]{64}$")
COUNTRY_NAMES: dict[str, list[str]] = json.loads(
    (Path(__file__).with_name("countries.json")).read_text(encoding="utf-8"))


def country_info(code: str) -> dict:
    code = (code or "").upper()
    ru, en = COUNTRY_NAMES.get(code, [code, code])
    return {"code": code, "ru": ru, "en": en}


# ----------------------------------------------------------------------------
# Auth: the client identifies itself with its license key and device id.
# ----------------------------------------------------------------------------

_CABINET_RE = re.compile(r"^W-[A-Za-z0-9_-]{30}$")


class WebOwner:
    """A buyer on the website: identified by a secret cabinet token kept in the browser."""

    def __init__(self, token: str):
        self.key = token


def auth(db: Session, key: str, hwid: str, cabinet: str = ""):
    """License key + device id (app) or a cabinet token (website). Returns an object with .key."""
    if cabinet:
        if not _CABINET_RE.match(cabinet):
            raise ApiError(400, "bad_request")
        return WebOwner(cabinet)
    k = normalize_key(key or "")
    h = (hwid or "").strip().lower()
    if k is None or not _HWID_RE.match(h):
        raise ApiError(400, "bad_request")
    lic = db.scalar(select(License).where(License.key == k))
    if lic is None:
        raise ApiError(404, "not_found")
    if lic.revoked:
        raise ApiError(403, "revoked")
    if lic.is_expired():
        raise ApiError(410, "expired")
    if not any(d.hwid == h for d in lic.devices):
        raise ApiError(403, "not_activated")
    return lic


# ----------------------------------------------------------------------------
# Catalog
# ----------------------------------------------------------------------------

def catalog() -> dict:
    out: dict = {"methods": list(METHODS), "dc": None, "traffic": None}
    if not settings.STORE_ENABLED:
        return out
    if cy.accounts():
        try:
            codes = cy.dc_countries(settings.STORE_DC_DAYS)
            out["dc"] = {"price_rub": pricing.dc_price_rub(), "days": settings.STORE_DC_DAYS,
                         "countries": [country_info(c) for c in codes]}
        except cy.CyError as exc:
            logger.warning("catalog: dedicated list failed: %s", exc)
    if sx.accounts():
        try:
            rate = pricing.usd_rub()
            prices = {t: round(pricing.gb_price_rub(t, rate), 2) for t in pricing.usd_per_gb()}
            countries = []
            for c in sx.countries():
                info = country_info(c["code"])
                info["id"] = c["id"]
                countries.append(info)
            out["traffic"] = {"gb_price_rub": prices, "min_gb": settings.STORE_MIN_GB,
                              "max_gb": settings.STORE_MAX_GB, "ttl_max": TTL_MAX, "countries": countries}
        except (sx.SxError, pricing.PriceError) as exc:
            logger.warning("catalog: traffic list failed: %s", exc)
    return out


def states(country_id: int) -> list[dict]:
    try:
        return sx.states(int(country_id))
    except (sx.SxError, ValueError, TypeError):
        raise ApiError(502, "vendor_error")


def cities(country_id: int, state_id: int) -> list[dict]:
    try:
        return sx.cities(int(country_id), int(state_id))
    except (sx.SxError, ValueError, TypeError):
        raise ApiError(502, "vendor_error")


def availability(country_code: str, ptype: str) -> bool:
    if ptype not in pricing.TRAFFIC_TYPES:
        raise ApiError(400, "bad_request")
    try:
        return sx.available(country_code, ptype)
    except sx.SxError:
        raise ApiError(502, "vendor_error")


# ----------------------------------------------------------------------------
# Validation and prices
# ----------------------------------------------------------------------------

def _int(value, default=None):
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _own_proxy(db: Session, lic: License, proxy_id) -> StoreProxy:
    p = db.get(StoreProxy, _int(proxy_id, 0) or 0)
    if p is None or p.license_key != lic.key:
        raise ApiError(404, "proxy_not_found")
    return p


def validate(db: Session, lic: License, product: str, params: dict) -> tuple[dict, int, float]:
    """Check an order and return (clean params, price RUB, our cost USD)."""
    params = params or {}
    if product not in PRODUCTS:
        raise ApiError(400, "bad_product")
    days = settings.STORE_DC_DAYS

    if product == "dc":
        code = str(params.get("country") or "").upper()
        try:
            item = cy.dc_product(code, days)
        except cy.CyError:
            raise ApiError(502, "vendor_error")
        if item is None:
            raise ApiError(409, "unavailable")
        return {"country": code}, pricing.dc_price_rub(), cy.product_price(item)

    if product == "dc_renew":
        p = _own_proxy(db, lic, params.get("proxy_id"))
        expires = from_db(p.expires_at)
        if p.kind != "dc" or p.status != "active" or not expires or expires <= utcnow() + timedelta(minutes=30):
            raise ApiError(409, "cannot_renew")
        if p.renew_pending > 0 or _open_order_for(db, p.id):
            raise ApiError(409, "renew_pending")
        try:
            item = cy.dc_product(p.country_code, days)
        except cy.CyError:
            item = None
        cost = cy.product_price(item) if item else 0.0
        return {"proxy_id": p.id}, pricing.dc_price_rub(), cost

    gb = _int(params.get("gb"), 0)
    if not settings.STORE_MIN_GB <= gb <= settings.STORE_MAX_GB:
        raise ApiError(400, "bad_gb")

    if product == "topup":
        p = _own_proxy(db, lic, params.get("proxy_id"))
        if p.kind != "traffic" or p.status not in ("active", "exhausted"):
            raise ApiError(409, "cannot_topup")
        if p.gb_total + gb > 10_000:
            raise ApiError(400, "bad_gb")
        try:
            return {"proxy_id": p.id, "gb": gb}, pricing.traffic_price_rub(p.ptype, gb), \
                pricing.traffic_cost_usd(p.ptype, gb)
        except pricing.PriceError:
            raise ApiError(503, "price_unavailable")

    # product == "traffic"
    ptype = str(params.get("type") or "")
    rotation = str(params.get("rotation") or "")
    if ptype not in pricing.usd_per_gb() or rotation not in ROTATIONS:
        raise ApiError(400, "bad_request")
    ttl = None
    if rotation == "interval":
        ttl = _int(params.get("ttl"), 0)
        if not 1 <= ttl <= TTL_MAX:
            raise ApiError(400, "bad_ttl")
    try:
        country = sx.country_by_code(str(params.get("country") or ""))
    except sx.SxError:
        raise ApiError(502, "vendor_error")
    if country is None:
        raise ApiError(400, "bad_country")
    clean = {"country": country["code"], "country_id": country["id"], "type": ptype,
             "rotation": rotation, "ttl": ttl, "gb": gb, "state_id": None, "state": None,
             "city_id": None, "city": None}
    state_id = _int(params.get("state_id"))
    if state_id:
        st = next((s for s in states(country["id"]) if s["id"] == state_id), None)
        if st is None:
            raise ApiError(400, "bad_state")
        clean.update(state_id=st["id"], state=st["name"])
        city_id = _int(params.get("city_id"))
        if city_id:
            ct = next((c for c in cities(country["id"], st["id"]) if c["id"] == city_id), None)
            if ct is None:
                raise ApiError(400, "bad_city")
            clean.update(city_id=ct["id"], city=ct["name"])
    if not availability(country["code"], ptype):
        raise ApiError(409, "unavailable")
    try:
        return clean, pricing.traffic_price_rub(ptype, gb), pricing.traffic_cost_usd(ptype, gb)
    except pricing.PriceError:
        raise ApiError(503, "price_unavailable")


def _open_order_for(db: Session, proxy_id: int) -> bool:
    """A paid renewal for this proxy that is not processed yet."""
    rows = db.scalars(select(StoreOrder).where(StoreOrder.product == "dc_renew",
                                               StoreOrder.status == "paid",
                                               StoreOrder.fulfill.in_(("queued", "working")))).all()
    return any(json.loads(o.params or "{}").get("proxy_id") == proxy_id for o in rows)


# ----------------------------------------------------------------------------
# Vendor capacity: never take money we cannot turn into a proxy.
# ----------------------------------------------------------------------------

def _sx_committed_usd(db: Session, acc: sx.Account) -> float:
    """What the not yet used GB of live ports on the account will still cost us."""
    total = 0.0
    for p in db.scalars(select(StoreProxy).where(StoreProxy.vendor == "sx", StoreProxy.account == acc.fp,
                                                 StoreProxy.status.in_(("provisioning", "active")))).all():
        left = max(p.gb_total * sx.BYTES_PER_GB - p.bytes_used, 0) / sx.BYTES_PER_GB
        total += left * (acc.cost(p.ptype) or 0)
    return total


def pick_sx_account(db: Session, ptype: str, gb: int, prefer_fp: str | None = None) -> sx.Account | None:
    """The cheapest account for the type that can pay for the GB.

    Accounts whose cost for the type is above the selling price basis are never used, and
    accounts without enough free balance are skipped silently.
    """
    basis = pricing.usd_per_gb().get(ptype)
    if basis is None:
        return None
    accs = [a for a in sx.accounts() if a.cost(ptype) is not None and a.cost(ptype) <= basis + 1e-9]
    if prefer_fp:
        accs = [a for a in accs if a.fp == prefer_fp]
    best, best_key = None, None
    for acc in accs:
        need = acc.cost(ptype) * gb
        try:
            free = sx.balance(acc) - _sx_committed_usd(db, acc)
        except sx.SxError as exc:
            logger.warning("sx balance %s failed: %s", acc.fp, exc)
            continue
        if free < need:
            continue
        key = (acc.cost(ptype), -free)
        if best_key is None or key < best_key:
            best, best_key = acc, key
    return best


def pick_cy_account(cost_usd: float, prefer_fp: str | None = None) -> cy.Account | None:
    accs = cy.accounts()
    if prefer_fp:
        accs = [a for a in accs if a.fp == prefer_fp]
    for acc in accs:
        try:
            if cy.balance(acc) >= cost_usd:
                return acc
        except cy.CyError as exc:
            logger.warning("cy balance %s failed: %s", acc.fp, exc)
    return None


def ensure_capacity(db: Session, lic: License, product: str, params: dict, cost_usd: float) -> None:
    if product in ("dc", "dc_renew"):
        prefer = None
        if product == "dc_renew":
            prefer = _own_proxy(db, lic, params["proxy_id"]).account
        ok = pick_cy_account(cost_usd, prefer) is not None
    elif product == "topup":
        p = _own_proxy(db, lic, params["proxy_id"])
        ok = pick_sx_account(db, p.ptype, params["gb"], p.account) is not None
    else:
        ok = pick_sx_account(db, params["type"], params["gb"]) is not None
    if not ok:
        _notify_once("capacity:" + product + str(params.get("type", "")),
                     f"⚠️ Магазин прокси: ни на одном аккаунте не хватает денег, заказ отклонён "
                     f"({_product_title_raw(product, params)}, ${cost_usd:.2f}).", every_sec=12 * 3600)
        raise ApiError(409, "out_of_stock")


# ----------------------------------------------------------------------------
# Orders and payments
# ----------------------------------------------------------------------------

def return_url(token: str = "", web: bool = False) -> str:
    if web:
        return settings.BASE_URL + "/kupit-proksi/?order=" + token
    return settings.BASE_URL + "/store-paid.html"


def create_order(db: Session, lic: License, product: str, params: dict, method: str) -> StoreOrder:
    web = isinstance(lic, WebOwner)
    if not settings.STORE_ENABLED:
        raise ApiError(503, "store_disabled")
    if method not in METHODS:
        raise ApiError(400, "bad_method")
    clean, amount, cost = validate(db, lic, product, params)
    ensure_capacity(db, lic, product, clean, cost)

    token = secrets.token_urlsafe(24)
    description = {"dc": "ProxyBridge: прокси на 30 дней", "dc_renew": "ProxyBridge: продление прокси",
                   "traffic": f"ProxyBridge: прокси, {clean.get('gb')} ГБ",
                   "topup": f"ProxyBridge: трафик {clean.get('gb')} ГБ"}[product]
    try:
        if method == "yookassa":
            payment = yookassa.create_payment(amount, description, token, return_url(token, web))
            provider_id, pay_url = payment["id"], payment["confirmation_url"]
        else:
            invoice = cryptopay.create_invoice(amount, description, "store:" + token,
                                               paid_btn_url=return_url(token, web))
            provider_id, pay_url = invoice["invoice_id"], invoice["pay_url"]
    except (yookassa.YooKassaError, cryptopay.CryptoPayError) as exc:
        logger.error("store checkout provider error (%s): %s", method, exc)
        raise ApiError(400, "provider_error")

    order = StoreOrder(token=token, license_key=lic.key, product=product, params=json.dumps(clean),
                       amount_rub=amount, cost_usd=round(cost, 4), method=method,
                       provider_payment_id=provider_id)
    db.add(order)
    db.commit()
    order.pay_url = pay_url  # not stored
    return order


def _amount_ok(amount: dict, order: StoreOrder) -> bool:
    if (amount.get("currency") or "RUB") != "RUB":
        return False
    try:
        return Decimal(str(amount.get("value"))) == Decimal(order.amount_rub)
    except (InvalidOperation, TypeError):
        return False


def _mark_paid(db: Session, order: StoreOrder) -> None:
    res = db.execute(update(StoreOrder)
                     .where(StoreOrder.id == order.id, StoreOrder.status == "pending")
                     .values(status="paid", paid_at=to_db(utcnow()), fulfill="queued"))
    db.commit()
    db.refresh(order)
    if res.rowcount == 1:
        logger.info("store order %s paid (%s, %s RUB)", order.id, order.product, order.amount_rub)
        notify.send_admin(f"🟩 Магазин прокси: оплата\nТовар: {_product_title(order)}\n"
                          f"Сумма: {order.amount_rub} ₽ ({order.method})\nЗаказ: S{order.id}")


def check_payment(db: Session, order: StoreOrder) -> StoreOrder:
    """Ask the payment provider about a pending order. Never raises on provider errors."""
    if order.status != "pending" or not order.provider_payment_id:
        return order
    try:
        if order.method == "yookassa":
            payment = yookassa.get_payment(order.provider_payment_id)
            if payment.get("payment_method_type"):
                order.pay_type = str(payment["payment_method_type"])[:32]
            if payment.get("income_amount") not in (None, ""):
                order.income_rub = float(Decimal(str(payment["income_amount"])))
            order.checked_at = to_db(utcnow())
            status = payment.get("status")
            if status == "succeeded" and payment.get("id") == order.provider_payment_id:
                if _amount_ok(payment.get("amount") or {}, order):
                    db.commit()
                    _mark_paid(db, order)
                    return order
                logger.warning("store order %s amount mismatch: %s", order.id, payment.get("amount"))
            elif status == "canceled":
                order.status = "canceled"
                order.fail_reason = str(payment.get("cancellation_reason") or "")[:64] or None
            db.commit()
        else:
            invoice = cryptopay.get_invoice(order.provider_payment_id)
            order.checked_at = to_db(utcnow())
            if invoice is None:
                db.commit()
                return order
            if invoice.get("paid_asset"):
                order.pay_type = str(invoice["paid_asset"])[:32]
            if invoice.get("status") == "paid":
                db.commit()
                _mark_paid(db, order)
                return order
            if invoice.get("status") == "expired":
                order.status = "canceled"
                order.fail_reason = "expired"
            db.commit()
    except (yookassa.YooKassaError, cryptopay.CryptoPayError, ValueError) as exc:
        db.rollback()
        logger.warning("store payment check %s failed: %s", order.id, exc)
    return order


# ----------------------------------------------------------------------------
# Fulfillment
# ----------------------------------------------------------------------------

def _claim(db: Session, order: StoreOrder, src: str, dst: str) -> bool:
    res = db.execute(update(StoreOrder).where(StoreOrder.id == order.id, StoreOrder.fulfill == src)
                     .values(fulfill=dst))
    db.commit()
    db.refresh(order)
    return res.rowcount == 1


def _done(db: Session, order: StoreOrder, proxy: StoreProxy | None = None) -> None:
    order.fulfill = "done"
    order.fulfilled_at = to_db(utcnow())
    db.commit()
    where = f"{proxy.country_code} {proxy.ptype}" if proxy else ""
    notify.send_admin(f"✅ Магазин прокси: выдано\nТовар: {_product_title(order)} {where}\nЗаказ: S{order.id}")


def _fail(db: Session, order: StoreOrder, reason: str, refund: bool) -> None:
    """Give up on an order. With refund=True the outcome at the vendor is known to be "nothing bought"."""
    order.fulfill = "failed"
    order.fulfill_error = reason[:256]
    db.commit()
    refunded_note = "нужен ручной возврат"
    if refund and order.method == "yookassa" and order.provider_payment_id and not order.refunded:
        try:
            yookassa.create_refund(order.provider_payment_id, order.amount_rub, "store-refund-" + order.token)
            order.refunded = True
            db.commit()
            refunded_note = "деньги возвращены автоматически"
        except yookassa.YooKassaError as exc:
            logger.error("store refund for %s failed: %s", order.id, exc)
            refunded_note = "автовозврат не прошёл, верните вручную"
    elif not refund:
        refunded_note = "проверьте у поставщика вручную, возврат не делался"
    logger.error("store order %s failed: %s (%s)", order.id, reason, refunded_note)
    notify.send_admin(f"🟥 Магазин прокси: ошибка выдачи\nТовар: {_product_title(order)}\n"
                      f"Сумма: {order.amount_rub} ₽ ({order.method})\nПричина: {reason[:200]}\n"
                      f"Итог: {refunded_note}\nЗаказ: S{order.id}")


def _manual(db: Session, order: StoreOrder, reason: str) -> None:
    order.fulfill = "manual"
    order.fulfill_error = reason[:256]
    db.commit()
    notify.send_admin(f"🟨 Магазин прокси: нужна проверка\nТовар: {_product_title(order)}\n"
                      f"Причина: {reason[:200]}\nЗаказ: S{order.id}")


def fulfill(db: Session, order: StoreOrder) -> None:
    if order.status != "paid" or not _claim(db, order, "queued", "working"):
        return
    params = json.loads(order.params or "{}")
    try:
        if order.product == "dc":
            _fulfill_dc(db, order, params)
        elif order.product == "dc_renew":
            _fulfill_dc_renew(db, order, params)
        elif order.product == "traffic":
            _fulfill_traffic(db, order, params)
        elif order.product == "topup":
            _fulfill_topup(db, order, params)
    except Exception as exc:  # noqa: BLE001
        logger.exception("store fulfill %s crashed", order.id)
        db.rollback()
        db.refresh(order)
        if order.fulfill == "working":
            _manual(db, order, f"internal error: {type(exc).__name__}")


def _fulfill_dc(db: Session, order: StoreOrder, params: dict) -> None:
    code = params["country"]
    try:
        item = cy.dc_product(code, settings.STORE_DC_DAYS)
    except cy.CyError as exc:
        return _fail(db, order, f"catalog: {exc}", refund=True)
    if item is None:
        return _fail(db, order, f"sold out: {code}", refund=True)
    acc = pick_cy_account(cy.product_price(item))
    if acc is None:
        return _fail(db, order, "vendor balance too low", refund=True)
    p = StoreProxy(license_key=order.license_key, kind="dc", vendor="cy", account=acc.fp,
                   country_code=code, ptype="datacenter", status="provisioning")
    db.add(p)
    db.commit()
    order.proxy_id = p.id
    db.commit()
    try:
        vendor_order = cy.buy(acc, item["id"])
    except cy.CyError as exc:
        if exc.sent:
            p.status = "failed"
            db.commit()
            return _manual(db, order, f"purchase outcome unknown: {exc}")
        p.status = "failed"
        db.commit()
        return _fail(db, order, f"purchase rejected: {exc}", refund=True)
    p.vendor_id = vendor_order
    order.vendor_order_id = vendor_order
    db.commit()
    # Stays "working": the worker polls the vendor until the proxy is ready.


def _fulfill_dc_renew(db: Session, order: StoreOrder, params: dict) -> None:
    p = db.get(StoreProxy, params["proxy_id"])
    acc = cy.account_by_fp(p.account) if p else None
    if p is None or acc is None or not p.vendor_id:
        return _fail(db, order, "proxy or vendor account missing", refund=True)
    try:
        cy.set_auto_renew(acc, p.vendor_id, True)
    except cy.CyError as exc:
        return _fail(db, order, f"auto renew: {exc}", refund=True)
    p.renew_pending += 1
    order.proxy_id = p.id
    db.commit()
    _done(db, order, p)


def _fulfill_traffic(db: Session, order: StoreOrder, params: dict) -> None:
    acc = pick_sx_account(db, params["type"], params["gb"])
    if acc is None:
        return _fail(db, order, "vendor balance too low", refund=True)
    p = StoreProxy(license_key=order.license_key, kind="traffic", vendor="sx", account=acc.fp,
                   country_code=params["country"], country_id=params["country_id"],
                   state=params.get("state"), state_id=params.get("state_id"),
                   city=params.get("city"), city_id=params.get("city_id"),
                   ptype=params["type"], rotation=params["rotation"], ttl=params.get("ttl"),
                   gb_total=params["gb"], status="provisioning")
    db.add(p)
    db.commit()
    order.proxy_id = p.id
    db.commit()
    try:
        port = sx.create_port(acc, p.country_code, p.state, p.city, p.ptype, p.rotation, p.ttl, p.gb_total)
    except sx.SxError as exc:
        p.status = "failed"
        db.commit()
        return _fail(db, order, f"create: {exc}", refund=True)
    p.vendor_id = str(port.get("id"))
    order.vendor_order_id = p.vendor_id
    order.cost_usd = round((acc.cost(p.ptype) or 0) * p.gb_total, 4)
    p.host = str(port.get("server") or "")
    p.port = _int(port.get("port"), 0)
    p.login = str(port.get("login") or "")
    p.password = str(port.get("password") or "")
    p.state_id = port.get("state_id") or p.state_id
    p.city_id = port.get("city_id") or p.city_id
    db.commit()
    # Stays "working" until the probe through the new port succeeds.


def _fulfill_topup(db: Session, order: StoreOrder, params: dict) -> None:
    p = db.get(StoreProxy, params["proxy_id"])
    acc = sx.account_by_fp(p.account) if p else None
    if p is None or acc is None or not p.vendor_id:
        return _fail(db, order, "proxy or vendor account missing", refund=True)
    new_total = p.gb_total + int(params["gb"])
    if pick_sx_account(db, p.ptype, int(params["gb"]), p.account) is None:
        return _fail(db, order, "vendor balance too low", refund=True)
    order.cost_usd = round((acc.cost(p.ptype) or 0) * int(params["gb"]), 4)
    try:
        sx.update_port(acc, p.vendor_id, p.country_id, p.state_id, p.city_id, p.ptype, p.rotation or "static",
                       p.ttl, new_total)
        if p.status == "exhausted":
            sx.unarchive(acc, p.vendor_id)
    except sx.SxError as exc:
        return _fail(db, order, f"top up: {exc}", refund=True)
    p.gb_total = new_total
    if p.status == "exhausted":
        p.status = "active"
    order.proxy_id = p.id
    db.commit()
    _done(db, order, p)


def probe(p: StoreProxy) -> bool:
    """One HTTP request through the proxy."""
    if not p.host or not p.port:
        return False
    url = f"http://{quote(p.login, safe='')}:{quote(p.password, safe='')}@{p.host}:{p.port}"
    try:
        with httpx.Client(proxy=url, timeout=15) as client:
            r = client.get(PROBE_URL)
        return r.status_code == 200 and r.json().get("status") == "success"
    except Exception:  # noqa: BLE001
        return False


def advance_working(db: Session, order: StoreOrder) -> None:
    """Finish orders that wait for the vendor (dedicated purchase or a new port warming up)."""
    p = db.get(StoreProxy, order.proxy_id) if order.proxy_id else None
    if p is None:
        return
    age = utcnow() - from_db(order.paid_at or order.created_at)
    if order.product == "dc":
        if not order.vendor_order_id:
            return
        acc = cy.account_by_fp(p.account)
        try:
            item = cy.find_order(acc, order.vendor_order_id) if acc else None
        except cy.CyError as exc:
            logger.warning("store: cy order %s lookup failed: %s", order.id, exc)
            return
        st = (item or {}).get("order_status")
        if st == "success" and item.get("connection_host"):
            p.host = str(item.get("connection_host"))
            p.port = _int(item.get("connection_port"), 0)
            p.login = str(item.get("connection_login") or "")
            p.password = str(item.get("connection_password") or "")
            p.expires_at = to_db(_parse_dt(item.get("access_expires_at")))
            p.status = "active"
            p.ready_at = to_db(utcnow())
            db.commit()
            return _done(db, order, p)
        if st == "canceled":
            p.status = "failed"
            db.commit()
            return _fail(db, order, "vendor canceled the purchase", refund=True)
        if st == "needs_investigation" or age > CY_GIVE_UP:
            return _manual(db, order, f"vendor status {st or 'not found'} after {int(age.total_seconds())} s")
        return
    if order.product == "traffic":
        if probe(p):
            p.status = "active"
            p.ready_at = to_db(utcnow())
            db.commit()
            return _done(db, order, p)
        if age > PROBE_GIVE_UP:
            acc = sx.account_by_fp(p.account)
            try:
                if acc and p.vendor_id:
                    sx.delete(acc, p.vendor_id)
            except sx.SxError as exc:
                logger.warning("store: delete of dead port %s failed: %s", p.vendor_id, exc)
            p.status = "failed"
            db.commit()
            return _fail(db, order, "new port does not answer", refund=True)


def _parse_dt(value):
    if not value:
        return None
    from datetime import datetime
    try:
        return datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    except ValueError:
        return None


# ----------------------------------------------------------------------------
# Usage and term sync
# ----------------------------------------------------------------------------

def sync_traffic(db: Session, p: StoreProxy) -> None:
    acc = sx.account_by_fp(p.account)
    if acc is None or not p.vendor_id:
        return
    raw = sx.port_traffic(acc, p.vendor_id)
    # Accumulate so a counter reset at the vendor never gives free traffic.
    delta = raw - p.bytes_raw if raw >= p.bytes_raw else raw
    p.bytes_raw = raw
    p.bytes_used += max(delta, 0)
    p.synced_at = to_db(utcnow())
    if p.status == "active" and p.bytes_used >= p.gb_total * sx.BYTES_PER_GB:
        sx.archive(acc, p.vendor_id)
        p.status = "exhausted"
        logger.info("store proxy %s exhausted, port archived", p.id)
    db.commit()


def sync_dc(db: Session, p: StoreProxy) -> None:
    acc = cy.account_by_fp(p.account)
    if acc is None or not p.vendor_id:
        return
    expires = from_db(p.expires_at)
    now = utcnow()
    near = expires is not None and expires - now < timedelta(days=2)
    if p.renew_pending > 0 or near:
        item = cy.find_order(acc, p.vendor_id)
        if item:
            new_exp = _parse_dt(item.get("access_expires_at"))
            if new_exp and expires and new_exp > expires + timedelta(days=1) and p.renew_pending > 0:
                p.renew_pending -= 1
                if p.renew_pending == 0:
                    cy.set_auto_renew(acc, p.vendor_id, False)
            if new_exp:
                p.expires_at = to_db(new_exp)
            if str(item.get("system_status")) == "deleted":
                p.status = "expired"
    expires = from_db(p.expires_at)
    if expires and expires <= now and p.renew_pending == 0:
        p.status = "expired"
    p.synced_at = to_db(now)
    db.commit()


_last: dict[str, float] = {}


def _notify_once(key: str, text: str, every_sec: int = 6 * 3600) -> None:
    now = time.time()
    if now - _last.get("n:" + key, 0) < every_sec:
        return
    _last["n:" + key] = now
    notify.send_admin(text)


def tick(db: Session) -> None:
    """One worker pass. Safe to run often."""
    now = utcnow()
    cutoff = to_db(now - PAYMENT_MAX_AGE)
    recheck = to_db(now - timedelta(seconds=15))
    for order in db.scalars(select(StoreOrder).where(StoreOrder.status == "pending",
                                                     StoreOrder.created_at >= cutoff)).all():
        if order.checked_at is None or order.checked_at <= recheck:
            check_payment(db, order)
    for order in db.scalars(select(StoreOrder).where(StoreOrder.status == "paid",
                                                     StoreOrder.fulfill == "queued")).all():
        fulfill(db, order)
    for order in db.scalars(select(StoreOrder).where(StoreOrder.status == "paid",
                                                     StoreOrder.fulfill == "working")).all():
        try:
            advance_working(db, order)
        except Exception as exc:  # noqa: BLE001
            db.rollback()
            logger.warning("store: advance %s failed: %s", order.id, exc)

    if time.time() - _last.get("sync", 0) >= SYNC_EVERY_SEC:
        _last["sync"] = time.time()
        for p in db.scalars(select(StoreProxy).where(StoreProxy.status.in_(("active", "exhausted")))).all():
            try:
                if p.kind == "traffic":
                    sync_traffic(db, p)
                elif p.kind == "dc" and p.status == "active":
                    sync_dc(db, p)
            except (sx.SxError, cy.CyError) as exc:
                db.rollback()
                logger.warning("store: sync proxy %s failed: %s", p.id, exc)
            time.sleep(0.3)


def worker(stop) -> None:
    from .. import db as dbmod
    while not stop.wait(5):
        if not settings.STORE_ENABLED:
            continue
        try:
            with dbmod.SessionLocal() as session:
                tick(session)
        except Exception as exc:  # noqa: BLE001
            logger.warning("store worker cycle failed: %s", exc)


# ----------------------------------------------------------------------------
# Client views
# ----------------------------------------------------------------------------

def _product_title(order: StoreOrder) -> str:
    return _product_title_raw(order.product, json.loads(order.params or "{}"))


def _product_title_raw(product: str, params: dict) -> str:
    if product == "dc":
        return f"выделенный прокси {params.get('country', '')} на {settings.STORE_DC_DAYS} дней"
    if product == "dc_renew":
        return "продление выделенного прокси"
    if product == "traffic":
        geo = " / ".join(x for x in (params.get("country"), params.get("state"), params.get("city")) if x)
        return f"прокси {params.get('type')} {geo}, {params.get('gb')} ГБ, ротация {params.get('rotation')}"
    return f"докупка трафика {params.get('gb')} ГБ"


def proxy_view(p: StoreProxy) -> dict:
    ready = p.status in ("active", "exhausted", "expired")
    expires = from_db(p.expires_at)
    view = {
        "id": p.id,
        "kind": p.kind,
        "status": p.status,
        "type": p.ptype,
        "country": country_info(p.country_code),
        "state": p.state,
        "city": p.city,
        "rotation": p.rotation,
        "ttl": p.ttl,
        "gb_total": p.gb_total if p.kind == "traffic" else None,
        "gb_used": round(p.bytes_used / sx.BYTES_PER_GB, 3) if p.kind == "traffic" else None,
        "expires_at": iso_z(p.expires_at),
        "renew_pending": p.renew_pending > 0,
        "can_renew": p.kind == "dc" and p.status == "active" and p.renew_pending == 0
        and bool(expires and expires > utcnow() + timedelta(minutes=30)),
        "can_topup": p.kind == "traffic" and p.status in ("active", "exhausted"),
        "can_refresh_ip": p.kind == "traffic" and p.status == "active",
        "created_at": iso_z(p.created_at),
        "host": p.host if ready else "",
        "port": p.port if ready else 0,
        "login": p.login if ready else "",
        "password": p.password if ready else "",
    }
    return view


def order_view(db: Session, order: StoreOrder) -> dict:
    state = order.status
    if order.status == "paid":
        state = {"queued": "processing", "working": "processing", "done": "done",
                 "failed": "failed", "manual": "processing"}.get(order.fulfill, "processing")
    p = db.get(StoreProxy, order.proxy_id) if order.proxy_id else None
    return {
        "token": order.token,
        "product": order.product,
        "amount_rub": order.amount_rub,
        "state": state,
        "refunded": order.refunded,
        "proxy": proxy_view(p) if p and state == "done" else None,
    }


def list_proxies(db: Session, lic: License) -> list[dict]:
    rows = db.scalars(select(StoreProxy).where(StoreProxy.license_key == lic.key,
                                               StoreProxy.status.in_(("provisioning", "active",
                                                                      "exhausted", "expired")))
                      .order_by(StoreProxy.id.desc())).all()
    return [proxy_view(p) for p in rows]


def refresh_ip(db: Session, lic: License, proxy_id) -> None:
    p = _own_proxy(db, lic, proxy_id)
    if p.kind != "traffic" or p.status != "active" or not p.vendor_id:
        raise ApiError(409, "cannot_refresh")
    key = f"refresh:{p.id}"
    if time.time() - _last.get(key, 0) < 30:
        raise ApiError(429, "too_often")
    _last[key] = time.time()
    acc = sx.account_by_fp(p.account)
    try:
        sx.refresh_ip(acc, p.vendor_id)
    except (sx.SxError, AttributeError):
        raise ApiError(502, "vendor_error")
