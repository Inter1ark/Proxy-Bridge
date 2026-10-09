import json
from datetime import timedelta

import pytest

from app.models import StoreOrder, StoreProxy, to_db, utcnow
from app.store import service

CSRF = {"X-Requested-With": "pb-admin"}
KEY_A = "PB-AAAA-BBBB-CCCC-DDDD"
KEY_B = "PB-EEEE-FFFF-GGGG-HHHH"


@pytest.fixture(autouse=True)
def no_worker(monkeypatch):
    """The store worker thread must not touch the rows these tests create."""
    monkeypatch.setattr(service, "tick", lambda db: None)


def _add_order(db, *, key=KEY_A, product="traffic", status="paid", fulfill="done", amount=500, cost=1.5,
               income=None, refunded=False, vendor_order_id=None, error=None, proxy_id=None, method="yookassa"):
    n = db.query(StoreOrder).count() + 1
    params = {"country": "DE", "type": "residential", "rotation": "static", "gb": 2} if product == "traffic" else \
        {"country": "DE"} if product == "dc" else {"proxy_id": proxy_id or 1, "gb": 1}
    now = to_db(utcnow())
    order = StoreOrder(token=f"stok{n:028d}", license_key=key, product=product, params=json.dumps(params),
                       amount_rub=amount, cost_usd=cost, method=method, status=status,
                       provider_payment_id=f"yk-{n}", income_rub=income, created_at=now,
                       paid_at=now if status == "paid" else None, fulfill=fulfill if status == "paid" else "",
                       fulfill_error=error, vendor_order_id=vendor_order_id, proxy_id=proxy_id, refunded=refunded)
    db.add(order)
    db.commit()
    return order


def _add_proxy(db, *, key=KEY_A, kind="traffic", status="active", used=1_234_567_890):
    p = StoreProxy(license_key=key, kind=kind, vendor="sx" if kind == "traffic" else "cy", account="fp1234",
                   vendor_id="777", status=status, host="203.0.113.5", port=10777, login="secretlogin",
                   password="secretpass", country_code="DE", state="Bavaria", city="Munich",
                   ptype="residential" if kind == "traffic" else "datacenter", rotation="static",
                   gb_total=5 if kind == "traffic" else 0, bytes_used=used,
                   expires_at=to_db(utcnow() + timedelta(days=30)) if kind == "dc" else None)
    db.add(p)
    db.commit()
    return p


def test_store_admin_requires_auth(client):
    for path in ("/api/admin/store/orders", "/api/admin/store/proxies", "/api/admin/store/summary"):
        assert client.get(path).status_code == 401
    assert client.post("/api/admin/store/orders/1/retry").status_code == 401
    assert client.post("/api/admin/store/orders/1/mark-refunded").status_code == 401


def test_store_admin_cookie_post_requires_csrf(client, db_session):
    from tests.conftest import ADMIN_LOGIN, ADMIN_PASSWORD
    o = _add_order(db_session, fulfill="failed", error="create: boom")
    assert client.post("/api/admin/login", json={"login": ADMIN_LOGIN, "password": ADMIN_PASSWORD}).status_code == 200
    assert client.post(f"/api/admin/store/orders/{o.id}/retry").status_code == 403
    assert client.post(f"/api/admin/store/orders/{o.id}/retry", headers=CSRF).status_code == 200


def test_store_orders_listing_and_filters(client, admin_headers, db_session):
    done = _add_order(db_session, income=480.5)
    failed = _add_order(db_session, key=KEY_B, product="dc", fulfill="failed", error="sold out: DE")
    pending = _add_order(db_session, status="pending")
    manual = _add_order(db_session, product="topup", fulfill="manual", error="internal error")

    r = client.get("/api/admin/store/orders", headers=admin_headers)
    assert r.status_code == 200
    data = r.json()
    assert data["total"] == 4
    assert [i["id"] for i in data["items"]] == [manual.id, pending.id, failed.id, done.id]
    item = data["items"][-1]
    assert item["sid"] == f"S{done.id}" and item["license_key"] == KEY_A
    assert item["title"] == service._product_title(done)
    assert item["income_rub"] == 480.5 and item["cost_usd"] == 1.5 and item["attention"] is False
    assert "token" not in item

    by_fulfill = client.get("/api/admin/store/orders?fulfill=failed", headers=admin_headers).json()
    assert [i["id"] for i in by_fulfill["items"]] == [failed.id]
    attention = client.get("/api/admin/store/orders?fulfill=attention", headers=admin_headers).json()
    assert {i["id"] for i in attention["items"]} == {failed.id, manual.id}
    assert all(i["attention"] for i in attention["items"])
    several = client.get("/api/admin/store/orders?fulfill=done,manual", headers=admin_headers).json()
    assert {i["id"] for i in several["items"]} == {done.id, manual.id}
    by_status = client.get("/api/admin/store/orders?status=pending", headers=admin_headers).json()
    assert [i["id"] for i in by_status["items"]] == [pending.id]

    by_key = client.get("/api/admin/store/orders", params={"q": "eeee-ffff"}, headers=admin_headers).json()
    assert [i["id"] for i in by_key["items"]] == [failed.id]
    by_sid = client.get("/api/admin/store/orders", params={"q": f"S{done.id}"}, headers=admin_headers).json()
    assert [i["id"] for i in by_sid["items"]] == [done.id]
    by_token = client.get("/api/admin/store/orders", params={"q": pending.token}, headers=admin_headers).json()
    assert [i["id"] for i in by_token["items"]] == [pending.id]
    limited = client.get("/api/admin/store/orders?limit=2", headers=admin_headers).json()
    assert len(limited["items"]) == 2 and limited["total"] == 4

    assert client.get("/api/admin/store/orders?fulfill=nope", headers=admin_headers).status_code == 400
    assert client.get("/api/admin/store/orders?status=nope", headers=admin_headers).status_code == 400


