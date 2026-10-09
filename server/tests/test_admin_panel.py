import csv
import io
import sqlite3
from datetime import datetime, timedelta

import pytest

from tests.conftest import ADMIN_LOGIN, ADMIN_PASSWORD, HWID_A, HWID_B

from app import admin_auth
from app import db as app_db
from app.admin_data import MSK, msk_day_start_utc
from app.config import settings
from app.models import Device, License, Order, to_db, utcnow

CSRF = {"X-Requested-With": "pb-admin"}


def _login(client, login=ADMIN_LOGIN, password=ADMIN_PASSWORD, headers=None):
    return client.post("/api/admin/login", json={"login": login, "password": password}, headers=headers or {})


@pytest.fixture
def logged_in(client):
    r = _login(client)
    assert r.status_code == 200, r.text
    return client


def _add_order(db, *, status="paid", plan="month", method="yookassa", email="buyer@mail.ru",
               created=None, income=None, key=None, pid=None, fail_reason=None, pay_type=None,
               checked=True):
    created = created or to_db(utcnow())
    amount = {"month": 99, "3months": 249, "lifetime": 699}[plan]
    n = db.query(Order).count() + 1
    order = Order(token=f"tok{n:029d}", plan_id=plan, email=email, method=method, status=status,
                  amount_rub=amount, provider_payment_id=pid or f"pid-{n}", key=key, created_at=created,
                  paid_at=created if status == "paid" else None, income_rub=income,
                  fail_reason=fail_reason, pay_type=pay_type,
                  checked_at=to_db(utcnow()) if checked else None)
    db.add(order)
    db.commit()
    return order


# ---------- auth ----------

def test_login_success_sets_cookie_and_allows_get(client):
    r = _login(client)
    assert r.status_code == 200 and r.json()["ok"] is True
    cookie = r.headers["set-cookie"]
    assert cookie.startswith("pb_admin=")
    low = cookie.lower()
    assert "httponly" in low and "samesite=strict" in low and "path=/" in low
    assert "secure" not in low  # BASE_URL is http in tests
    r = client.get("/api/admin/stats")
    assert r.status_code == 200
    assert client.get("/api/admin/me").json()["auth"] == "cookie"

    r = client.post("/api/admin/logout")
    assert r.status_code == 200
    client.cookies.clear()
    assert client.get("/api/admin/stats").status_code == 401


def test_login_cookie_secure_on_https(client, monkeypatch):
    monkeypatch.setattr(settings, "BASE_URL", "https://www.proxybridge.org")
    r = _login(client)
    assert "secure" in r.headers["set-cookie"].lower()


def test_login_wrong_credentials(client):
    r = _login(client, password="nope")
    assert r.status_code == 401 and r.json() == {"ok": False, "error": "bad_credentials"}
    r = _login(client, login="other")
    assert r.status_code == 401
    assert client.get("/api/admin/stats").status_code == 401


def test_login_rate_limit_per_ip(client):
    for _ in range(5):
        assert _login(client, password="bad").status_code == 401
    r = _login(client)
    assert r.status_code == 429 and r.json() == {"ok": False, "error": "too_many_attempts"}
    # Another client IP (behind nginx) is not affected.
    r = _login(client, headers={"X-Real-IP": "203.0.113.7"})
    assert r.status_code == 200
    admin_auth.reset_rate_limit()


def test_login_disabled_without_credentials(client, monkeypatch):
    monkeypatch.setattr(settings, "ADMIN_PASSWORD", "")
    r = _login(client, password="")
    assert r.status_code == 403 and r.json()["error"] == "login_disabled"
    monkeypatch.setattr(settings, "ADMIN_PASSWORD", ADMIN_PASSWORD)
    monkeypatch.setattr(settings, "ADMIN_LOGIN", "")
    assert _login(client).status_code == 403


