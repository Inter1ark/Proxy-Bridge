"""Admin endpoints.

Auth: the X-Admin-Token header (scripts, curl) or the pb_admin session cookie
set by POST /api/admin/login (web panel at /admin). See app/admin_auth.py.
"""

from datetime import datetime, timedelta

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel
from sqlalchemy import or_, select, update
from sqlalchemy.orm import Session

from .. import admin_data, mailer
from ..admin_auth import (COOKIE_NAME, SESSION_TTL_SEC, check_credentials, clear_failures, client_ip,
                          cookie_secure, is_rate_limited, login_enabled, make_session, register_failure,
                          require_admin)
from ..config import settings
from ..db import get_db
from ..errors import ApiError
from ..keys import normalize_key
from ..models import License, Order, StoreOrder, to_db, utcnow
from ..orders import backfill_provider_details, check_order_with_provider, issue_license, order_to_dict
from ..plans import get_plan
from ..routers.checkout import is_valid_email

router = APIRouter(prefix="/api/admin", tags=["admin"])

STATUSES = ("paid", "canceled", "pending")
METHODS = ("yookassa", "cryptobot")
LICENSE_STATES = ("active", "revoked", "expired")


# ---------- session ----------

class LoginRequest(BaseModel):
    login: str = ""
    password: str = ""


@router.post("/login")
def login(req: LoginRequest, request: Request):
    if not login_enabled():
        raise ApiError(403, "login_disabled")
    ip = client_ip(request)
    if is_rate_limited(ip):
        raise ApiError(429, "too_many_attempts")
    if not check_credentials(req.login, req.password):
        register_failure(ip)
        raise ApiError(401, "bad_credentials")
    clear_failures(ip)
    resp = JSONResponse({"ok": True, "login": settings.ADMIN_LOGIN})
    resp.set_cookie(COOKIE_NAME, make_session(settings.ADMIN_LOGIN), max_age=SESSION_TTL_SEC, path="/",
                    httponly=True, samesite="strict", secure=cookie_secure())
    return resp


@router.post("/logout")
def logout():
    resp = JSONResponse({"ok": True})
    resp.delete_cookie(COOKIE_NAME, path="/", httponly=True, samesite="strict", secure=cookie_secure())
    return resp


@router.get("/me")
def me(auth: str = Depends(require_admin)):
    return {"ok": True, "auth": auth, "login": settings.ADMIN_LOGIN or None}


# ---------- keys (existing) ----------

class IssueKeyRequest(BaseModel):
    plan_id: str = ""
    email: str | None = None


@router.post("/keys", dependencies=[Depends(require_admin)])
def issue_key(req: IssueKeyRequest, db: Session = Depends(get_db)):
    if get_plan(req.plan_id) is None:
        raise ApiError(400, "bad_plan")
    email = (req.email or "").strip().lower() or None
    if email and not is_valid_email(email):
        raise ApiError(400, "bad_email")
    lic = issue_license(db, req.plan_id, email, source="admin")
    db.commit()
    if email:
        mailer.send_key_email(email, lic.key, lic.plan_id)
    return {"key": lic.key}


def _load_license(db: Session, key: str) -> License:
    norm = normalize_key(key)
    if norm is None:
        raise ApiError(400, "bad_request")
    lic = db.scalar(select(License).where(License.key == norm))
    if lic is None:
        raise ApiError(404, "not_found")
    return lic


@router.post("/keys/{key}/revoke", dependencies=[Depends(require_admin)])
def revoke_key(key: str, db: Session = Depends(get_db)):
    lic = _load_license(db, key)
    lic.revoked = True
    db.commit()
    return {"ok": True}


# ---------- stats ----------

@router.get("/stats", dependencies=[Depends(require_admin)])
def stats(exclude_test: bool = False, db: Session = Depends(get_db)):
    return admin_data.compute_stats(db, exclude_test=exclude_test)


# ---------- orders ----------

def _order_conds(status, method, plan, q, date_from, date_to, exclude_test) -> list:
    if status and status not in STATUSES:
        raise ApiError(400, "bad_status")
    if method and method not in METHODS:
        raise ApiError(400, "bad_method")
    if plan and get_plan(plan) is None:
        raise ApiError(400, "bad_plan")
    for value in (date_from, date_to):
        if value and admin_data.parse_day(value) is None:
            raise ApiError(400, "bad_date")
    return admin_data.order_filters(status=status, method=method, plan=plan, q=q,
                                    date_from=date_from, date_to=date_to, exclude_test=exclude_test)


