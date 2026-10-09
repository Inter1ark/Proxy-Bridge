"""Order and license business logic shared by routers.

Everything here is synchronous and takes an explicit SQLAlchemy session.
"""

import logging
from datetime import timedelta
from decimal import Decimal, InvalidOperation

from sqlalchemy import func, select
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


def _record_yookassa_details(order: Order, payment: dict) -> None:
    """Store fail reason, payment method and income from a YooKassa payment (not committed)."""
    reason = payment.get("cancellation_reason")
    if reason:
        order.fail_reason = str(reason)[:64]
    pay_type = payment.get("payment_method_type")
    if pay_type:
        order.pay_type = str(pay_type)[:32]
    income = payment.get("income_amount")
    if income not in (None, ""):
        try:
            order.income_rub = float(Decimal(str(income)))
        except (InvalidOperation, TypeError, ValueError):
            pass
    order.checked_at = to_db(utcnow())


def _record_cryptobot_details(order: Order, invoice: dict) -> None:
    """Store the paid asset and the expiry reason from a CryptoBot invoice (not committed)."""
    asset = invoice.get("paid_asset")
    if asset:
        order.pay_type = str(asset)[:32]
    if invoice.get("status") == "expired":
        order.fail_reason = "expired"
    order.checked_at = to_db(utcnow())


def apply_yookassa_payment(db: Session, order: Order, payment: dict) -> bool:
    """Apply a payment fetched from the YooKassa API. Returns True when marked paid."""
    status = payment.get("status")
    payment_id = payment.get("id")
    same_payment = not (order.provider_payment_id and payment_id and order.provider_payment_id != payment_id)
    if same_payment:
        _record_yookassa_details(order, payment)
    if status == "succeeded":
        if not amount_matches_plan(payment.get("amount") or {}, order.plan_id):
            logger.warning("YooKassa payment %s amount mismatch for order %s: %s",
                           payment_id, order.token, payment.get("amount"))
            db.commit()
            return False
        if not same_payment:
            logger.warning("YooKassa payment id mismatch for order %s", order.token)
            db.commit()
            return False
        mark_order_paid(db, order, provider_payment_id=payment_id)
        db.commit()
        return True
    if status == "canceled":
        mark_order_canceled(db, order)
    db.commit()
    return False


def check_order_with_provider(db: Session, order: Order, any_status: bool = False) -> Order:
    """Ask the provider for the current status and update the order.

    By default only pending orders are checked. With any_status=True paid and
    canceled orders are fetched too, but for them only the details
    (fail_reason, pay_type, income_rub, checked_at) are updated, never the status.
    """
    if not order.provider_payment_id:
        return order
    if order.status != "pending" and not any_status:
        return order
    may_change_status = order.status == "pending"
    try:
        if order.method == "yookassa":
            payment = yookassa.get_payment(order.provider_payment_id)
            if may_change_status:
                apply_yookassa_payment(db, order, payment)
            else:
                if not payment.get("id") or payment.get("id") == order.provider_payment_id:
                    _record_yookassa_details(order, payment)
                db.commit()
        elif order.method == "cryptobot":
            invoice = cryptopay.get_invoice(order.provider_payment_id)
            if invoice is None:
                return order
            _record_cryptobot_details(order, invoice)
            inv_status = invoice.get("status")
            if may_change_status and inv_status == "paid":
                mark_order_paid(db, order)
            elif may_change_status and inv_status == "expired":
                mark_order_canceled(db, order)
            db.commit()
    except (yookassa.YooKassaError, cryptopay.CryptoPayError) as exc:
        db.rollback()
        logger.error("Provider check failed for order %s: %s", order.token, exc)
    return order


def backfill_provider_details(limit: int = 500, db: Session | None = None) -> dict:
    """One-shot: re-check orders of any status that were never checked (checked_at IS NULL).

    Paid and canceled orders only get their details filled; a pending order may
    become paid or canceled exactly like in the regular poller. Not run at startup.
    """
    own_session = db is None
    if own_session:
        from . import db as dbmod
        db = dbmod.SessionLocal()
    counts = {"total": 0, "checked": 0, "failed": 0, "became_paid": 0, "became_canceled": 0}
    try:
        orders = db.scalars(select(Order)
                            .where(Order.checked_at.is_(None), Order.provider_payment_id.isnot(None))
                            .order_by(Order.id.desc())
                            .limit(limit)).all()
        counts["total"] = len(orders)
        for order in orders:
            before = order.status
            try:
                check_order_with_provider(db, order, any_status=True)
            except Exception as exc:  # noqa: BLE001
                db.rollback()
                logger.warning("backfill: order %s failed: %s", order.id, exc)
            if order.checked_at is None:
                counts["failed"] += 1
                continue
            counts["checked"] += 1
            if before == "pending" and order.status == "paid":
                counts["became_paid"] += 1
            elif before == "pending" and order.status == "canceled":
                counts["became_canceled"] += 1
        counts["remaining"] = db.scalar(select(func.count(Order.id))
                                        .where(Order.checked_at.is_(None),
                                               Order.provider_payment_id.isnot(None))) or 0
    finally:
        if own_session:
            db.close()
    return counts


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