def test_tampered_or_expired_cookie_rejected(client):
    good = admin_auth.make_session(ADMIN_LOGIN)
    assert admin_auth.verify_session(good)
    client.cookies.set("pb_admin", good[:-2] + ("00" if not good.endswith("00") else "11"))
    assert client.get("/api/admin/stats").status_code == 401
    old = admin_auth.make_session(ADMIN_LOGIN, now=1_000_000)
    assert not admin_auth.verify_session(old)
    client.cookies.set("pb_admin", old)
    assert client.get("/api/admin/stats").status_code == 401


def test_cookie_mutation_requires_csrf_header(logged_in):
    r = logged_in.post("/api/admin/keys", json={"plan_id": "month"})
    assert r.status_code == 403 and r.json() == {"ok": False, "error": "csrf"}
    r = logged_in.post("/api/admin/keys", json={"plan_id": "month"}, headers=CSRF)
    assert r.status_code == 200 and r.json()["key"].startswith("PB-")


def test_admin_token_header_still_works(client, admin_headers):
    assert client.get("/api/admin/stats").status_code == 401
    assert client.get("/api/admin/stats", headers=admin_headers).status_code == 200
    # Token auth does not need the CSRF header.
    r = client.post("/api/admin/keys", json={"plan_id": "lifetime"}, headers=admin_headers)
    assert r.status_code == 200
    r = client.get("/api/admin/licenses", headers=admin_headers)
    assert r.status_code == 200 and r.json()["total"] == 1


# ---------- stats ----------

def test_stats_math_and_test_exclusion(client, admin_headers, db_session):
    today_start = msk_day_start_utc(datetime.now(MSK).date())
    just_after_midnight = today_start + timedelta(minutes=1)
    just_before_midnight = today_start - timedelta(minutes=1)
    _add_order(db_session, status="paid", plan="month", created=just_after_midnight, income=95.04,
               pay_type="sbp")
    _add_order(db_session, status="paid", plan="lifetime", method="cryptobot", created=just_before_midnight,
               pay_type="USDT")
    _add_order(db_session, status="canceled", created=just_after_midnight, fail_reason="insufficient_funds")
    _add_order(db_session, status="pending", created=just_after_midnight)
    _add_order(db_session, status="paid", plan="3months", email="Test@ProxyBridge.org",
               created=just_after_midnight)
    _add_order(db_session, status="paid", plan="month", created=to_db(utcnow() - timedelta(days=40)))

    data = client.get("/api/admin/stats?exclude_test=1", headers=admin_headers).json()
    today = data["periods"]["today"]
    assert (today["orders"], today["paid"], today["canceled"], today["pending"]) == (3, 1, 1, 1)
    assert today["gross"] == 99 and today["net"] == 95.04 and today["net_known"] == 1
    assert today["conversion"] == 33.3
    week = data["periods"]["7d"]
    assert week["paid"] == 2 and week["gross"] == 99 + 699
    assert week["net"] == 95.04 + 699 and week["net_known"] == 1
    assert {r["id"]: r["count"] for r in week["by_plan"]} == {"month": 1, "lifetime": 1}
    assert {r["id"]: r["gross"] for r in week["by_method"]} == {"yookassa": 99, "cryptobot": 699}
    every = data["periods"]["all"]
    assert every["orders"] == 5 and every["paid"] == 3 and every["gross"] == 99 + 699 + 99
    assert data["periods"]["30d"]["paid"] == 2

    days = data["revenue_by_day"]
    assert len(days) == 30 and days[-1]["date"] == datetime.now(MSK).date().isoformat()
    assert days[-1]["paid_count"] == 1 and days[-1]["gross"] == 99
    assert days[-2]["gross"] == 699

    with_test = client.get("/api/admin/stats", headers=admin_headers).json()
    assert with_test["periods"]["today"]["paid"] == 2
    assert with_test["periods"]["today"]["gross"] == 99 + 249


