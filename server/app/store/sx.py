"""Traffic vendor client (pay per GB ports).

Auth is the apiKey query parameter. Every account is one API key; the store
spreads ports over accounts by free balance. Ports are created without a name,
so the vendor keeps its default one.

Never pass vendor responses to the client as is: some fields (refresh links)
contain the API key.
"""

import hashlib
import logging
import threading
import time

import httpx

from ..config import settings

logger = logging.getLogger("proxybridge.store.sx")

API_URL = "https://api.sx.org/v2"
TIMEOUT = 30.0

# Our type name -> vendor pool id (create) and pool name (update).
POOL_ID = {"residential": 1, "mobile": 3, "datacenter": 4}
POOL_NAME = {"residential": "residential", "mobile": "mobile", "datacenter": "corporate"}
# Rotation -> connection type id (create) and name (update).
CONN_ID = {"static": 1, "interval": 3, "request": 3}
CONN_NAME = {"static": "keep-proxy", "interval": "rotate-connection", "request": "rotate-connection"}

BYTES_PER_GB = 1_000_000_000


class SxError(Exception):
    def __init__(self, message: str, status: int | None = None):
        super().__init__(message)
        self.status = status


class Account:
    """One vendor account. Accounts differ by tariff: what one GB of each type costs us."""

    def __init__(self, key: str, tariff: str = "legacy"):
        self.key = key
        self.tariff = tariff
        self.fp = hashlib.sha256(key.encode()).hexdigest()[:12]

    def cost(self, ptype: str) -> float | None:
        """USD per GB of this type on this account, None when unknown."""
        return settings.sx_costs(self.tariff).get(ptype)

    def __repr__(self) -> str:
        return f"<sx {self.fp} {self.tariff}>"


def accounts() -> list[Account]:
    return ([Account(k, "legacy") for k in settings.sx_keys]
            + [Account(k, "new") for k in settings.sx_keys_new])


def account_by_fp(fp: str) -> Account | None:
    return next((a for a in accounts() if a.fp == fp), None)


def _request(acc: Account, method: str, path: str, params: dict | None = None, body: dict | None = None):
    q = dict(params or {})
    q["apiKey"] = acc.key
    try:
        with httpx.Client(timeout=TIMEOUT) as client:
            resp = client.request(method, API_URL + path, params=q, json=body,
                                  headers={"Accept": "application/json"})
    except Exception as exc:  # noqa: BLE001
        raise SxError(f"{method} {path}: {type(exc).__name__}") from exc
    try:
        data = resp.json()
    except ValueError:
        data = {}
    if resp.status_code >= 400 or (isinstance(data, dict) and data.get("success") is False):
        msg = data.get("message") if isinstance(data, dict) else ""
        raise SxError(f"{method} {path}: {resp.status_code} {msg}"[:250], resp.status_code)
    return data


_balances: dict[str, tuple[float, float]] = {}


def balance(acc: Account, max_age: float = 60) -> float:
    """Account balance in USD, cached for a minute (there can be many accounts)."""
    hit = _balances.get(acc.fp)
    if hit and time.time() - hit[0] < max_age:
        return hit[1]
    data = _request(acc, "GET", "/user/balance")
    value = float(data.get("balance") or 0)
    _balances[acc.fp] = (time.time(), value)
    return value


# Geo directories change rarely: cache them for a day.
_cache: dict[str, tuple[float, object]] = {}
_cache_lock = threading.Lock()
DIR_TTL = 24 * 3600


def _cached(name: str, ttl: float, loader):
    now = time.time()
    with _cache_lock:
        hit = _cache.get(name)
        if hit and now - hit[0] < ttl:
            return hit[1]
    value = loader()
    with _cache_lock:
        _cache[name] = (now, value)
    return value


def _any_account() -> Account:
    accs = accounts()
    if not accs:
        raise SxError("no accounts configured")
    return accs[0]


def countries() -> list[dict]:
    """[{"id", "code", "name"}] sorted by name."""
    def load():
        data = _request(_any_account(), "GET", "/dir/countries")
        items = [{"id": int(c["id"]), "code": str(c["code"]).upper(), "name": c.get("name") or c["code"]}
                 for c in data.get("countries", []) if c.get("code")]
        return sorted(items, key=lambda c: c["name"])
    return _cached("countries", DIR_TTL, load)


