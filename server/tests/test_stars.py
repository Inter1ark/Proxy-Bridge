"""Telegram Stars: site checkout, bot buttons, pre-checkout and payment through the webhook."""

import pytest

from app.config import settings
from app.keys import normalize_key
from app.models import License, Order

SECRET = "stars-secret"
HDR = {"X-Telegram-Bot-Api-Secret-Token": SECRET}
CHAT = 555


@pytest.fixture(autouse=True)
def stars_bot(monkeypatch):
    monkeypatch.setattr(settings, "STARS_BOT_TOKEN", "")  # no outgoing calls in tests
    monkeypatch.setattr(settings, "STARS_BOT_USERNAME", "pb_test_bot")
    monkeypatch.setattr(settings, "STARS_WEBHOOK_SECRET", SECRET)
    monkeypatch.setattr(settings, "STARS_RUB_RATE", 1.0)


def hook(client, update):
    r = client.post("/webhook/telegram-stars", json=update, headers=HDR)
    assert r.status_code == 200, r.text
    return r.json()


def user(lang="ru"):
    return {"id": CHAT, "is_bot": False, "first_name": "A", "language_code": lang}


def message(text, lang="ru"):
    return {"update_id": 1, "message": {"message_id": 1, "from": user(lang), "text": text,
                                         "chat": {"id": CHAT, "type": "private"}}}


def pre_checkout(token, amount):
    return {"update_id": 2, "pre_checkout_query": {"id": "q1", "from": user(), "currency": "XTR",
                                                    "total_amount": amount, "invoice_payload": token}}


def paid(token, amount, charge="ch-1"):
    return {"update_id": 3, "message": {"message_id": 2, "from": user(), "chat": {"id": CHAT, "type": "private"},
                                         "successful_payment": {"currency": "XTR", "total_amount": amount,
                                                                "invoice_payload": token,
                                                                "telegram_payment_charge_id": charge,
                                                                "provider_payment_charge_id": ""}}}


def test_webhook_needs_secret(client):
    assert client.post("/webhook/telegram-stars", json=message("/start")).status_code == 403
    r = client.post("/webhook/telegram-stars", json=message("/start"), headers={"X-Telegram-Bot-Api-Secret-Token": "x"})
    assert r.status_code == 403


def test_site_checkout_then_pay_in_bot(client, db_session):
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "stars"})
    assert r.status_code == 200, r.text
    token = r.json()["order_token"]
    assert r.json()["pay_url"] == f"https://t.me/pb_test_bot?start={token}"
    st = client.get(f"/api/order/{token}").json()
    assert st["status"] == "pending" and st["amount_stars"] == 150 and st["tg_url"].endswith(token)

    inv = hook(client, message(f"/start {token}"))
    assert inv["method"] == "sendInvoice" and inv["currency"] == "XTR" and inv["provider_token"] == ""
    assert inv["payload"] == token and inv["prices"] == [{"label": "1 месяц", "amount": 150}]

    assert hook(client, pre_checkout(token, 150))["ok"] is True
    bad = hook(client, pre_checkout(token, 1))
    assert bad["ok"] is False and bad["error_message"]

    reply = hook(client, paid(token, 150))
    assert reply["method"] == "sendMessage" and reply["chat_id"] == CHAT
    st = client.get(f"/api/order/{token}").json()
    assert st["status"] == "paid" and normalize_key(st["key"]) == st["key"] and st["key"] in reply["text"]
    order = db_session.query(Order).filter_by(token=token).one()
    assert order.provider_payment_id == f"{CHAT}:ch-1" and order.pay_type == "XTR"

    # Telegram re-sends the update: still one key, the same text.
    again = hook(client, paid(token, 150))
    assert st["key"] in again["text"]
    assert db_session.query(License).count() == 1
    # The order is closed for a new pre-checkout.
    assert hook(client, pre_checkout(token, 150))["ok"] is False


def test_bot_only_purchase(client, db_session):
    menu = hook(client, message("/start", lang="en"))
    buttons = [b[0]["callback_data"] for b in menu["reply_markup"]["inline_keyboard"]]
    assert buttons == ["buy:month", "buy:3months", "buy:lifetime"]

    cq = {"update_id": 4, "callback_query": {"id": "c1", "from": user("en"), "data": "buy:lifetime",
                                             "message": {"message_id": 3, "chat": {"id": CHAT, "type": "private"}}}}
    inv = hook(client, cq)
    assert inv["method"] == "sendInvoice" and inv["prices"][0]["amount"] == 1000 and inv["title"] == "ProxyBridge: Lifetime"
    # Pressing the button again reuses the unpaid order.
    assert hook(client, cq)["payload"] == inv["payload"]
    assert db_session.query(Order).filter_by(method="stars").count() == 1

    hook(client, pre_checkout(inv["payload"], 1000))
    reply = hook(client, paid(inv["payload"], 1000))
    order = db_session.query(Order).filter_by(token=inv["payload"]).one()
    assert order.status == "paid" and order.email == "" and order.key in reply["text"]


def test_payment_with_wrong_amount_is_not_accepted(client, db_session):
    r = client.post("/api/checkout", json={"plan_id": "3months", "email": "a@b.co", "method": "stars"})
    token = r.json()["order_token"]
    hook(client, paid(token, 10))
    assert db_session.query(Order).filter_by(token=token).one().status == "pending"
    assert db_session.query(License).count() == 0


def test_support_and_terms(client):
    assert "support@proxybridge.org" in hook(client, message("/paysupport"))["text"]
    assert "offer.html" in hook(client, message("/terms", lang="en"))["text"]