def test_stats_licenses(client, admin_headers, issue_key, expire_key):
    k1 = issue_key("month")
    k2 = issue_key("lifetime", email="test@proxybridge.org")
    k3 = issue_key("month")
    expire_key(k3)
    client.post("/api/license/activate", json={"key": k1, "hwid": HWID_A, "app_version": "3.2"})
    client.post("/api/license/activate", json={"key": k2, "hwid": HWID_A, "app_version": "3.2"})
    lic = client.get("/api/admin/stats?exclude_test=1", headers=admin_headers).json()["licenses"]
    assert lic == {"total": 2, "active": 1, "with_devices": 1, "activations_24h": 1}
    lic = client.get("/api/admin/stats", headers=admin_headers).json()["licenses"]
    assert lic["active"] == 2 and lic["activations_24h"] == 2


# ---------- orders ----------

def test_orders_filters_and_search(client, admin_headers, db_session):
    o1 = _add_order(db_session, status="paid", email="alice@mail.ru", key="PB-AAAA-BBBB-CCCC-DDDD",
                    pay_type="bank_card")
    _add_order(db_session, status="canceled", method="cryptobot", email="bob@mail.ru", fail_reason="expired",
               pid="777")
    _add_order(db_session, status="canceled", email="carl@mail.ru", fail_reason="weird_code")
    _add_order(db_session, status="pending", email="test@proxybridge.org", plan="lifetime")
    _add_order(db_session, status="paid", email="old@mail.ru", created=to_db(datetime(2026, 1, 10, 22, 0)))

    def get(qs):
        r = client.get("/api/admin/orders?" + qs, headers=admin_headers)
        assert r.status_code == 200, r.text
        return r.json()

    data = get("")
    assert data["total"] == 5 and [i["id"] for i in data["items"]] == [5, 4, 3, 2, 1]
    assert "token" not in data["items"][0]
    assert get("status=paid")["total"] == 2
    assert get("method=cryptobot")["items"][0]["fail_reason_text"] == "Счёт истёк, оплата не поступила"
    assert get("plan=lifetime")["items"][0]["fail_reason_text"] == "Ожидает оплаты"
    assert get("q=weird")["total"] == 0
    assert get("q=carl")["items"][0]["fail_reason_text"] == "weird_code"
    assert get("q=bbbb-cccc")["items"][0]["id"] == o1.id
    assert get(f"q={o1.id}")["items"][0]["id"] == o1.id
    assert get("q=777")["total"] == 1
    alice = get("q=ALICE")["items"][0]
    assert alice["pay_type_text"] == "Банковская карта" and alice["status_text"] == "Оплачен"
    assert get("exclude_test=1")["total"] == 4
    assert get("plan=lifetime")["items"][0]["is_test"] is True
    # 2026-01-10 22:00 UTC is 2026-01-11 01:00 in Moscow.
    assert get("date_from=2026-01-11&date_to=2026-01-11")["total"] == 1
    assert get("date_from=2026-01-10&date_to=2026-01-10")["total"] == 0
    paged = get("per_page=2&page=2")
    assert paged["total"] == 5 and paged["pages"] == 3 and [i["id"] for i in paged["items"]] == [3, 2]
    assert client.get("/api/admin/orders?status=bogus", headers=admin_headers).status_code == 400
    assert client.get("/api/admin/orders?date_from=10.01.2026", headers=admin_headers).status_code == 400


def test_order_details_with_license_and_devices(client, admin_headers, mock_yookassa):
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "yookassa"})
    token = r.json()["order_token"]
    pid = mock_yookassa["created"][0]["id"]
    mock_yookassa["payments"][pid].update({"status": "succeeded", "paid": True, "income_amount": "95.04",
                                           "payment_method_type": "sbp"})
    key = client.post(f"/api/order/{token}/check").json()["key"]
    client.post("/api/license/activate", json={"key": key, "hwid": HWID_A, "app_version": "3.2.0"})
    client.post("/api/license/activate", json={"key": key, "hwid": HWID_B, "app_version": "3.2.0"})

    items = client.get("/api/admin/orders", headers=admin_headers).json()["items"]
    assert items[0]["device_count"] == 2 and items[0]["last_seen"]
    assert items[0]["income_rub"] == 95.04 and items[0]["pay_type_text"] == "СБП"
    d = client.get(f"/api/admin/orders/{items[0]['id']}", headers=admin_headers).json()
    assert d["license"]["key"] == key and len(d["license"]["devices"]) == 2
    assert {dev["hwid"] for dev in d["license"]["devices"]} == {HWID_A[:10], HWID_B[:10]}
    assert client.get("/api/admin/orders/9999", headers=admin_headers).status_code == 404


