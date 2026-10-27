"""Send the license key by email when SMTP is configured, otherwise log it."""

import html
import logging
import smtplib
from email.message import EmailMessage
from email.utils import formataddr, formatdate, make_msgid

from .config import settings
from .plans import get_plan

logger = logging.getLogger(__name__)

SENDER_NAME = "ProxyBridge"
SUPPORT_TG = "https://t.me/inter1ark"
# Download buttons point at the cards of the download page, so they never go stale after a release.
DOWNLOADS = [
    ("Windows", "/skachat/#windows"),
    ("Mac", "/skachat/#macos"),
    ("Android", "/skachat/#android"),
    ("iPhone", "/skachat/#ios"),
]
KINDS = ("purchase", "recovery")


def _site(path: str) -> str:
    return settings.BASE_URL.rstrip("/") + path


def _plan_title(plan_id: str) -> str:
    plan = get_plan(plan_id)
    return plan.title if plan else plan_id


def _intro(plan_id: str, kind: str = "purchase") -> tuple[str, str]:
    """Subject and the opening sentence."""
    if kind == "recovery":
        return ("Ключ ProxyBridge на сутки, без оплаты",
                "Ты начинал оформлять ProxyBridge на сайте, но оплата не завершилась. Вот ключ на 1 день, чтобы "
                "попробовать программу без оплаты: сутки считаются с момента активации.")
    if plan_id == "trial":
        return ("Пробный ключ ProxyBridge на 1 день",
                "Вот пробный ключ ProxyBridge. Он действует 1 день с момента активации, без оплаты и без привязки карты.")
    return ("Ваш ключ ProxyBridge",
            "Спасибо за покупку ProxyBridge! Ключ лицензии ниже, оплата прошла.")


def _outro(kind: str) -> tuple[str, str]:
    """Extra closing paragraph (text, html) for the recovery mailing."""
    if kind != "recovery":
        return "", ""
    buy = _site("/cena/")
    text = (f"Если понравится, ключ на месяц стоит 99 ₽, на 3 месяца 249 ₽, навсегда 699 ₽: {buy}\n"
            "Это письмо отправлено один раз, потому что на сайте был начат заказ с этим адресом. "
            "Больше писем без твоего заказа не будет.\n\n")
    html_part = (f'Если понравится, ключ на месяц стоит 99 ₽, на 3 месяца 249 ₽, навсегда 699 ₽: '
                 f'<a href="{buy}" style="color:#2d5bff">тарифы</a>.<br><br>'
                 'Это письмо отправлено один раз, потому что на сайте был начат заказ с этим адресом. '
                 'Больше писем без твоего заказа не будет.')
    return text, html_part


def _text_body(key: str, plan_id: str, kind: str = "purchase") -> str:
    _, intro = _intro(plan_id, kind)
    outro, _ = _outro(kind)
    links = "".join(f"   {label}: {_site(path)}\n" for label, path in DOWNLOADS)
    return (
        f"{intro}\n\n"
        f"Тариф: {_plan_title(plan_id)}\n"
        f"Ключ: {key}\n\n"
        "Как начать\n"
        "1. Скачай программу, файлы и пошаговая установка для каждой системы:\n"
        f"{links}"
        "2. При первом запуске введи ключ.\n"
        "3. На вкладке «Прокси» добавь адрес прокси и проверь его. Прокси ещё нет: "
        f"купи прямо в программе на вкладке «Получить прокси» или на {_site('/kupit-proksi/')}\n"
        "4. На компьютере на вкладке «Главная» выбери программы и прокси для них или укажи прокси в строке "
        "«Все остальные программы» и нажми «Подключить». На телефоне выбери прокси и нажми кнопку подключения.\n\n"
        f"Ключ работает на {settings.MAX_DEVICES} устройствах, например на ПК и телефоне. "
        "Лишнее устройство отвязывается в «Настройках».\n\n"
        f"{outro}"
        f"Вопросы: Telegram {SUPPORT_TG} или ответь на это письмо.\n"
        f"ProxyBridge, {_site('/')}\n"
    )


