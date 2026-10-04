"""Send the license key by email when SMTP is configured, otherwise log it."""

import logging
import smtplib
from email.message import EmailMessage

from .config import settings
from .plans import get_plan

logger = logging.getLogger(__name__)


def build_message(to_email: str, key: str, plan_id: str) -> EmailMessage:
    plan = get_plan(plan_id)
    title = plan.title if plan else plan_id
    msg = EmailMessage()
    msg["Subject"] = "Ваш ключ ProxyBridge"
    msg["From"] = settings.SMTP_FROM or settings.SMTP_USER
    msg["To"] = to_email
    msg.set_content(
        "Спасибо за покупку ProxyBridge!\n\n"
        f"Тариф: {title}\n"
        f"Ключ лицензии: {key}\n\n"
        "Введите ключ в окне активации программы. "
        f"Ключ можно использовать максимум на {settings.MAX_DEVICES} устройствах.\n\n"
        f"Скачать программу и прочитать инструкцию: {settings.BASE_URL}\n"
    )
    return msg


def send_key_email(to_email: str, key: str, plan_id: str) -> bool:
    """Returns True if the email was handed to the SMTP server."""
    if not to_email:
        return False
    if not settings.smtp_configured:
        logger.info("SMTP not configured; key for %s (plan %s) is %s", to_email, plan_id, key)
        return False
    msg = build_message(to_email, key, plan_id)
    try:
        if settings.SMTP_PORT == 465:
            server = smtplib.SMTP_SSL(settings.SMTP_HOST, settings.SMTP_PORT, timeout=20)
        else:
            server = smtplib.SMTP(settings.SMTP_HOST, settings.SMTP_PORT, timeout=20)
        with server:
            if settings.SMTP_PORT != 465:
                server.ehlo()
                try:
                    server.starttls()
                    server.ehlo()
                except smtplib.SMTPNotSupportedError:
                    pass
            if settings.SMTP_USER:
                server.login(settings.SMTP_USER, settings.SMTP_PASS)
            server.send_message(msg)
        logger.info("Key email sent to %s", to_email)
        return True
    except Exception as exc:
        logger.error("Failed to send key email to %s: %s", to_email, exc)
        return False
