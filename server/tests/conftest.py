import sys
from datetime import timedelta
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

SERVER_DIR = Path(__file__).resolve().parent.parent
if str(SERVER_DIR) not in sys.path:
    sys.path.insert(0, str(SERVER_DIR))

from app import db as app_db  # noqa: E402
from app.config import settings  # noqa: E402
from app.main import create_app  # noqa: E402
from app.models import License, to_db, utcnow  # noqa: E402

ADMIN_TOKEN = "test-admin-token"
HWID_A = "a" * 64
HWID_B = "b" * 64
HWID_C = "c" * 64


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "ADMIN_TOKEN", ADMIN_TOKEN)
    monkeypatch.setattr(settings, "MAX_DEVICES", 2)
    monkeypatch.setattr(settings, "SMTP_HOST", "")
    monkeypatch.setattr(settings, "YOOKASSA_SHOP_ID", "shop")
    monkeypatch.setattr(settings, "YOOKASSA_SECRET_KEY", "secret")
    monkeypatch.setattr(settings, "CRYPTOPAY_API_TOKEN", "token")
    app_db.configure("sqlite:///" + (tmp_path / "test.db").as_posix())
    app = create_app()
    with TestClient(app) as c:
        yield c


@pytest.fixture
def db_session(client):
    session = app_db.SessionLocal()
    try:
        yield session
    finally:
        session.close()


@pytest.fixture
def admin_headers():
    return {"X-Admin-Token": ADMIN_TOKEN}


@pytest.fixture
def issue_key(client, admin_headers):
    """Issue a license through the admin API; returns the key string."""

    def _issue(plan_id="month", email=None):
        payload = {"plan_id": plan_id}
        if email:
            payload["email"] = email
        r = client.post("/api/admin/keys", json=payload, headers=admin_headers)
        assert r.status_code == 200, r.text
        return r.json()["key"]

    return _issue


@pytest.fixture
def expire_key(db_session):
    """Move a license's expires_at into the past."""

    def _expire(key):
        lic = db_session.query(License).filter_by(key=key).one()
        lic.expires_at = to_db(utcnow() - timedelta(minutes=1))
        db_session.commit()

    return _expire


@pytest.fixture
def mock_yookassa(monkeypatch):
    """Patch provider calls; returns a dict that controls get_payment responses."""
    state = {"created": [], "payments": {}}

    def fake_create_payment(amount_rub, description, order_token, return_url):
        pid = f"yk-{len(state['created']) + 1}"
        state["created"].append({"id": pid, "amount": amount_rub, "token": order_token, "return_url": return_url})
        state["payments"][pid] = {
            "id": pid, "status": "pending", "paid": False,
            "amount": {"value": f"{amount_rub:.2f}", "currency": "RUB"},
            "metadata": {"order_token": order_token},
        }
        return {"id": pid, "confirmation_url": f"https://yookassa.test/pay/{pid}", "status": "pending"}

    def fake_get_payment(payment_id):
        from app.providers.yookassa import YooKassaError
        if payment_id not in state["payments"]:
            raise YooKassaError("not found")
        return dict(state["payments"][payment_id])

    monkeypatch.setattr("app.providers.yookassa.create_payment", fake_create_payment)
    monkeypatch.setattr("app.providers.yookassa.get_payment", fake_get_payment)
    return state


@pytest.fixture
def mock_cryptopay(monkeypatch):
    state = {"invoices": {}}

    def fake_create_invoice(amount_rub, description, payload, expires_in=3600, paid_btn_url=None):
        iid = str(1000 + len(state["invoices"]))
        state["invoices"][iid] = {"invoice_id": iid, "status": "active", "amount": str(amount_rub), "payload": payload}
        return {"invoice_id": iid, "pay_url": f"https://t.me/CryptoBot?start=inv{iid}", "status": "active"}

    def fake_get_invoice(invoice_id):
        return state["invoices"].get(str(invoice_id))

    monkeypatch.setattr("app.providers.cryptopay.create_invoice", fake_create_invoice)
    monkeypatch.setattr("app.providers.cryptopay.get_invoice", fake_get_invoice)
    return state
