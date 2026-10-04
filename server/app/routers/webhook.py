"""YooKassa webhook (payment.succeeded).

The body is never trusted: only object.id is taken from it, the payment is
re-fetched from the YooKassa API, and the order is marked paid only when the
API says status == succeeded and the amount equals the plan price.
"""

import logging

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse
from sqlalchemy import select
from sqlalchemy.orm import Session

from ..db import get_db
from ..models import Order
from ..orders import apply_yookassa_payment
from ..providers import yookassa

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/webhook", tags=["webhook"])


@router.post("/yookassa")
async def yookassa_webhook(request: Request, db: Session = Depends(get_db)):
    try:
        body = await request.json()
    except Exception:
        return JSONResponse({"ok": False, "error": "bad_request"}, status_code=400)
    if not isinstance(body, dict):
        return JSONResponse({"ok": False, "error": "bad_request"}, status_code=400)

    event = body.get("event")
    payment_id = (body.get("object") or {}).get("id") if isinstance(body.get("object"), dict) else None
    logger.info("YooKassa webhook event=%s payment_id=%s", event, payment_id)
    if event != "payment.succeeded":
        return {"ok": True}
    if not payment_id or not isinstance(payment_id, str):
        return JSONResponse({"ok": False, "error": "bad_request"}, status_code=400)

    try:
        payment = yookassa.get_payment(payment_id)
    except yookassa.YooKassaError as exc:
        # Non-2xx makes YooKassa retry the notification later.
        logger.error("YooKassa webhook: cannot fetch payment %s: %s", payment_id, exc)
        return JSONResponse({"ok": False, "error": "provider_error"}, status_code=502)

    token = (payment.get("metadata") or {}).get("order_token")
    order = None
    if token:
        order = db.scalar(select(Order).where(Order.token == str(token)))
    if order is None:
        order = db.scalar(select(Order).where(Order.provider_payment_id == payment_id))
    if order is None:
        logger.warning("YooKassa webhook: no order for payment %s", payment_id)
        return {"ok": True}

    apply_yookassa_payment(db, order, payment)
    return {"ok": True}
