"""Telegram webhook of the Stars bot. The reply body carries the bot's answer (see app/stars.py)."""

import hmac
import logging

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse
from sqlalchemy.orm import Session

from .. import stars
from ..config import settings
from ..db import get_db

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/webhook", tags=["webhook"])


@router.post("/telegram-stars")
async def telegram_stars_webhook(request: Request, db: Session = Depends(get_db)):
    secret = request.headers.get("x-telegram-bot-api-secret-token") or ""
    if not settings.STARS_WEBHOOK_SECRET or not hmac.compare_digest(secret, settings.STARS_WEBHOOK_SECRET):
        return JSONResponse({"ok": False}, status_code=403)
    try:
        update = await request.json()
    except Exception:
        return {}
    if not isinstance(update, dict):
        return {}
    try:
        return stars.handle_update(db, update)
    except Exception:  # noqa: BLE001
        db.rollback()
        logger.exception("stars update %s failed", update.get("update_id"))
        # A paid update is re-sent by Telegram after a 500; anything else is dropped with a 200
        # so a broken update is not retried forever.
        paid = isinstance((update.get("message") or {}).get("successful_payment"), dict)
        return JSONResponse({"ok": False}, status_code=500) if paid else {}
