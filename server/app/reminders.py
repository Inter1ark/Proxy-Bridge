"""Letters about a key's life: reminders before the end and one help letter after the purchase.

Every message is recorded in license_mails, so it goes out once. A reminder is tied to the end date
it was about, so a renewed key gets reminders again for its new end date.
"""

import logging
import threading
from datetime import datetime, timedelta

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from . import mailer
from .config import settings
from .models import License, LicenseMail, from_db, iso_z, to_db, utcnow

logger = logging.getLogger(__name__)

INTERVAL_SEC = 600
PER_RUN = 8                      # the free SendPulse plan takes 50 letters an hour
REMIND_PLANS = ("month", "3months")
HELP_AFTER = timedelta(days=2)   # the help letter goes 2 to 4 days after the purchase
HELP_UNTIL = timedelta(days=4)
RETURN_GRACE = timedelta(hours=20)  # an app check later than this after the purchase means the buyer came back


def reminder_kind(left: timedelta) -> str | None:
    if timedelta(0) < left <= timedelta(days=1):
        return "before1"
    if timedelta(days=1) < left <= timedelta(days=3):
        return "before3"
    return None


def _sent(db: Session, lic_id: int, kind: str, period: str) -> bool:
    return db.scalar(select(LicenseMail.id).where(LicenseMail.license_id == lic_id, LicenseMail.kind == kind,
                                                  LicenseMail.period == period)) is not None


def _record(db: Session, lic: License, kind: str, period: str, channel: str) -> None:
    db.add(LicenseMail(license_id=lic.id, kind=kind, period=period, channel=channel))
    try:
        db.commit()
    except IntegrityError:
        db.rollback()


def due_reminders(db: Session, now: datetime) -> list[tuple[License, str, str]]:
    """(license, kind, period) for paid keys that end within 3 days and were not reminded yet."""
    now_db = to_db(now)
    lics = db.scalars(select(License).where(
        License.source == "order", License.revoked.is_(False), License.plan_id.in_(REMIND_PLANS),
        License.expires_at > now_db, License.expires_at <= now_db + timedelta(days=3))
        .order_by(License.expires_at)).all()
    out = []
    for lic in lics:
        kind = reminder_kind(from_db(lic.expires_at) - now)
        period = iso_z(lic.expires_at)
        if kind and not _sent(db, lic.id, kind, period):
            out.append((lic, kind, period))
    return out


def due_help(db: Session, now: datetime) -> list[tuple[License, bool]]:
    """(license, activated) for keys bought 2 to 4 days ago whose app has not come back since day one."""
    now_db = to_db(now)
    lics = db.scalars(select(License).where(
        License.source == "order", License.revoked.is_(False), License.email.isnot(None), License.email != "",
        License.issued_at <= now_db - HELP_AFTER, License.issued_at > now_db - HELP_UNTIL)
        .order_by(License.issued_at)).all()
    out = []
    for lic in lics:
        if _sent(db, lic.id, "help", ""):
            continue
        back = any(d.last_seen and d.last_seen > lic.issued_at + RETURN_GRACE for d in lic.devices)
        if not back:
            out.append((lic, bool(lic.devices)))
    return out


def _telegram_reminder(db: Session, lic: License) -> bool:
    """For a key bought in the bot without an email: the reminder goes to that Telegram chat."""
    from . import stars
    chat = stars.buyer_chat(db, lic)
    if not chat or not settings.STARS_BOT_TOKEN:
        return False
    end_ru, end_en = stars.end_text(lic, True), stars.end_text(lic, False)
    text = (f"Ключ ProxyBridge {lic.key} действует {end_ru}. Продли его кнопкой ниже: срок добавится к текущему, "
            f"вводить ключ заново не нужно.\n\nYour ProxyBridge key {lic.key} works {end_en}. Renew it with a button "
            "below: the time is added to the current end, no need to enter the key again.")
    try:
        r = stars.api("sendMessage", {"chat_id": chat, "text": text, "reply_markup": stars.renew_keyboard(lic, True)})
        return bool(r.get("ok"))
    except Exception as exc:  # noqa: BLE001
        logger.warning("telegram reminder for license %s failed: %s", lic.id, exc)
        return False


def run_once(db: Session, now: datetime | None = None, limit: int = PER_RUN) -> dict:
    now = now or utcnow()
    counts = {"reminders": 0, "telegram": 0, "help": 0, "skipped": 0}
    budget = limit
    for lic, kind, period in due_reminders(db, now):
        if budget <= 0:
            return counts
        if lic.email:
            budget -= 1
            if mailer.send_reminder_email(lic.email, lic.key, lic.plan_id, from_db(lic.expires_at)):
                _record(db, lic, kind, period, "email")
                counts["reminders"] += 1
        elif _telegram_reminder(db, lic):
            _record(db, lic, kind, period, "telegram")
            counts["telegram"] += 1
        else:
            _record(db, lic, kind, period, "skipped")
            counts["skipped"] += 1
    for lic, activated in due_help(db, now):
        if budget <= 0:
            break
        budget -= 1
        if mailer.send_help_email(lic.email, lic.key, activated):
            _record(db, lic, "help", "", "email")
            counts["help"] += 1
    return counts


def worker(stop: threading.Event) -> None:
    from . import db as dbmod
    delay = 60
    while not stop.wait(delay):
        delay = INTERVAL_SEC
        try:
            with dbmod.SessionLocal() as session:
                counts = run_once(session)
            if any(counts.values()):
                logger.info("license letters: %s", counts)
        except Exception as exc:  # noqa: BLE001
            logger.warning("license letters: cycle failed: %s", exc)