def country_by_code(code: str) -> dict | None:
    code = (code or "").upper()
    return next((c for c in countries() if c["code"] == code), None)


def states(country_id: int) -> list[dict]:
    def load():
        data = _request(_any_account(), "GET", "/dir/states", {"countryId": country_id})
        items = [{"id": int(s["id"]), "name": s["name"]} for s in data.get("states", []) if s.get("name")]
        return sorted(items, key=lambda s: s["name"])
    return _cached(f"states:{country_id}", DIR_TTL, load)


def cities(country_id: int, state_id: int) -> list[dict]:
    def load():
        data = _request(_any_account(), "GET", "/dir/cities", {"countryId": country_id, "stateId": state_id})
        items = [{"id": int(c["id"]), "name": c["name"]} for c in data.get("cities", [])
                 if c.get("name") and int(c.get("dir_state_id") or state_id) == state_id]
        return sorted(items, key=lambda c: c["name"])
    return _cached(f"cities:{country_id}:{state_id}", DIR_TTL, load)


def available(country_code: str, ptype: str) -> bool:
    """True when the vendor has live exits of this type in the country (cached for an hour)."""
    def load():
        data = _request(_any_account(), "GET", "/proxy/search",
                        {"country": country_code.upper(), "limit": 1, "types[]": POOL_NAME[ptype]})
        if isinstance(data, list):
            return any(isinstance(x, str) for x in data)
        if isinstance(data, dict):
            return any(k != "success" for k in data)
        return False
    return _cached(f"avail:{country_code.upper()}:{ptype}", 3600, load)


def _first(data):
    item = data.get("data") if isinstance(data, dict) else None
    if isinstance(item, list):
        item = item[0] if item else None
    if not isinstance(item, dict):
        raise SxError("unexpected response without port data")
    return item


def create_port(acc: Account, country_code: str, state: str | None, city: str | None,
                ptype: str, rotation: str, ttl: int | None, gb: int) -> dict:
    """Create one dedicated login/password port with a traffic limit in GB. No name is sent."""
    body = {
        "country_code": country_code.upper(),
        "state": state or None,
        "city": city or None,
        "type_id": CONN_ID[rotation],
        "proxy_type_id": POOL_ID[ptype],
        "server_port_type_id": 1,
        "count": 1,
        "traffic_limit": int(gb),
    }
    if rotation == "interval":
        body["ttl"] = int(ttl or 1)
    return _first(_request(acc, "POST", "/proxy/create-port", body=body))


def update_port(acc: Account, port_id: str, country_id: int, state_id: int | None, city_id: int | None,
                ptype: str, rotation: str, ttl: int | None, gb: int) -> dict:
    body = {
        "geo_country_ids": [int(country_id)],
        "geo_state_id": state_id,
        "geo_city_id": city_id,
        "connection_type": CONN_NAME[rotation],
        "auth_type": "login-and-password",
        "proxy_types": [POOL_NAME[ptype]],
        "ttl": int(ttl) if rotation == "interval" and ttl else None,
        "traffic_limit": int(gb),
    }
    return _first(_request(acc, "PATCH", f"/proxy/update-port/{port_id}", body=body))


def port_traffic(acc: Account, port_id: str) -> int:
    """Bytes spent on the port according to the vendor."""
    data = _request(acc, "GET", "/proxy/port-info", {"id": port_id})
    info = (data.get("message") or {}).get("info") or {}
    return int((info.get("traffic") or {}).get("spent") or 0)


def archive(acc: Account, port_id: str) -> None:
    _request(acc, "PATCH", "/proxy/archive-port", {"id": port_id})


def unarchive(acc: Account, port_id: str) -> None:
    _request(acc, "PATCH", "/proxy/unarchive", {"id": port_id})


def delete(acc: Account, port_id: str) -> None:
    _request(acc, "DELETE", "/proxy/delete-port", {"id": port_id})


def refresh_ip(acc: Account, port_id: str) -> None:
    _request(acc, "GET", f"/proxy/refresh/{port_id}")
