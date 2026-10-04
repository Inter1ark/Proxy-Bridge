from app.models import License


def _checkout(client):
    r = client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "yookassa"})
    assert r.status_code == 200
    return r.json()["order_token"]


def _webhook_body(payment_id, **extra):
    body = {"type": "notification", "event": "payment.succeeded",
            "object": {"id": payment_id, "status": "succeeded",
                       "amount": {"value": "99.00", "currency": "RUB"}}}
    body["object"].update(extra)
    return body


def test_webhook_marks_paid_and_issues_key_once(client, mock_yookassa, db_session):
    token = _checkout(client)
    pid = mock_yookassa["created"][0]["id"]
    mock_yookassa["payments"][pid].update({"status": "succeeded", "paid": True})

    r = client.post("/webhook/yookassa", json=_webhook_body(pid))
    assert r.status_code == 200
    order = client.get(f"/api/order/{token}").json()
    assert order["status"] == "paid"
    key = order["key"]
    assert key and key.startswith("PB-") and len(key) == 22

    # Duplicate notification: same key, no second license.
    r = client.post("/webhook/yookassa", json=_webhook_body(pid))
    assert r.status_code == 200
    assert client.get(f"/api/order/{token}").json()["key"] == key
    assert db_session.query(License).count() == 1
    lic = db_session.query(License).one()
    assert lic.plan_id == "month" and lic.email == "a@b.co" and lic.expires_at is not None


def test_webhook_does_not_trust_body(client, mock_yookassa):
    token = _checkout(client)
    pid = mock_yookassa["created"][0]["id"]
    # API still says pending even though the body claims succeeded.
    r = client.post("/webhook/yookassa", json=_webhook_body(pid))
    assert r.status_code == 200
    assert client.get(f"/api/order/{token}").json()["status"] == "pending"


def test_webhook_amount_mismatch_is_ignored(client, mock_yookassa):
    token = _checkout(client)
    pid = mock_yookassa["created"][0]["id"]
    mock_yookassa["payments"][pid].update({"status": "succeeded", "paid": True,
                                           "amount": {"value": "1.00", "currency": "RUB"}})
    client.post("/webhook/yookassa", json=_webhook_body(pid))
    assert client.get(f"/api/order/{token}").json()["status"] == "pending"


def test_webhook_unknown_payment_and_other_events(client, mock_yookassa):
    r = client.post("/webhook/yookassa", json={"event": "payment.canceled", "object": {"id": "x"}})
    assert r.status_code == 200
    r = client.post("/webhook/yookassa", json=_webhook_body("unknown-id"))
    assert r.status_code == 502
    r = client.post("/webhook/yookassa", content=b"not json", headers={"Content-Type": "application/json"})
    assert r.status_code == 400


def test_yookassa_check_endpoint(client, mock_yookassa):
    token = _checkout(client)
    pid = mock_yookassa["created"][0]["id"]
    assert client.post(f"/api/order/{token}/check").json()["status"] == "pending"
    mock_yookassa["payments"][pid].update({"status": "succeeded", "paid": True})
    data = client.post(f"/api/order/{token}/check").json()
    assert data["status"] == "paid" and data["key"]
