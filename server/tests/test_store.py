from datetime import timedelta

import pytest

from app.config import settings
from app.models import StoreOrder, StoreProxy, from_db, to_db, utcnow
from app.store import cy, pricing, service, sx

from .conftest import HWID_A, HWID_B

REAL_TICK = service.tick


@pytest.fixture
def store(client, monkeypatch, mock_yookassa, issue_key):
    """Fake vendors and a license activated on HWID_A. The background worker is disabled."""
    monkeypatch.setattr(service, "tick", lambda db: None)
    monkeypatch.setattr(settings, "SX_API_KEYS", "sxkey1,sxkey2")
    monkeypatch.setattr(settings, "SX_API_KEYS_NEW", "")
    monkeypatch.setattr(settings, "STORE_SX_COST_LEGACY", "mobile:6,residential:5.5,datacenter:0.9")
    monkeypatch.setattr(settings, "STORE_SX_COST_NEW", "mobile:3,residential:3,datacenter:3")
    sx._balances.clear()
    monkeypatch.setattr(settings, "CY_API_KEYS", "cykey1")
    monkeypatch.setattr(settings, "STORE_SX_USD_PER_GB", "mobile:6,residential:5.5,datacenter:0.9")
    monkeypatch.setattr(settings, "STORE_FEE_PCT", 4.5)
    monkeypatch.setattr(settings, "STORE_MARKUP_PCT", 0)
    monkeypatch.setattr(settings, "STORE_DC_PRICE_RUB", 400)
    monkeypatch.setattr(pricing, "usd_rub", lambda: 80.0)
    monkeypatch.setattr(service.notify, "send_admin", lambda text: state["notes"].append(text))

    state = {
        "sx_balance": {"sxkey1": 50.0, "sxkey2": 5.0, "newkey1": 40.0, "newkey2": 0.0}, "cy_balance": {"cykey1": 20.0},
        "created": [], "updated": [], "archived": [], "unarchived": [], "deleted": [], "traffic": {},
        "bought": [], "cy_orders": {}, "auto_renew": [], "refunds": [], "notes": [], "probe": True,
        "buy_error": None, "create_error": None,
    }

    monkeypatch.setattr(sx, "balance", lambda acc, max_age=60: state["sx_balance"][acc.key])
    monkeypatch.setattr(sx, "countries", lambda: [{"id": 2921044, "code": "DE", "name": "Germany"},
                                                  {"id": 6252001, "code": "US", "name": "United States"}])
    monkeypatch.setattr(sx, "states", lambda cid: [{"id": 11, "name": "Bavaria"}])
    monkeypatch.setattr(sx, "cities", lambda cid, sid: [{"id": 12, "name": "Munich"}])
    monkeypatch.setattr(sx, "available", lambda code, ptype: not (code == "US" and ptype == "mobile"))

    def create_port(acc, country_code, state_name, city, ptype, rotation, ttl, gb):
        if state["create_error"]:
            raise sx.SxError(state["create_error"], 400)
        pid = 1000 + len(state["created"])
        state["created"].append({"acc": acc.key, "country": country_code, "state": state_name, "city": city,
                                 "type": ptype, "rotation": rotation, "ttl": ttl, "gb": gb})
        return {"id": pid, "server": "203.0.113.5", "port": 10000 + pid, "login": "lg", "password": "pw",
                "state_id": 11 if state_name else None, "city_id": 12 if city else None,
                "name": "vendor default name", "refresh_link": "https://vendor/refresh?apiKey=SECRET"}

    monkeypatch.setattr(sx, "create_port", create_port)
    monkeypatch.setattr(sx, "update_port", lambda acc, pid, *a: state["updated"].append((pid, a[-1])) or {})
    monkeypatch.setattr(sx, "archive", lambda acc, pid: state["archived"].append(pid))
    monkeypatch.setattr(sx, "unarchive", lambda acc, pid: state["unarchived"].append(pid))
    monkeypatch.setattr(sx, "delete", lambda acc, pid: state["deleted"].append(pid))
    monkeypatch.setattr(sx, "port_traffic", lambda acc, pid: state["traffic"].get(pid, 0))
    monkeypatch.setattr(service, "probe", lambda p: state["probe"])

    product = {"id": "prod-de", "location_country_code": "DE", "days": 30, "price_usd": "1.75",
               "discounted_price": None, "stock_status": "in_stock"}
    monkeypatch.setattr(cy, "balance", lambda acc: state["cy_balance"][acc.key])
    monkeypatch.setattr(cy, "dc_countries", lambda days: ["BG", "DE"])
    monkeypatch.setattr(cy, "dc_product", lambda code, days: dict(product) if code in ("BG", "DE") else None)

    def buy(acc, product_id):
        if state["buy_error"]:
            raise state["buy_error"]
        oid = f"cy-{len(state['bought']) + 1}"
        state["bought"].append(product_id)
        state["cy_orders"][oid] = {"id": oid, "order_status": "server_purchase_in_progress"}
        return oid

    monkeypatch.setattr(cy, "buy", buy)
    monkeypatch.setattr(cy, "find_order", lambda acc, oid, max_pages=10: state["cy_orders"].get(oid))
    monkeypatch.setattr(cy, "set_auto_renew", lambda acc, oid, on: state["auto_renew"].append((oid, on)))
    monkeypatch.setattr("app.providers.yookassa.create_refund",
                        lambda pid, amount, key: state["refunds"].append((pid, amount, key)) or {"status": "succeeded"})

    key = issue_key("month")
    r = client.post("/api/license/activate", json={"key": key, "hwid": HWID_A})
    assert r.status_code == 200, r.text
    state["auth"] = {"key": key, "hwid": HWID_A}
    state["yk"] = mock_yookassa
    return state