@router.get("/orders", dependencies=[Depends(require_admin)])
def list_orders(status: str | None = None, method: str | None = None, plan: str | None = None,
                q: str | None = None, date_from: str | None = None, date_to: str | None = None,
                exclude_test: bool = False,
                page: int = Query(default=1, ge=1), per_page: int = Query(default=50, ge=1, le=200),
                limit: int | None = Query(default=None, ge=1, le=500),
                db: Session = Depends(get_db)):
    if limit is not None:
        # Legacy format (?limit=N): plain list including the order token.
        orders = db.scalars(select(Order).order_by(Order.id.desc()).limit(limit)).all()
        return [order_to_dict(o, admin=True) for o in orders]
    conds = _order_conds(status, method, plan, q, date_from, date_to, exclude_test)
    return admin_data.list_orders(db, conds, page=page, per_page=per_page)


def _export_name(ext: str) -> str:
    stamp = datetime.now(admin_data.MSK).strftime("%Y%m%d-%H%M")
    return f"proxybridge-orders-{stamp}.{ext}"


@router.get("/orders/export.csv", dependencies=[Depends(require_admin)])
def export_orders_csv(status: str | None = None, method: str | None = None, plan: str | None = None,
                      q: str | None = None, date_from: str | None = None, date_to: str | None = None,
                      exclude_test: bool = False, db: Session = Depends(get_db)):
    conds = _order_conds(status, method, plan, q, date_from, date_to, exclude_test)
    body = admin_data.export_csv(admin_data.export_rows(db, conds))
    return Response(body, media_type="text/csv; charset=utf-8", headers={
        "Content-Disposition": f'attachment; filename="{_export_name("csv")}"',
        "Cache-Control": "no-store",
    })


@router.get("/orders/export.xlsx", dependencies=[Depends(require_admin)])
def export_orders_xlsx(status: str | None = None, method: str | None = None, plan: str | None = None,
                       q: str | None = None, date_from: str | None = None, date_to: str | None = None,
                       exclude_test: bool = False, db: Session = Depends(get_db)):
    conds = _order_conds(status, method, plan, q, date_from, date_to, exclude_test)
    body = admin_data.export_xlsx(admin_data.export_rows(db, conds))
    return Response(body,
                    media_type="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    headers={
                        "Content-Disposition": f'attachment; filename="{_export_name("xlsx")}"',
                        "Cache-Control": "no-store",
                    })


@router.post("/orders/refresh", dependencies=[Depends(require_admin)])
def refresh_orders(limit: int = Query(default=500, ge=1, le=2000), db: Session = Depends(get_db)):
    counts = backfill_provider_details(limit=limit, db=db)
    return {"ok": True, **counts}


def _get_order(db: Session, order_id: int) -> Order:
    order = db.get(Order, order_id)
    if order is None:
        raise ApiError(404, "not_found")
    return order


@router.get("/orders/{order_id:int}", dependencies=[Depends(require_admin)])
def order_details(order_id: int, db: Session = Depends(get_db)):
    return admin_data.order_details(db, _get_order(db, order_id))


@router.post("/orders/{order_id:int}/check", dependencies=[Depends(require_admin)])
def check_order(order_id: int, db: Session = Depends(get_db)):
    order = _get_order(db, order_id)
    if not order.provider_payment_id:
        raise ApiError(400, "no_payment_id")
    before = order.checked_at
    check_order_with_provider(db, order, any_status=True)
    db.refresh(order)
    if order.checked_at is None or order.checked_at == before:
        raise ApiError(502, "provider_error")
    return admin_data.order_details(db, order)


# ---------- licenses ----------

@router.get("/licenses", dependencies=[Depends(require_admin)])
def list_licenses(q: str | None = None, state: str | None = None, exclude_test: bool = False,
                  page: int = Query(default=1, ge=1), per_page: int = Query(default=50, ge=1, le=200),
                  db: Session = Depends(get_db)):
    if state and state not in LICENSE_STATES:
        raise ApiError(400, "bad_state")
    conds = admin_data.license_filters(q=q, state=state, exclude_test=exclude_test)
    return admin_data.list_licenses(db, conds, page=page, per_page=per_page)


def _license_response(db: Session, lic: License) -> dict:
    db.refresh(lic)
    return {"ok": True, "license": admin_data.license_dict(lic)}


@router.post("/licenses/{key}/revoke", dependencies=[Depends(require_admin)])
def license_revoke(key: str, db: Session = Depends(get_db)):
    lic = _load_license(db, key)
    lic.revoked = True
    db.commit()
    return _license_response(db, lic)


