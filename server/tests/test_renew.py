"""Renewing a key from the site or the bot, and the letters before the end and after the purchase."""

from datetime import timedelta

import pytest

from app import mailer, reminders
from app.config import settings
from app.models import Device, License, LicenseMail, Order, from_db, to_db, utcnow
from tests.test_stars import CHAT, hook, paid, pre_checkout, user


def buy(client, mock_cryptopay, plan="month", email="a@b.co", renew_key=None):
    body = {"plan_id": plan, "email": email, "method": "cryptobot"}
    if renew_key is not None:
        body["renew_key"] = renew_key
    r = client.post("/api/checkout", json=body)
    assert r.status_code == 200, r.text
    token = r.json()["order_token"]
    inv = next(i for i in mock_cryptopay["invoices"].values() if i.get("status") != "paid")
    inv["status"] = "paid"
    return client.post(f"/api/order/{token}/check").json(), token


@pytest.fixture
def letters(monkeypatch):
    sent = []
    monkeypatch.setattr(mailer, "send_reminder_email", lambda *a, **k: sent.append(("remind",) + a) or True)
    monkeypatch.setattr(mailer, "send_help_email", lambda *a, **k: sent.append(("help",) + a) or True)
    monkeypatch.setattr(mailer, "send_renewal_email", lambda *a, **k: sent.append(("renewed",) + a) or True)
    return sent


def test_renew_extends_the_same_key(client, mock_cryptopay, db_session, letters):
    first, _ = buy(client, mock_cryptopay)
    key = first["key"]
    lic = db_session.query(License).filter_by(key=key).one()
    end = from_db(lic.expires_at)

    info = client.get(f"/api/renew/{key.lower()}").json()
    assert info["key"] == key and info["plan_id"] == "month" and info["expired"] is False
    assert info["email_hint"] == "a***@b.co"

    # The email may be left empty: the receipt goes to the key's address.
    second, token = buy(client, mock_cryptopay, plan="3months", email="", renew_key=key)
    assert second["status"] == "paid" and second["key"] == key and second["renew"] is True
    db_session.expire_all()
    lic = db_session.query(License).filter_by(key=key).one()
    assert from_db(lic.expires_at) - end == timedelta(days=90) and lic.plan_id == "3months"
    assert db_session.query(License).count() == 1
    order = db_session.query(Order).filter_by(token=token).one()
    assert order.renew_key == key and order.email == "a@b.co"
    assert letters[-1][:2] == ("renewed", "a@b.co")

    # Checking again does not add the days twice.
    client.post(f"/api/order/{token}/check")
    db_session.expire_all()
    assert from_db(db_session.query(License).filter_by(key=key).one().expires_at) - end == timedelta(days=90)


def test_renew_after_expiry_counts_from_now(client, mock_cryptopay, db_session, expire_key, letters):
    first, _ = buy(client, mock_cryptopay)
    expire_key(first["key"])
    assert client.get(f"/api/renew/{first['key']}").json()["expired"] is True
    buy(client, mock_cryptopay, renew_key=first["key"])
    db_session.expire_all()
    left = from_db(db_session.query(License).filter_by(key=first["key"]).one().expires_at) - utcnow()
    assert timedelta(days=29, hours=23) < left <= timedelta(days=30)


def test_renew_to_lifetime_and_bad_keys(client, mock_cryptopay, db_session, letters):
    first, _ = buy(client, mock_cryptopay)
    buy(client, mock_cryptopay, plan="lifetime", renew_key=first["key"])
    db_session.expire_all()
    lic = db_session.query(License).filter_by(key=first["key"]).one()
    assert lic.expires_at is None and lic.plan_id == "lifetime"

    r = client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "cryptobot",
                                           "renew_key": first["key"]})
    assert r.status_code == 400 and r.json()["error"] == "lifetime"
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "cryptobot",
                                           "renew_key": "PB-AAAA-AAAA-AAAA-AAAA"})
    assert r.status_code == 404 and r.json()["error"] == "key_not_found"
    assert client.get("/api/renew/nonsense").status_code == 404