def post(client, path, state, **body):
    return client.post("/api/store" + path, json={**state["auth"], **body})


def pay(state, token):
    for created in state["yk"]["created"]:
        if created["token"] == token:
            state["yk"]["payments"][created["id"]]["status"] = "succeeded"


def run(db_session, times=1):
    service._last["sync"] = 0
    for _ in range(times):
        db_session.expire_all()
        REAL_TICK(db_session)


def traffic_params(**kw):
    p = {"country": "DE", "type": "residential", "rotation": "static", "gb": 2}
    p.update(kw)
    return p


def test_store_requires_activated_device(client, store, issue_key):
    r = client.post("/api/store/catalog", json={"key": store["auth"]["key"], "hwid": HWID_B})
    assert r.status_code == 403 and r.json()["error"] == "not_activated"
    r = client.post("/api/store/catalog", json={"key": "PB-AAAA-BBBB-CCCC-DDDD", "hwid": HWID_A})
    assert r.status_code == 404


def test_catalog_has_prices_and_names_without_vendor_names(client, store):
    r = post(client, "/catalog", store)
    data = r.json()
    assert data["dc"]["price_rub"] == 400 and data["dc"]["days"] == 30
    assert {"code": "DE", "ru": "Германия", "en": "Germany"} in data["dc"]["countries"]
    prices = data["traffic"]["gb_price_rub"]
    assert prices["datacenter"] == round(0.9 * 80 / 0.955, 2)
    assert prices["mobile"] == round(6 * 80 / 0.955, 2)
    text = r.text.lower()
    assert "sx" not in text.replace("sx.", "") and "cyber" not in text


