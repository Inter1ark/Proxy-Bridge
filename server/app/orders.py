"""Order and license business logic shared by routers.

Everything here is synchronous and takes an explicit SQLAlchemy session.
"""

import logging
from datetime import timedelta
from decimal import Decimal, InvalidOperation

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from . import mailer
from .keys import generate_key
from .models import License, Order, iso_z, to_db, utcnow
from .plans import get_plan
from .providers import cryptopay, yookassa
from . import notify

logger = logging.getLogger(__name__)


def issue_license(db: Session, plan_id: str, email: str | None, order: Order | None = None,
                  source: str = "order") -> License:
    """Create a License row for the plan (not committed)."""
    plan = get_plan(plan_id)
    if plan is None:
        raise ValueError(f"unknown plan {plan_id}")
    expires_at = None if plan.days is None else to_db(utcnow() + timedelta(days=plan.days))
    for _ in range(10):
        key = generate_key()
        if db.scalar(select(License.id).where(License.key == key)) is None:
            break
    lic = License(key=key, plan_id=plan.id, email=email or None, expires_at=expires_at,
                  source=source, order=order)
    db.add(lic)
    return lic


def mark_order_paid(db: Session, order: Order, provider_payment_id: str | None = None) -> License:
    """Idempotently mark the order paid, issue exactly one key and email it."""
    existing = db.scalar(select(License).where(License.order_id == order.id))
    if order.status == "paid" and existing is not None:
        return existing

    if provider_payment_id and not order.provider_payment_id:
        order.provider_payment_id = provider_payment_id

    if existing is None:
        lic = issue_license(db, order.plan_id, order.email, order=order)
    else:
        lic = existing
    order.status = "paid"
    order.paid_at = to_db(utcnow())
    order.key = lic.key
    try:
        db.commit()
    except IntegrityError:
        # A concurrent request already issued the license for this order.
        db.rollback()
        db.refresh(order)
        lic = db.scalar(select(License).where(License.order_id == order.id))
        if lic is None:
            raise
        return lic

    logger.info("Order %s paid, key issued (plan %s)", order.token, order.plan_id)
    mailer.send_key_email(order.email, lic.key, order.plan_id)
    notify.notify_paid_order(order, lic.key)
    return lic


def mark_order_canceled(db: Session, order: Order) -> None:
    if order.status == "pending":
        order.status = "canceled"
        db.commit()


def amount_matches_plan(amount: dict, plan_id: str) -> bool:
    plan = get_plan(plan_id)
    if plan is None:
        return False
    if (amount.get("currency") or "RUB") != "RUB":
        return False
    try:
        return Decimal(str(amount.get("value"))) == Decimal(plan.price_rub)
    except (InvalidOperation, TypeError):
        return False


def apply_yookassa_payment(db: Session, order: Order, payment: dict) -> bool:
    """Apply a payment fetched from the YooKassa API. Returns True when marked paid."""
    status = payment.get("status")
    if status == "succeeded":
        if not amount_matches_plan(payment.get("amount") or {}, order.plan_id):
            logger.warning("YooKassa payment %s amount mismatch for order %s: %s",
                           payment.get("id"), order.token, payment.get("amount"))
            return False
        if order.provider_payment_id and payment.get("id") and order.provider_payment_id != payment.get("id"):
            logger.warning("YooKassa payment id mismatch for order %s", order.token)
            return False
        mark_order_paid(db, order, provider_payment_id=payment.get("id"))
        return True
    if status == "canceled":
        mark_order_canceled(db, order)
    return False


def check_order_with_provider(db: Session, order: Order) -> Order:
    """Ask the provider for the current status and update the order."""
    if order.status != "pending" or not order.provider_payment_id:
        return order
    try:
        if order.method == "yookassa":
            payment = yookassa.get_payment(order.provider_payment_id)
            apply_yookassa_payment(db, order, payment)
        elif order.method == "cryptobot":
            invoice = cryptopay.get_invoice(order.provider_payment_id)
            if invoice is None:
                return order
            inv_status = invoice.get("status")
            if inv_status == "paid":
                mark_order_paid(db, order)
            elif inv_status == "expired":
                mark_order_canceled(db, order)
    except (yookassa.YooKassaError, cryptopay.CryptoPayError) as exc:
        logger.error("Provider check failed for order %s: %s", order.token, exc)
    return order


def order_to_dict(order: Order, admin: bool = False) -> dict:
    data = {
        "status": order.status,
        "plan_id": order.plan_id,
        "email": order.email,
        "key": order.key if order.status == "paid" else None,
        "method": order.method,
    }
    if admin:
        data.update({
            "token": order.token,
            "amount_rub": order.amount_rub,
            "provider_payment_id": order.provider_payment_id,
            "created_at": iso_z(order.created_at),
            "paid_at": iso_z(order.paid_at),
        })
    return data
