from app.config import settings
from app.keys import normalize_key


def test_admin_issue_key_with_token(client, admin_headers):
    r = client.post("/api/admin/keys", json={"plan_id": "lifetime", "email": "x@y.co"}, headers=admin_headers)
    assert r.status_code == 200, r.text
    key = r.json()["key"]
    assert normalize_key(key) == key

    r = client.post("/api/admin/keys", json={"plan_id": "nope"}, headers=admin_headers)
    assert r.status_code == 400 and r.json() == {"ok": False, "error": "bad_plan"}


def test_admin_requires_token(client):
    r = client.post("/api/admin/keys", json={"plan_id": "lifetime"})
    assert r.status_code == 401 and r.json() == {"ok": False, "error": "unauthorized"}
    r = client.post("/api/admin/keys", json={"plan_id": "lifetime"}, headers={"X-Admin-Token": "wrong"})
    assert r.status_code == 401
    r = client.get("/api/admin/orders")
    assert r.status_code == 401
    r = client.post("/api/admin/keys/PB-AAAA-BBBB-CCCC-DDDD/revoke")
    assert r.status_code == 401


def test_admin_disabled_when_token_unset(client, monkeypatch, admin_headers):
    monkeypatch.setattr(settings, "ADMIN_TOKEN", "")
    r = client.post("/api/admin/keys", json={"plan_id": "lifetime"}, headers=admin_headers)
    assert r.status_code == 401
    r = client.post("/api/admin/keys", json={"plan_id": "lifetime"}, headers={"X-Admin-Token": ""})
    assert r.status_code == 401


def test_admin_orders_list(client, admin_headers, mock_yookassa):
    for _ in range(3):
        client.post("/api/checkout", json={"plan_id": "month", "email": "a@b.co", "method": "yookassa"})
    r = client.get("/api/admin/orders?limit=2", headers=admin_headers)
    assert r.status_code == 200
    orders = r.json()
    assert len(orders) == 2
    assert {"token", "status", "plan_id", "email", "method", "amount_rub", "created_at"} <= set(orders[0])
    assert orders[0]["amount_rub"] == 99


def test_admin_revoke_unknown_key(client, admin_headers):
    r = client.post("/api/admin/keys/PB-AAAA-BBBB-CCCC-DDDD/revoke", headers=admin_headers)
    assert r.status_code == 404 and r.json() == {"ok": False, "error": "not_found"}