def test_traffic_order_full_flow(client, store, db_session):
    r = post(client, "/quote", store, product="traffic", params=traffic_params(state_id=11, city_id=12))
    assert r.json()["amount_rub"] == 922  # ceil(2 * 5.5 * 80 / 0.955)

    r = post(client, "/order", store, product="traffic", method="yookassa",
             params=traffic_params(state_id=11, city_id=12, rotation="interval", ttl=10))
    assert r.status_code == 200, r.text
    token = r.json()["token"]
    assert store["yk"]["created"][0]["return_url"].endswith("/store-paid.html")

    r = post(client, "/order/status", store, token=token)
    assert r.json()["state"] == "pending"

    pay(store, token)
    r = post(client, "/order/status", store, token=token)
    assert r.json()["state"] == "processing"

    run(db_session)  # creates the port, waits for the probe
    created = store["created"][0]
    assert created == {"acc": "sxkey1", "country": "DE", "state": "Bavaria", "city": "Munich",
                       "type": "residential", "rotation": "interval", "ttl": 10, "gb": 2}
    run(db_session)
    r = post(client, "/order/status", store, token=token)
    body = r.json()
    assert body["state"] == "done"
    proxy = body["proxy"]
    assert proxy["host"] == "203.0.113.5" and proxy["login"] == "lg" and proxy["password"] == "pw"
    assert proxy["gb_total"] == 2 and proxy["city"] == "Munich"
    assert "refresh_link" not in r.text and "apiKey" not in r.text and "vendor default name" not in r.text and "SECRET" not in r.text

    r = post(client, "/proxies", store)
    assert [p["id"] for p in r.json()["proxies"]] == [proxy["id"]]


def test_unavailable_type_is_rejected_before_payment(client, store):
    r = post(client, "/order", store, product="traffic", method="yookassa",
             params=traffic_params(country="US", type="mobile"))
    assert r.status_code == 409 and r.json()["error"] == "unavailable"
    assert store["yk"]["created"] == []


def test_no_vendor_balance_means_no_payment(client, store):
    store["sx_balance"].update(sxkey1=1.0, sxkey2=1.0)
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params(gb=5))
    assert r.status_code == 409 and r.json()["error"] == "out_of_stock"
    assert store["yk"]["created"] == []
    assert store["notes"]


def test_committed_traffic_counts_against_balance(client, store, db_session):
    # 50 USD on account 1: an 8 GB residential port commits 44 USD, so 2 more GB (11 USD) must fail.
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params(gb=8))
    pay(store, r.json()["token"])
    post(client, "/order/status", store, token=r.json()["token"])
    run(db_session, 2)
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params(gb=2))
    assert r.status_code == 409 and r.json()["error"] == "out_of_stock"


def test_vendor_error_refunds_automatically(client, store, db_session):
    store["create_error"] = "no exits"
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params())
    token = r.json()["token"]
    pay(store, token)
    post(client, "/order/status", store, token=token)
    run(db_session)
    body = post(client, "/order/status", store, token=token).json()
    assert body["state"] == "failed" and body["refunded"] is True
    assert store["refunds"][0][1] == body["amount_rub"]


def test_dead_port_is_deleted_and_refunded(client, store, db_session):
    store["probe"] = False
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params())
    token = r.json()["token"]
    pay(store, token)
    post(client, "/order/status", store, token=token)
    run(db_session)
    order = db_session.query(StoreOrder).filter_by(token=token).one()
    order.paid_at = to_db(utcnow() - timedelta(minutes=10))
    db_session.commit()
    run(db_session)
    body = post(client, "/order/status", store, token=token).json()
    assert body["state"] == "failed" and body["refunded"]
    assert store["deleted"] == [str(1000)]


def test_dc_order_flow_and_unknown_outcome(client, store, db_session):
    r = post(client, "/order", store, product="dc", method="yookassa", params={"country": "DE"})
    assert r.json()["amount_rub"] == 400
    token = r.json()["token"]
    pay(store, token)
    post(client, "/order/status", store, token=token)
    run(db_session)
    assert store["bought"] == ["prod-de"]
    assert post(client, "/order/status", store, token=token).json()["state"] == "processing"
    store["cy_orders"]["cy-1"].update(order_status="success", connection_host="194.0.2.10",
                                      connection_port=48349, connection_login="u", connection_password="p",
                                      access_expires_at="2026-11-09T21:29:52Z")
    run(db_session)
    body = post(client, "/order/status", store, token=token).json()
    assert body["state"] == "done"
    assert body["proxy"]["host"] == "194.0.2.10" and body["proxy"]["expires_at"] == "2026-11-09T21:29:52Z"
    assert body["proxy"]["can_renew"] is True

    # A purchase whose outcome is unknown is never retried or refunded automatically.
    store["buy_error"] = cy.CyError("timeout", sent=True)
    r = post(client, "/order", store, product="dc", method="yookassa", params={"country": "BG"})
    token2 = r.json()["token"]
    pay(store, token2)
    post(client, "/order/status", store, token=token2)
    run(db_session, 3)
    order = db_session.query(StoreOrder).filter_by(token=token2).one()
    assert order.fulfill == "manual" and not order.refunded
    assert len(store["bought"]) == 1