def test_admin_check_single_order(client, admin_headers, mock_yookassa):
    client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "yookassa"})
    pid = mock_yookassa["created"][0]["id"]
    mock_yookassa["payments"][pid].update({"status": "canceled", "cancellation_reason": "country_forbidden",
                                           "payment_method_type": "bank_card"})
    r = client.post("/api/admin/orders/1/check", headers=admin_headers)
    assert r.status_code == 200, r.text
    d = r.json()
    assert d["status"] == "canceled" and d["fail_reason"] == "country_forbidden"
    assert d["fail_reason_text"] == "Карта иностранного банка, оплата запрещена"
    assert d["checked_at"]
    mock_yookassa["payments"].clear()
    assert client.post("/api/admin/orders/1/check", headers=admin_headers).status_code == 502


def test_cryptobot_details_stored(client, admin_headers, mock_cryptopay):
    client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "cryptobot"})
    client.post("/api/checkout", json={"plan_id": "month", "email": "c@d.co", "method": "cryptobot"})
    ids = list(mock_cryptopay["invoices"])
    mock_cryptopay["invoices"][ids[0]].update({"status": "paid", "paid_asset": "USDT"})
    mock_cryptopay["invoices"][ids[1]]["status"] = "expired"
    from app import main
    with app_db.SessionLocal() as s:
        for o in s.query(Order).all():
            main.check_order_with_provider(s, o)
    items = {i["email"]: i for i in client.get("/api/admin/orders", headers=admin_headers).json()["items"]}
    assert items["a@b.co"]["status"] == "paid" and items["a@b.co"]["pay_type"] == "USDT"
    assert items["c@d.co"]["status"] == "canceled" and items["c@d.co"]["fail_reason"] == "expired"


# ---------- exports ----------

def test_export_csv_bom_and_russian_headers(client, admin_headers, db_session):
    _add_order(db_session, status="paid", email="alice@mail.ru", income=95.04, pay_type="sberbank")
    _add_order(db_session, status="canceled", email="test@proxybridge.org", fail_reason="general_decline")
    r = client.get("/api/admin/orders/export.csv", headers=admin_headers)
    assert r.status_code == 200
    assert r.headers["content-type"].startswith("text/csv")
    assert "attachment" in r.headers["content-disposition"]
    assert r.content.startswith(b"\xef\xbb\xbf")
    rows = list(csv.reader(io.StringIO(r.content.decode("utf-8-sig")), delimiter=";"))
    assert rows[0][:4] == ["№", "Создан (МСК)", "Статус", "Тариф"] and rows[0][-1] == "Тест"
    assert len(rows) == 3
    assert rows[1][2] == "Не оплачен" and rows[1][8] == "Банк отклонил без объяснения" and rows[1][-1] == "да"
    assert rows[2][2] == "Оплачен" and rows[2][3] == "1 месяц" and rows[2][7] == "SberPay"

    r = client.get("/api/admin/orders/export.csv?exclude_test=1&status=paid", headers=admin_headers)
    assert len(list(csv.reader(io.StringIO(r.content.decode("utf-8-sig")), delimiter=";"))) == 2


