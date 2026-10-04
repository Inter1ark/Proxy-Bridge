"""Payment endpoints used by the site: plans, checkout, order status."""

import logging
import re
import secrets

from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.orm import Session

from ..config import settings
from ..db import get_db
from ..errors import ApiError
from ..models import Order
from ..orders import check_order_with_provider, order_to_dict
from ..plans import get_plan, plans_list
from ..providers import cryptopay, yookassa

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/api", tags=["checkout"])

EMAIL_RE = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")
METHODS = ("yookassa", "cryptobot")


class CheckoutRequest(BaseModel):
    plan_id: str = ""
    email: str = ""
    method: str = ""


def is_valid_email(value: str) -> bool:
    return bool(value) and len(value) <= 254 and EMAIL_RE.match(value) is not None


@router.get("/plans")
def plans():
    return plans_list()


@router.post("/checkout")
def checkout(req: CheckoutRequest, db: Session = Depends(get_db)):
    plan = get_plan(req.plan_id)
    if plan is None:
        raise ApiError(400, "bad_plan")
    email = (req.email or "").strip().lower()
    if not is_valid_email(email):
        raise ApiError(400, "bad_email")
    if req.method not in METHODS:
        raise ApiError(400, "bad_method")

    token = secrets.token_urlsafe(24)  # 32 url-safe characters
    description = f"ProxyBridge: {plan.title}"
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
                  amount_rub=plan.price_rub, provider_payment_id=provider_id)
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
    return order_to_dict(_get_order(db, token))


@router.post("/order/{token}/check")
def check_order(token: str, db: Session = Depends(get_db)):
    order = _get_order(db, token)
    order = check_order_with_provider(db, order)
    return order_to_dict(order)