def _html_body(key: str, plan_id: str, kind: str = "purchase") -> str:
    _, intro = _intro(plan_id, kind)
    _, outro = _outro(kind)
    e = html.escape
    btn = ("display:inline-block;padding:11px 18px;border-radius:8px;background:#2d5bff;color:#ffffff;"
           "font-weight:700;text-decoration:none;font-size:14px;margin:0 8px 8px 0")
    btn2 = btn.replace("#2d5bff", "#0a0e1a")
    buttons = "\n  ".join(f'<a href="{_site(path)}" style="{btn if i == 0 else btn2}">{e(label)}</a>'
                          for i, (label, path) in enumerate(DOWNLOADS))
    outro_row = (f'<tr><td style="font-size:14px;line-height:1.6;color:#4b5570;padding-bottom:16px">{outro}</td></tr>'
                 if outro else "")
    return f"""<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"></head>
<body style="margin:0;padding:0;background:#f3f5fa;font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:#0a0e1a">
<table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f3f5fa;padding:24px 12px">
<tr><td align="center">
<table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border-radius:14px;padding:28px 28px 20px">
<tr><td style="font-size:22px;font-weight:800;padding-bottom:12px">ProxyBridge</td></tr>
<tr><td style="font-size:15px;line-height:1.55;padding-bottom:18px">{e(intro)}</td></tr>
<tr><td style="padding-bottom:18px">
  <table role="presentation" cellspacing="0" cellpadding="0" width="100%" style="background:#0a0e1a;border-radius:10px">
  <tr><td style="padding:16px 18px;color:#9aa3b8;font-size:12px;letter-spacing:.06em;text-transform:uppercase">Тариф: {e(_plan_title(plan_id))}</td></tr>
  <tr><td style="padding:0 18px 18px;color:#ffffff;font-family:SFMono-Regular,Menlo,Consolas,monospace;font-size:22px;font-weight:700;letter-spacing:.04em">{e(key)}</td></tr>
  </table>
</td></tr>
<tr><td style="font-size:16px;font-weight:700;padding-bottom:10px">Как начать</td></tr>
<tr><td style="font-size:15px;line-height:1.6;padding-bottom:6px">1. Скачай программу для своей системы:</td></tr>
<tr><td style="padding:4px 0 12px">
  {buttons}
  <div style="font-size:13px;color:#4b5570;margin-top:4px">Файлы и пошаговая установка: <a href="{_site('/skachat/')}" style="color:#2d5bff">страница «Скачать»</a>.</div>
</td></tr>
<tr><td style="font-size:15px;line-height:1.6;padding-bottom:6px">2. При первом запуске введи ключ.</td></tr>
<tr><td style="font-size:15px;line-height:1.6;padding-bottom:6px">3. На вкладке «Прокси» добавь адрес прокси и проверь его. Прокси ещё нет: купи прямо в программе на вкладке «Получить прокси» или на <a href="{_site('/kupit-proksi/')}" style="color:#2d5bff">сайте</a>.</td></tr>
<tr><td style="font-size:15px;line-height:1.6;padding-bottom:16px">4. На компьютере на вкладке «Главная» выбери программы и прокси для них или укажи прокси в строке «Все остальные программы» и нажми «Подключить». На телефоне выбери прокси и нажми кнопку подключения.</td></tr>
<tr><td style="font-size:14px;line-height:1.6;color:#4b5570;padding-bottom:16px">Ключ работает на {settings.MAX_DEVICES} устройствах, например на ПК и телефоне. Лишнее устройство отвязывается в «Настройках».</td></tr>
{outro_row}
<tr><td style="font-size:14px;line-height:1.6;color:#4b5570;border-top:1px solid #e6e9f2;padding-top:14px">Вопросы: <a href="{SUPPORT_TG}" style="color:#2d5bff">Telegram</a> или ответь на это письмо.<br><a href="{_site('/')}" style="color:#4b5570">proxybridge.org</a></td></tr>
</table>
</td></tr></table>
</body></html>
"""


def build_message(to_email: str, key: str, plan_id: str, kind: str = "purchase") -> EmailMessage:
    if kind not in KINDS:
        raise ValueError(f"unknown email kind {kind}")
    subject, _ = _intro(plan_id, kind)
    sender = settings.SMTP_FROM or settings.SMTP_USER
    msg = EmailMessage()
    msg["Subject"] = subject
    msg["From"] = formataddr((SENDER_NAME, sender))
    msg["To"] = to_email
    msg["Reply-To"] = sender
    msg["Date"] = formatdate(localtime=True)
    msg["Message-ID"] = make_msgid(domain=sender.split("@")[-1] if "@" in sender else "proxybridge.org")
    msg.set_content(_text_body(key, plan_id, kind))
    msg.add_alternative(_html_body(key, plan_id, kind), subtype="html")
    return msg


def send_key_email(to_email: str, key: str, plan_id: str, kind: str = "purchase") -> bool:
    """Returns True if the email was handed to the SMTP server."""
    if not to_email:
        return False
    if not settings.smtp_configured:
        logger.info("SMTP not configured; key for %s (plan %s) is %s", to_email, plan_id, key)
        return False
    msg = build_message(to_email, key, plan_id, kind)
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
        logger.info("Key email sent to %s (plan %s)", to_email, plan_id)
        return True
    except Exception as exc:
        logger.error("Failed to send key email to %s: %s", to_email, exc)
        return False
