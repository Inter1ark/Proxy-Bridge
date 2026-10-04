import re

from tests.conftest import HWID_A, HWID_B, HWID_C

from app.keys import generate_key, normalize_key


def _act(client, key, hwid, path="activate"):
    return client.post(f"/api/license/{path}", json={"key": key, "hwid": hwid, "app_version": "3.2.0"})


def test_key_format_and_normalization():
    key = generate_key()
    assert re.fullmatch(r"PB-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}", key)
    body = key.replace("PB-", "").replace("-", "")
    assert normalize_key(" pb " + body.lower() + " ") == key
    assert normalize_key(body) is None
    assert normalize_key("PB-0000-0000-0000-0000") is None
    assert normalize_key(123) is None


def test_activate_and_verify_flow(client, issue_key):
    key = issue_key("month")
    r = _act(client, key, HWID_A)
    assert r.status_code == 200, r.text
    data = r.json()
    assert data["ok"] is True and data["plan"] == "month"
    assert data["activated_devices"] == 1 and data["max_devices"] == 2
    assert re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", data["expires_at"])

    # Re-activating the same device does not consume a slot.
    assert _act(client, key.lower().replace("-", " "), HWID_A).json()["activated_devices"] == 1

    r = _act(client, key, HWID_A, "verify")
    assert r.status_code == 200 and r.json()["activated_devices"] == 1

    r = _act(client, key, HWID_B, "verify")
    assert r.status_code == 403 and r.json() == {"ok": False, "error": "not_activated"}


def test_device_limit_and_deactivate(client, issue_key):
    key = issue_key("3months")
    assert _act(client, key, HWID_A).status_code == 200
    assert _act(client, key, HWID_B).json()["activated_devices"] == 2
    r = _act(client, key, HWID_C)
    assert r.status_code == 403 and r.json() == {"ok": False, "error": "device_limit"}

    r = client.post("/api/license/deactivate", json={"key": key, "hwid": HWID_A})
    assert r.status_code == 200 and r.json() == {"ok": True}
    r = _act(client, key, HWID_A, "verify")
    assert r.status_code == 403 and r.json()["error"] == "not_activated"
    assert _act(client, key, HWID_C).json()["activated_devices"] == 2


def test_lifetime_has_null_expiry(client, issue_key):
    key = issue_key("lifetime")
    data = _act(client, key, HWID_A).json()
    assert data["ok"] is True and data["expires_at"] is None and data["plan"] == "lifetime"


def test_expired(client, issue_key, expire_key):
    key = issue_key("month")
    assert _act(client, key, HWID_A).status_code == 200
    expire_key(key)
    r = _act(client, key, HWID_A)
    assert r.status_code == 410 and r.json() == {"ok": False, "error": "expired"}
    r = _act(client, key, HWID_A, "verify")
    assert r.status_code == 410 and r.json()["error"] == "expired"


def test_not_found_and_bad_request(client):
    r = _act(client, "PB-AAAA-BBBB-CCCC-DDDD", HWID_A)
    assert r.status_code == 404 and r.json() == {"ok": False, "error": "not_found"}
    r = _act(client, "nonsense", HWID_A)
    assert r.status_code == 400 and r.json() == {"ok": False, "error": "bad_request"}
    r = _act(client, "PB-AAAA-BBBB-CCCC-DDDD", "short")
    assert r.status_code == 400 and r.json()["error"] == "bad_request"
    r = client.post("/api/license/activate", json={"key": 5})
    assert r.status_code == 400 and r.json()["error"] == "bad_request"
    r = client.post("/api/license/deactivate", json={"key": "PB-AAAA-BBBB-CCCC-DDDD", "hwid": HWID_A})
    assert r.status_code == 404


def test_revoked(client, issue_key, admin_headers):
    key = issue_key("lifetime")
    assert _act(client, key, HWID_A).status_code == 200
    r = client.post(f"/api/admin/keys/{key}/revoke", headers=admin_headers)
    assert r.status_code == 200 and r.json() == {"ok": True}
    r = _act(client, key, HWID_A, "verify")
    assert r.status_code == 403 and r.json() == {"ok": False, "error": "revoked"}
    r = _act(client, key, HWID_B)
    assert r.status_code == 403 and r.json()["error"] == "revoked"