def test_traffic_exhaustion_and_topup(client, store, db_session):
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params(gb=1))
    token = r.json()["token"]
    pay(store, token)
    post(client, "/order/status", store, token=token)
    run(db_session, 2)
    proxy_id = post(client, "/order/status", store, token=token).json()["proxy"]["id"]

    store["traffic"]["1000"] = 600_000_000
    run(db_session)
    p = post(client, "/proxies", store).json()["proxies"][0]
    assert p["status"] == "active" and p["gb_used"] == 0.6
    store["traffic"]["1000"] = 1_000_000_001
    run(db_session)
    p = post(client, "/proxies", store).json()["proxies"][0]
    assert p["status"] == "exhausted" and store["archived"] == ["1000"]

    r = post(client, "/order", store, product="topup", method="yookassa", params={"proxy_id": proxy_id, "gb": 3})
    assert r.status_code == 200, r.text
    pay(store, r.json()["token"])
    post(client, "/order/status", store, token=r.json()["token"])
    run(db_session)
    p = post(client, "/proxies", store).json()["proxies"][0]
    assert p["status"] == "active" and p["gb_total"] == 4
    assert store["updated"] == [("1000", 4)] and store["unarchived"] == ["1000"]


def test_counter_reset_at_vendor_keeps_usage(client, store, db_session):
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params(gb=5))
    pay(store, r.json()["token"])
    post(client, "/order/status", store, token=r.json()["token"])
    run(db_session, 2)
    store["traffic"]["1000"] = 2_000_000_000
    run(db_session)
    store["traffic"]["1000"] = 500_000_000  # vendor counter restarted
    run(db_session)
    p = db_session.query(StoreProxy).one()
    db_session.refresh(p)
    assert p.bytes_used == 2_500_000_000


def test_dc_renewal_arms_and_disarms_auto_renew(client, store, db_session):
    r = post(client, "/order", store, product="dc", method="yookassa", params={"country": "DE"})
    pay(store, r.json()["token"])
    post(client, "/order/status", store, token=r.json()["token"])
    run(db_session)
    exp = utcnow() + timedelta(days=1)
    store["cy_orders"]["cy-1"].update(order_status="success", connection_host="h", connection_port=1,
                                      connection_login="u", connection_password="p",
                                      access_expires_at=exp.strftime("%Y-%m-%dT%H:%M:%SZ"))
    run(db_session)
    proxy_id = post(client, "/proxies", store).json()["proxies"][0]["id"]

    r = post(client, "/order", store, product="dc_renew", method="yookassa", params={"proxy_id": proxy_id})
    assert r.status_code == 200, r.text
    pay(store, r.json()["token"])
    post(client, "/order/status", store, token=r.json()["token"])
    run(db_session)
    assert store["auto_renew"] == [("cy-1", True)]
    r = post(client, "/order", store, product="dc_renew", method="yookassa", params={"proxy_id": proxy_id})
    assert r.status_code == 409 and r.json()["error"] == "renew_pending"

    new_exp = exp + timedelta(days=30)
    store["cy_orders"]["cy-1"]["access_expires_at"] = new_exp.strftime("%Y-%m-%dT%H:%M:%SZ")
    run(db_session)
    p = db_session.query(StoreProxy).one()
    db_session.refresh(p)
    assert p.renew_pending == 0 and from_db(p.expires_at) > exp + timedelta(days=29)
    assert store["auto_renew"][-1] == ("cy-1", False)