def test_export_xlsx_opens(client, admin_headers, db_session):
    openpyxl = pytest.importorskip("openpyxl")
    _add_order(db_session, status="paid", plan="lifetime", email="alice@mail.ru", income=670.5)
    r = client.get("/api/admin/orders/export.xlsx", headers=admin_headers)
    assert r.status_code == 200 and "spreadsheetml" in r.headers["content-type"]
    wb = openpyxl.load_workbook(io.BytesIO(r.content))
    ws = wb.active
    header = [c.value for c in ws[1]]
    assert header[0] == "№" and "К зачислению ₽" in header and header[-1] == "Тест"
    row = [c.value for c in ws[2]]
    assert row[2] == "Оплачен" and row[3] == "Навсегда" and row[4] == 699 and row[5] == 670.5


# ---------- licenses ----------

def test_license_actions(logged_in, issue_key, expire_key, db_session):
    client = logged_in
    key = issue_key("month", email="a@b.co")
    life = issue_key("lifetime")
    client.post("/api/license/activate", json={"key": key, "hwid": HWID_A, "app_version": "3.2"})

    # Mutations by cookie need the CSRF header.
    assert client.post(f"/api/admin/licenses/{key}/revoke").status_code == 403

    r = client.post(f"/api/admin/licenses/{key}/revoke", headers=CSRF)
    assert r.status_code == 200 and r.json()["license"]["state"] == "revoked"
    assert client.get("/api/admin/licenses?state=revoked").json()["total"] == 1
    r = client.post(f"/api/admin/licenses/{key}/restore", headers=CSRF)
    assert r.json()["license"]["state"] == "active"

    lst = client.get("/api/admin/licenses?q=a@b.co").json()
    assert lst["total"] == 1 and lst["items"][0]["devices"][0]["hwid"] == HWID_A[:10]
    r = client.post(f"/api/admin/licenses/{key}/reset-devices", headers=CSRF)
    assert r.json()["removed"] == 1 and r.json()["license"]["devices"] == []

    lic = db_session.query(License).filter_by(key=key).one()
    before = lic.expires_at
    r = client.post(f"/api/admin/licenses/{key}/extend", json={"days": 30}, headers=CSRF)
    assert r.status_code == 200
    db_session.refresh(lic)
    assert lic.expires_at - before == timedelta(days=30)

    # Already expired: counted from now.
    expire_key(key)
    assert client.get("/api/admin/licenses?state=expired").json()["total"] == 1
    client.post(f"/api/admin/licenses/{key}/extend", json={"days": 30}, headers=CSRF)
    db_session.refresh(lic)
    left = lic.expires_at - to_db(utcnow())
    assert timedelta(days=29, hours=23) < left <= timedelta(days=30)

    r = client.post(f"/api/admin/licenses/{life}/extend", json={"days": 30}, headers=CSRF)
    assert r.status_code == 400 and r.json() == {"ok": False, "error": "lifetime"}
    assert client.post("/api/admin/licenses/PB-AAAA-BBBB-CCCC-DDDD/revoke", headers=CSRF).status_code == 404
    assert client.get("/api/admin/licenses?state=active").json()["total"] == 2


# ---------- backfill ----------