@router.post("/licenses/{key}/restore", dependencies=[Depends(require_admin)])
def license_restore(key: str, db: Session = Depends(get_db)):
    lic = _load_license(db, key)
    lic.revoked = False
    db.commit()
    return _license_response(db, lic)


@router.post("/licenses/{key}/reset-devices", dependencies=[Depends(require_admin)])
def license_reset_devices(key: str, db: Session = Depends(get_db)):
    lic = _load_license(db, key)
    removed = len(lic.devices)
    for device in list(lic.devices):
        db.delete(device)
    db.commit()
    data = _license_response(db, lic)
    data["removed"] = removed
    return data


class ExtendRequest(BaseModel):
    days: int = 30


@router.post("/licenses/{key}/extend", dependencies=[Depends(require_admin)])
def license_extend(key: str, req: ExtendRequest, db: Session = Depends(get_db)):
    if not 1 <= req.days <= 3650:
        raise ApiError(400, "bad_days")
    lic = _load_license(db, key)
    if lic.expires_at is None:
        raise ApiError(400, "lifetime")
    now = to_db(utcnow())
    base = lic.expires_at if lic.expires_at > now else now
    lic.expires_at = base + timedelta(days=req.days)
    db.commit()
    return _license_response(db, lic)


# ---------- proxy store ----------

def _check_store_filters(status: str | None, fulfill: str | None) -> None:
    if status and status not in admin_data.STORE_STATUSES:
        raise ApiError(400, "bad_status")
    if fulfill and fulfill != "attention":
        if any(f not in admin_data.STORE_FULFILL for f in fulfill.split(",") if f):
            raise ApiError(400, "bad_fulfill")


@router.get("/store/orders", dependencies=[Depends(require_admin)])
def store_orders(status: str | None = None, fulfill: str | None = None, q: str | None = None,
                 limit: int = Query(default=200, ge=1, le=1000), db: Session = Depends(get_db)):
    _check_store_filters(status, fulfill)
    conds = admin_data.store_order_filters(status=status, fulfill=fulfill, q=q)
    return admin_data.list_store_orders(db, conds, limit=limit)


@router.get("/store/proxies", dependencies=[Depends(require_admin)])
def store_proxies(status: str | None = None, q: str | None = None,
                  limit: int = Query(default=200, ge=1, le=1000), db: Session = Depends(get_db)):
    if status and status not in admin_data.STORE_PROXY_STATUSES:
        raise ApiError(400, "bad_status")
    conds = admin_data.store_proxy_filters(status=status, q=q)
    return admin_data.list_store_proxies(db, conds, limit=limit)


@router.get("/store/summary", dependencies=[Depends(require_admin)])
def store_summary(db: Session = Depends(get_db)):
    return admin_data.store_summary(db)


def _get_store_order(db: Session, order_id: int) -> StoreOrder:
    order = db.get(StoreOrder, order_id)
    if order is None:
        raise ApiError(404, "not_found")
    return order


@router.post("/store/orders/{order_id:int}/retry", dependencies=[Depends(require_admin)])
def store_order_retry(order_id: int, db: Session = Depends(get_db)):
    """Put a failed order back in the worker queue. Only when nothing was bought at the vendor."""
    _get_store_order(db, order_id)
    # Conditional update: a concurrent worker pass or a second click cannot queue it twice.
    res = db.execute(update(StoreOrder).where(
        StoreOrder.id == order_id,
        StoreOrder.status == "paid",
        StoreOrder.fulfill.in_(admin_data.RETRY_FULFILL),
        StoreOrder.refunded.is_(False),
        or_(StoreOrder.vendor_order_id.is_(None), StoreOrder.vendor_order_id == ""),
    ).values(fulfill="queued", fulfill_error=None))
    db.commit()
    if res.rowcount != 1:
        raise ApiError(409, "cannot_retry")
    order = _get_store_order(db, order_id)
    db.refresh(order)
    return {"ok": True, "order": admin_data.store_order_item(order)}


@router.post("/store/orders/{order_id:int}/mark-refunded", dependencies=[Depends(require_admin)])
def store_order_mark_refunded(order_id: int, db: Session = Depends(get_db)):
    """The owner returned the money by hand (or at the provider dashboard)."""
    order = _get_store_order(db, order_id)
    if order.status != "paid":
        raise ApiError(409, "not_paid")
    order.refunded = True
    db.commit()
    db.refresh(order)
    return {"ok": True, "order": admin_data.store_order_item(order)}