def test_other_license_cannot_touch_proxy(client, store, db_session, issue_key):
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params())
    pay(store, r.json()["token"])
    post(client, "/order/status", store, token=r.json()["token"])
    run(db_session, 2)
    proxy_id = db_session.query(StoreProxy).one().id
    other = issue_key("month")
    client.post("/api/license/activate", json={"key": other, "hwid": HWID_B})
    r = client.post("/api/store/order", json={"key": other, "hwid": HWID_B, "product": "topup",
                                               "method": "yookassa", "params": {"proxy_id": proxy_id, "gb": 1}})
    assert r.status_code == 404
    r = client.post("/api/store/order/status", json={"key": other, "hwid": HWID_B, "token": "x"})
    assert r.status_code == 404
    assert client.post("/api/store/proxies", json={"key": other, "hwid": HWID_B}).json()["proxies"] == []


def test_amount_mismatch_is_not_paid(client, store):
    r = post(client, "/order", store, product="dc", method="yookassa", params={"country": "DE"})
    token = r.json()["token"]
    pid = store["yk"]["created"][0]["id"]
    store["yk"]["payments"][pid].update(status="succeeded", amount={"value": "1.00", "currency": "RUB"})
    assert post(client, "/order/status", store, token=token).json()["state"] == "pending"


def test_cheapest_account_per_type_and_never_at_a_loss(client, store, db_session, monkeypatch):
    # Selling basis: mobile and residential at the new tariff (3), datacenter at the old one (0.9).
    monkeypatch.setattr(settings, "SX_API_KEYS_NEW", "newkey1,newkey2")
    monkeypatch.setattr(settings, "STORE_SX_USD_PER_GB", "mobile:3,residential:3,datacenter:0.9")
    r = post(client, "/catalog", store)
    assert r.json()["traffic"]["gb_price_rub"]["residential"] == round(3 * 80 / 0.955, 2)

    for ptype, expected in (("residential", "newkey1"), ("datacenter", "sxkey1")):
        r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params(type=ptype))
        pay(store, r.json()["token"])
        post(client, "/order/status", store, token=r.json()["token"])
        run(db_session)
        assert store["created"][-1]["acc"] == expected

    # 12 GB residential = 36 USD > 40 - 6 committed on the only new account; legacy accounts cost 5.5 > 3.
    r = post(client, "/order", store, product="traffic", method="yookassa", params=traffic_params(gb=12))
    assert r.status_code == 409 and r.json()["error"] == "out_of_stock"
    order = db_session.query(StoreOrder).filter(StoreOrder.product == "traffic").first()
    assert order.cost_usd == 6.0


def test_website_cabinet_can_buy_and_list(client, store, db_session):
    cab = {"cabinet": "W-" + "a" * 30}
    r = client.post("/api/store/catalog", json=cab)
    assert r.status_code == 200 and r.json()["dc"]["price_rub"] == 400
    assert client.post("/api/store/catalog", json={"cabinet": "W-short"}).status_code == 400
    r = client.post("/api/store/order", json={**cab, "product": "traffic", "method": "yookassa",
                                              "params": traffic_params()})
    token = r.json()["token"]
    assert store["yk"]["created"][-1]["return_url"].endswith("/kupit-proksi/?order=" + token)
    pay(store, token)
    client.post("/api/store/order/status", json={**cab, "token": token})
    run(db_session, 2)
    body = client.post("/api/store/order/status", json={**cab, "token": token}).json()
    assert body["state"] == "done" and body["proxy"]["login"] == "lg"
    assert len(client.post("/api/store/proxies", json=cab).json()["proxies"]) == 1
    # another cabinet and the app license see nothing of it
    assert client.post("/api/store/proxies", json={"cabinet": "W-" + "b" * 30}).json()["proxies"] == []
    assert post(client, "/proxies", store).json()["proxies"] == []
    assert client.post("/api/store/order/status", json={"cabinet": "W-" + "b" * 30, "token": token}).status_code == 404