def test_renew_with_stars_from_the_site(client, db_session, monkeypatch, issue_key, letters):
    monkeypatch.setattr(settings, "STARS_BOT_TOKEN", "")
    monkeypatch.setattr(settings, "STARS_BOT_USERNAME", "pb_test_bot")
    monkeypatch.setattr(settings, "STARS_WEBHOOK_SECRET", "stars-secret")
    key = issue_key("month", email="c@d.co")
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "", "method": "stars", "renew_key": key})
    token = r.json()["order_token"]
    inv = hook(client, {"update_id": 1, "message": {"message_id": 1, "from": user(), "text": f"/start {token}",
                                                    "chat": {"id": CHAT, "type": "private"}}})
    assert key in inv["description"]
    assert hook(client, pre_checkout(token, 150))["ok"] is True
    reply = hook(client, paid(token, 150))
    assert "продлён" in reply["text"] and key in reply["text"]
    assert db_session.query(License).count() == 1


def test_bot_renew_button_only_for_the_buyer(client, db_session, monkeypatch, letters):
    monkeypatch.setattr(settings, "STARS_BOT_USERNAME", "pb_test_bot")
    monkeypatch.setattr(settings, "STARS_WEBHOOK_SECRET", "stars-secret")
    cq = {"update_id": 4, "callback_query": {"id": "c1", "from": user(), "data": "buy:month",
                                             "message": {"message_id": 3, "chat": {"id": CHAT, "type": "private"}}}}
    inv = hook(client, cq)
    hook(client, paid(inv["payload"], 150))
    lic = db_session.query(License).one()

    stranger = {"update_id": 5, "callback_query": {"id": "c2", "from": {"id": 999, "language_code": "ru"},
                                                   "data": f"ren:month:{lic.id}",
                                                   "message": {"message_id": 4, "chat": {"id": 999, "type": "private"}}}}
    assert hook(client, stranger)["method"] == "sendMessage"

    mine = {"update_id": 6, "callback_query": {"id": "c3", "from": user(), "data": f"ren:3months:{lic.id}",
                                               "message": {"message_id": 5, "chat": {"id": CHAT, "type": "private"}}}}
    inv2 = hook(client, mine)
    assert inv2["method"] == "sendInvoice" and inv2["prices"][0]["amount"] == 375
    hook(client, paid(inv2["payload"], 375))
    db_session.expire_all()
    lic = db_session.query(License).one()
    left = from_db(lic.expires_at) - utcnow()
    assert timedelta(days=119) < left <= timedelta(days=120)


def _license(db, key, email, days_left=None, issued_ago=timedelta(days=10), source="order", plan="month"):
    now = utcnow()
    lic = License(key=key, plan_id=plan, email=email, source=source, issued_at=to_db(now - issued_ago),
                  expires_at=to_db(now + days_left) if days_left is not None else None)
    db.add(lic)
    db.commit()
    return lic


def test_reminders_go_once_per_end_date(client, db_session, letters):
    soon = _license(db_session, "PB-AAAA-AAAA-AAAA-AAA2", "x@y.co", timedelta(hours=20))
    later = _license(db_session, "PB-AAAA-AAAA-AAAA-AAA3", "z@y.co", timedelta(days=2, hours=5))
    _license(db_session, "PB-AAAA-AAAA-AAAA-AAA4", "far@y.co", timedelta(days=10))
    _license(db_session, "PB-AAAA-AAAA-AAAA-AAA5", "gift@y.co", timedelta(hours=5), source="admin")
    _license(db_session, "PB-AAAA-AAAA-AAAA-AAA6", "t@y.co", timedelta(hours=5), plan="trial")

    counts = reminders.run_once(db_session)
    assert counts["reminders"] == 2
    assert sorted(x[1] for x in letters if x[0] == "remind") == ["x@y.co", "z@y.co"]
    kinds = {m.license_id: m.kind for m in db_session.query(LicenseMail)}
    assert kinds == {soon.id: "before1", later.id: "before3"}

    letters.clear()
    reminders.run_once(db_session)
    assert letters == []

    # Two days later the second key is inside the last day: it gets the second reminder.
    reminders.run_once(db_session, now=utcnow() + timedelta(days=1, hours=12))
    assert [x[1] for x in letters] == ["z@y.co"]

    # A renewed key is reminded again before its new end.
    letters.clear()
    soon.expires_at = to_db(utcnow() + timedelta(days=30, hours=20))
    db_session.commit()
    reminders.run_once(db_session, now=utcnow() + timedelta(days=30))
    assert [x[1] for x in letters] == ["x@y.co"]


