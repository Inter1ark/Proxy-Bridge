from app.keys import normalize_key
from app.models import License


def test_plans_list(client):
    r = client.get("/api/plans")
    assert r.status_code == 200
    plans = r.json()
    assert [p["id"] for p in plans] == ["month", "3months", "lifetime"]
    assert plans[0] == {"id": "month", "title": "1 месяц", "price_rub": 99, "days": 30}
    assert plans[1]["price_rub"] == 249 and plans[1]["days"] == 90
    assert plans[2]["price_rub"] == 699 and plans[2]["days"] is None


def test_checkout_yookassa_creates_order(client, mock_yookassa):
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "User@Example.com", "method": "yookassa"})
    assert r.status_code == 200, r.text
    data = r.json()
    assert len(data["order_token"]) == 32
    assert data["pay_url"].startswith("https://yookassa.test/pay/")
    created = mock_yookassa["created"][0]
    assert created["amount"] == 99
    assert created["token"] == data["order_token"]
    assert created["return_url"].endswith("/success.html?token=" + data["order_token"])

    r = client.get(f"/api/order/{data['order_token']}")
    assert r.status_code == 200
    assert r.json() == {"status": "pending", "plan_id": "month", "email": "user@example.com",
                        "key": None, "method": "yookassa"}


def test_checkout_cryptobot_creates_order(client, mock_cryptopay):
    r = client.post("/api/checkout", json={"plan_id": "lifetime", "email": "a@b.co", "method": "cryptobot"})
    assert r.status_code == 200, r.text
    assert r.json()["pay_url"].startswith("https://t.me/CryptoBot")


def test_checkout_validation_errors(client, mock_yookassa):
    r = client.post("/api/checkout", json={"plan_id": "year", "email": "a@b.co", "method": "yookassa"})
    assert r.status_code == 400 and r.json() == {"ok": False, "error": "bad_plan"}
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "not-an-email", "method": "yookassa"})
    assert r.status_code == 400 and r.json() == {"ok": False, "error": "bad_email"}
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "paypal"})
    assert r.status_code == 400 and r.json() == {"ok": False, "error": "bad_method"}


def test_checkout_provider_error(client, monkeypatch):
    from app.providers.yookassa import YooKassaError

    def boom(*args, **kwargs):
        raise YooKassaError("down")

    monkeypatch.setattr("app.providers.yookassa.create_payment", boom)
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "yookassa"})
    assert r.status_code == 400 and r.json() == {"ok": False, "error": "provider_error"}


def test_order_not_found(client):
    r = client.get("/api/order/doesnotexist")
    assert r.status_code == 404 and r.json() == {"ok": False, "error": "not_found"}


def test_cryptobot_check_marks_paid(client, mock_cryptopay, db_session):
    r = client.post("/api/checkout", json={"plan_id": "3months", "email": "a@b.co", "method": "cryptobot"})
    token = r.json()["order_token"]

    r = client.post(f"/api/order/{token}/check")
    assert r.json()["status"] == "pending" and r.json()["key"] is None

    invoice_id = next(iter(mock_cryptopay["invoices"]))
    mock_cryptopay["invoices"][invoice_id]["status"] = "paid"
    r = client.post(f"/api/order/{token}/check")
    data = r.json()
    assert data["status"] == "paid"
    assert normalize_key(data["key"]) == data["key"]

    # A second check does not issue another key.
    r = client.post(f"/api/order/{token}/check")
    assert r.json()["key"] == data["key"]
    assert db_session.query(License).count() == 1
    assert client.get(f"/api/order/{token}").json()["key"] == data["key"]


def test_cryptobot_check_expired_invoice_cancels(client, mock_cryptopay):
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "cryptobot"})
    token = r.json()["order_token"]
    invoice_id = next(iter(mock_cryptopay["invoices"]))
    mock_cryptopay["invoices"][invoice_id]["status"] = "expired"
    r = client.post(f"/api/order/{token}/check")
    assert r.json()["status"] == "canceled" and r.json()["key"] is None
