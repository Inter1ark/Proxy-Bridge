"""Payment endpoints used by the site: plans, checkout, order status."""

import logging
import re
import secrets

from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.orm import Session

from .. import stars
from ..config import settings
from ..db import get_db
from ..errors import ApiError
from ..models import Order, iso_z
from ..orders import check_order_with_provider, order_to_dict, renewable_license
from ..plans import get_public_plan, plans_list
from ..providers import cryptopay, yookassa

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/api", tags=["checkout"])

EMAIL_RE = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")
METHODS = ("yookassa", "cryptobot", "stars")


class CheckoutRequest(BaseModel):
    plan_id: str = ""
    email: str = ""
    method: str = ""
    renew_key: str = ""  # extend this key instead of issuing a new one


def _mask_email(email: str | None) -> str:
    if not email or "@" not in email:
        return ""
    name, domain = email.split("@", 1)
    return (name[:2] + "***" if len(name) > 2 else name[:1] + "***") + "@" + domain


def is_valid_email(value: str) -> bool:
    return bool(value) and len(value) <= 254 and EMAIL_RE.match(value) is not None


@router.get("/plans")
def plans():
    return plans_list()


@router.get("/renew/{key}")
def renew_info(key: str, db: Session = Depends(get_db)):
    """What the buy page shows before extending a key: its plan and end date, not the full email."""
    lic = renewable_license(db, key)
    return {"ok": True, "key": lic.key, "plan_id": lic.plan_id, "expires_at": iso_z(lic.expires_at),
            "expired": lic.is_expired(), "email_hint": _mask_email(lic.email), "has_email": bool(lic.email)}


@router.post("/checkout")
def checkout(req: CheckoutRequest, db: Session = Depends(get_db)):
    plan = get_public_plan(req.plan_id)
    if plan is None:
        raise ApiError(400, "bad_plan")
    lic = renewable_license(db, req.renew_key) if (req.renew_key or "").strip() else None
    email = (req.email or "").strip().lower()
    if not email and lic is not None and lic.email:
        email = lic.email  # a renewal may skip the email: the receipt goes to the key's address
    if not is_valid_email(email):
        raise ApiError(400, "bad_email")
    if req.method not in METHODS:
        raise ApiError(400, "bad_method")
    renew_key = lic.key if lic is not None else None
    if req.method == "stars":
        # Paid in the bot: the buyer opens it with /start <token> and gets the invoice there.
        if not settings.STARS_BOT_USERNAME or not plan.price_stars:
            raise ApiError(400, "provider_error")
        order = stars.new_order(db, plan, email, renew_key=renew_key)
        return {"order_token": order.token, "pay_url": stars.bot_link(order.token)}

    token = secrets.token_urlsafe(24)  # 32 url-safe characters
    description = f"ProxyBridge: {'продление ключа, ' if renew_key else ''}{plan.title}"
    try:
        if req.method == "yookassa":
            payment = yookassa.create_payment(plan.price_rub, description, token, settings.return_url(token))
            provider_id, pay_url = payment["id"], payment["confirmation_url"]
        else:
            invoice = cryptopay.create_invoice(plan.price_rub, description, token,
                                               paid_btn_url=settings.return_url(token))
            provider_id, pay_url = invoice["invoice_id"], invoice["pay_url"]
    except (yookassa.YooKassaError, cryptopay.CryptoPayError) as exc:
        logger.error("Checkout provider error (%s): %s", req.method, exc)
        raise ApiError(400, "provider_error")

    order = Order(token=token, plan_id=plan.id, email=email, method=req.method,
                  amount_rub=plan.price_rub, provider_payment_id=provider_id, renew_key=renew_key)
    db.add(order)
    db.commit()
    return {"order_token": token, "pay_url": pay_url}


def _get_order(db: Session, token: str) -> Order:
    order = db.scalar(select(Order).where(Order.token == token))
    if order is None:
        raise ApiError(404, "not_found")
    return order


@router.get("/order/{token}")
def get_order(token: str, db: Session = Depends(get_db)):
    return order_to_dict(_get_order(db, token), db=db)


@router.post("/order/{token}/check")
def check_order(token: str, db: Session = Depends(get_db)):
    order = _get_order(db, token)
    order = check_order_with_provider(db, order)
    return order_to_dict(order, db=db)