def test_store_proxies_listing_hides_credentials(client, admin_headers, db_session):
    traffic = _add_proxy(db_session)
    dc = _add_proxy(db_session, key=KEY_B, kind="dc", used=0)
    _add_proxy(db_session, status="failed")

    r = client.get("/api/admin/store/proxies", headers=admin_headers)
    assert r.status_code == 200
    assert "secretpass" not in r.text and "secretlogin" not in r.text
    data = r.json()
    assert data["total"] == 3
    item = next(i for i in data["items"] if i["id"] == traffic.id)
    assert "password" not in item and "login" not in item
    assert item["gb_used"] == 1.235 and item["gb_total"] == 5
    assert item["address"] == "203.0.113.5:10777" and item["vendor"] == "sx"

    active = client.get("/api/admin/store/proxies?status=active", headers=admin_headers).json()
    assert {i["id"] for i in active["items"]} == {traffic.id, dc.id}
    by_key = client.get("/api/admin/store/proxies", params={"q": KEY_B}, headers=admin_headers).json()
    assert [i["id"] for i in by_key["items"]] == [dc.id]
    assert client.get("/api/admin/store/proxies?status=nope", headers=admin_headers).status_code == 400


def test_store_summary(client, admin_headers, db_session):
    _add_order(db_session, amount=500, cost=1.5, income=480.0)
    _add_order(db_session, amount=400, cost=1.75, fulfill="done")
    _add_order(db_session, amount=300, cost=2.0, fulfill="failed", refunded=True)
    _add_order(db_session, amount=200, cost=0.5, fulfill="manual")
    _add_order(db_session, amount=999, status="pending")
    _add_proxy(db_session)
    _add_proxy(db_session, status="exhausted")

    s = client.get("/api/admin/store/summary", headers=admin_headers).json()
    assert s["revenue_rub"] == 1400 and s["paid_count"] == 4
    assert s["income_rub"] == 480.0 and s["income_known"] == 1
    assert s["cost_usd"] == 3.25
    assert s["by_fulfill"] == {"queued": 0, "working": 0, "done": 2, "failed": 1, "manual": 1}
    assert s["attention"] == 1
    assert s["refunded_rub"] == 300 and s["refunded_count"] == 1
    assert s["active_proxies"] == 1


def test_store_retry_rules(client, admin_headers, db_session):
    failed = _add_order(db_session, fulfill="failed", error="vendor balance too low")
    manual = _add_order(db_session, product="dc", fulfill="manual", error="internal error")
    bought = _add_order(db_session, product="dc", fulfill="manual", vendor_order_id="cy-1")
    refunded = _add_order(db_session, fulfill="failed", refunded=True)
    done = _add_order(db_session)
    working = _add_order(db_session, fulfill="working")

    r = client.post(f"/api/admin/store/orders/{failed.id}/retry", headers=admin_headers)
    assert r.status_code == 200, r.text
    assert r.json()["order"]["fulfill"] == "queued" and r.json()["order"]["fulfill_error"] is None
    db_session.expire_all()
    assert db_session.get(StoreOrder, failed.id).fulfill == "queued"
    # Second click: already queued.
    assert client.post(f"/api/admin/store/orders/{failed.id}/retry", headers=admin_headers).status_code == 409

    # "manual" may already be bought at the vendor: never retried from the panel.
    for o in (manual, bought, refunded, done, working):
        r = client.post(f"/api/admin/store/orders/{o.id}/retry", headers=admin_headers)
        assert r.status_code == 409 and r.json()["error"] == "cannot_retry"
        db_session.expire_all()
        assert db_session.get(StoreOrder, o.id).fulfill == o.fulfill
    assert client.post("/api/admin/store/orders/9999/retry", headers=admin_headers).status_code == 404


def test_store_mark_refunded(client, admin_headers, db_session):
    manual = _add_order(db_session, product="dc", fulfill="manual", vendor_order_id="cy-1")
    pending = _add_order(db_session, status="pending")

    r = client.post(f"/api/admin/store/orders/{manual.id}/mark-refunded", headers=admin_headers)
    assert r.status_code == 200
    order = r.json()["order"]
    assert order["refunded"] is True and order["attention"] is False and order["can_retry"] is False
    db_session.expire_all()
    assert db_session.get(StoreOrder, manual.id).refunded is True
    assert client.get("/api/admin/store/summary", headers=admin_headers).json()["attention"] == 0
    # Idempotent.
    assert client.post(f"/api/admin/store/orders/{manual.id}/mark-refunded", headers=admin_headers).status_code == 200

    r = client.post(f"/api/admin/store/orders/{pending.id}/mark-refunded", headers=admin_headers)
    assert r.status_code == 409 and r.json()["error"] == "not_paid"
    assert client.post("/api/admin/store/orders/9999/mark-refunded", headers=admin_headers).status_code == 404
