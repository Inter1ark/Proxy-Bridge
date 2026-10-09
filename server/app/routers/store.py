"""In-app proxy store endpoints. The app authenticates with its license key and device id."""

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.orm import Session

from ..db import get_db
from ..errors import ApiError
from ..models import StoreOrder
from ..store import service

router = APIRouter(prefix="/api/store", tags=["store"])


class Auth(BaseModel):
    key: str = ""
    hwid: str = ""
    # Website buyers: secret cabinet token instead of a license key.
    cabinet: str = ""


class GeoRequest(Auth):
    country_id: int = 0
    state_id: int = 0


class AvailabilityRequest(Auth):
    country: str = ""
    type: str = ""


class OrderRequest(Auth):
    product: str = ""
    params: dict = Field(default_factory=dict)
    method: str = ""


class TokenRequest(Auth):
    token: str = ""


class ProxyRequest(Auth):
    proxy_id: int = 0


@router.post("/catalog")
def catalog(req: Auth, db: Session = Depends(get_db)):
    service.auth(db, req.key, req.hwid, req.cabinet)
    return {"ok": True, **service.catalog()}


@router.post("/states")
def states(req: GeoRequest, db: Session = Depends(get_db)):
    service.auth(db, req.key, req.hwid, req.cabinet)
    return {"ok": True, "states": service.states(req.country_id)}


@router.post("/cities")
def cities(req: GeoRequest, db: Session = Depends(get_db)):
    service.auth(db, req.key, req.hwid, req.cabinet)
    if not req.state_id:
        raise ApiError(400, "bad_request")
    return {"ok": True, "cities": service.cities(req.country_id, req.state_id)}


@router.post("/availability")
def availability(req: AvailabilityRequest, db: Session = Depends(get_db)):
    service.auth(db, req.key, req.hwid, req.cabinet)
    return {"ok": True, "available": service.availability(req.country, req.type)}


@router.post("/quote")
def quote(req: OrderRequest, db: Session = Depends(get_db)):
    lic = service.auth(db, req.key, req.hwid, req.cabinet)
    _, amount, _ = service.validate(db, lic, req.product, req.params)
    return {"ok": True, "amount_rub": amount}


@router.post("/order")
def create_order(req: OrderRequest, db: Session = Depends(get_db)):
    lic = service.auth(db, req.key, req.hwid, req.cabinet)
    order = service.create_order(db, lic, req.product, req.params, req.method)
    return {"ok": True, "token": order.token, "pay_url": order.pay_url, "amount_rub": order.amount_rub}


@router.post("/order/status")
def order_status(req: TokenRequest, db: Session = Depends(get_db)):
    lic = service.auth(db, req.key, req.hwid, req.cabinet)
    order = db.scalar(select(StoreOrder).where(StoreOrder.token == req.token))
    if order is None or order.license_key != lic.key:
        raise ApiError(404, "not_found")
    service.check_payment(db, order)
    return {"ok": True, **service.order_view(db, order)}


@router.post("/proxies")
def proxies(req: Auth, db: Session = Depends(get_db)):
    lic = service.auth(db, req.key, req.hwid, req.cabinet)
    return {"ok": True, "proxies": service.list_proxies(db, lic)}


@router.post("/proxy/refresh-ip")
def refresh_ip(req: ProxyRequest, db: Session = Depends(get_db)):
    lic = service.auth(db, req.key, req.hwid, req.cabinet)
    service.refresh_ip(db, lic, req.proxy_id)
    return {"ok": True}