def test_help_letter_for_buyers_who_did_not_come_back(client, db_session, letters):
    gone = _license(db_session, "PB-BBBB-BBBB-BBBB-BBB2", "gone@y.co", timedelta(days=27), timedelta(days=3))
    db_session.add(Device(license=gone, hwid="a" * 64, activated_at=gone.issued_at,
                          last_seen=gone.issued_at + timedelta(hours=1)))
    back = _license(db_session, "PB-BBBB-BBBB-BBBB-BBB3", "back@y.co", timedelta(days=27), timedelta(days=3))
    db_session.add(Device(license=back, hwid="b" * 64, activated_at=back.issued_at,
                          last_seen=back.issued_at + timedelta(days=2)))
    _license(db_session, "PB-BBBB-BBBB-BBBB-BBB4", "never@y.co", timedelta(days=27), timedelta(days=3))
    _license(db_session, "PB-BBBB-BBBB-BBBB-BBB5", "old@y.co", timedelta(days=20), timedelta(days=10))
    _license(db_session, "PB-BBBB-BBBB-BBBB-BBB6", "new@y.co", timedelta(days=29), timedelta(hours=12))
    db_session.commit()

    assert reminders.run_once(db_session)["help"] == 2
    assert sorted((x[1], x[3]) for x in letters) == [("gone@y.co", True), ("never@y.co", False)]
    letters.clear()
    reminders.run_once(db_session)
    assert letters == []


def test_budget_limits_one_run(client, db_session, letters):
    for i in range(5):
        _license(db_session, f"PB-CCCC-CCCC-CCCC-CC{i + 2}{i + 2}", f"{i}@y.co", timedelta(hours=10 + i))
    assert reminders.run_once(db_session, limit=3)["reminders"] == 3
    assert reminders.run_once(db_session, limit=3)["reminders"] == 2


def test_letter_texts():
    end = utcnow() + timedelta(days=2)
    msg = mailer.reminder_message("a@b.co", "PB-AAAA-AAAA-AAAA-AAAA", "month", end)
    text = msg.get_body(("plain",)).get_content()
    assert "renew=PB-AAAA-AAAA-AAAA-AAAA" in text and "Автоматических списаний нет" in text
    assert msg["Subject"].startswith("Ключ ProxyBridge закончится")
    assert "завтра" in mailer.reminder_message("a@b.co", "PB-AAAA-AAAA-AAAA-AAAA", "month",
                                               utcnow() + timedelta(days=1), now=utcnow())["Subject"]
    done = mailer.renewal_message("a@b.co", "PB-AAAA-AAAA-AAAA-AAAA", "month", end)
    assert "продлён" in done.get_body(("plain",)).get_content()
    assert "бессрочный" in mailer.renewal_message("a@b.co", "K", "lifetime", None).get_body(("plain",)).get_content()
    helpm = mailer.help_message("a@b.co", "PB-AAAA-AAAA-AAAA-AAAA", activated=False)
    assert "не активирован" in helpm.get_body(("plain",)).get_content()
    for m in (msg, done, helpm):
        body = m.get_body(("plain",)).get_content() + m.get_body(("html",)).get_content()
        assert "—" not in body and "–" not in body