def test_refresh_backfill_details_without_changing_paid(client, admin_headers, db_session, mock_yookassa,
                                                        mock_cryptopay):
    paid = _add_order(db_session, status="paid", pid="yk-paid", key="PB-AAAA-BBBB-CCCC-DDDD", checked=False)
    canceled = _add_order(db_session, status="canceled", pid="yk-canc", checked=False)
    crypto = _add_order(db_session, status="canceled", method="cryptobot", pid="5001", checked=False)
    done = _add_order(db_session, status="canceled", pid="yk-done", checked=True)
    mock_yookassa["payments"]["yk-paid"] = {"id": "yk-paid", "status": "canceled",  # never downgrades
                                            "amount": {"value": "99.00", "currency": "RUB"},
                                            "payment_method_type": "bank_card", "income_amount": "95.04",
                                            "cancellation_reason": None}
    mock_yookassa["payments"]["yk-canc"] = {"id": "yk-canc", "status": "canceled", "amount": {},
                                            "cancellation_reason": "expired_on_confirmation",
                                            "payment_method_type": None, "income_amount": None}
    mock_cryptopay["invoices"]["5001"] = {"invoice_id": "5001", "status": "expired", "paid_asset": None}

    r = client.post("/api/admin/orders/refresh", headers=admin_headers)
    assert r.status_code == 200, r.text
    counts = r.json()
    assert counts["total"] == 3 and counts["checked"] == 3 and counts["failed"] == 0
    assert counts["became_paid"] == 0 and counts["remaining"] == 0

    for o in (paid, canceled, crypto, done):
        db_session.refresh(o)
    assert paid.status == "paid" and paid.pay_type == "bank_card" and paid.income_rub == 95.04
    assert paid.fail_reason is None and paid.checked_at is not None
    assert canceled.status == "canceled" and canceled.fail_reason == "expired_on_confirmation"
    assert crypto.fail_reason == "expired"
    assert done.fail_reason is None  # already checked, skipped
    assert db_session.query(License).count() == 0  # no keys issued by a backfill of paid orders

    # Second run has nothing to do.
    assert client.post("/api/admin/orders/refresh", headers=admin_headers).json()["total"] == 0


# ---------- migration ----------

OLD_ORDERS_SQL = """
CREATE TABLE orders (
    id INTEGER NOT NULL PRIMARY KEY,
    token VARCHAR(64) NOT NULL,
    plan_id VARCHAR(32) NOT NULL,
    email VARCHAR(254) NOT NULL,
    method VARCHAR(16) NOT NULL,
    status VARCHAR(16) NOT NULL,
    amount_rub INTEGER NOT NULL,
    provider_payment_id VARCHAR(128),
    key VARCHAR(32),
    created_at DATETIME NOT NULL,
    paid_at DATETIME
)
"""


def test_migration_adds_columns_to_old_db(tmp_path):
    path = tmp_path / "old.db"
    con = sqlite3.connect(path)
    con.execute(OLD_ORDERS_SQL)
    con.execute("CREATE UNIQUE INDEX ix_orders_token ON orders (token)")
    con.execute("INSERT INTO orders (id, token, plan_id, email, method, status, amount_rub, "
                "provider_payment_id, key, created_at, paid_at) VALUES (7, 'tok', 'month', 'a@b.co', "
                "'yookassa', 'paid', 99, 'yk-1', 'PB-AAAA-BBBB-CCCC-DDDD', '2026-05-01 10:00:00.000000', "
                "'2026-05-01 10:05:00.000000')")
    con.commit()
    con.close()

    app_db.configure("sqlite:///" + path.as_posix())
    try:
        app_db.init_db()
        app_db.init_db()  # idempotent
        con = sqlite3.connect(path)
        cols = {row[1] for row in con.execute("PRAGMA table_info(orders)")}
        con.close()
        assert {"fail_reason", "pay_type", "income_rub", "checked_at"} <= cols
        with app_db.SessionLocal() as s:
            order = s.get(Order, 7)
            assert order.status == "paid" and order.key == "PB-AAAA-BBBB-CCCC-DDDD"
            assert order.fail_reason is None and order.checked_at is None
            order.income_rub = 95.04
            s.commit()
        assert app_db.migrate_sqlite(app_db.engine) == []
    finally:
        app_db.engine.dispose()


# ---------- UI ----------

def test_admin_ui_served_with_noindex(client):
    for path in ("/admin", "/admin/"):
        r = client.get(path)
        assert r.status_code == 200
        assert r.headers["content-type"].startswith("text/html")
        assert r.headers["x-robots-tag"] == "noindex, nofollow"
        assert r.headers["cache-control"] == "no-store"
        assert "<html" in r.text.lower()
    assert client.get("/admin/assets/../../config.py").status_code == 404
    assert client.get("/api/health").json() == {"ok": True}
