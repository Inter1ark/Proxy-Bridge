"""Admin authentication: X-Admin-Token header or a signed session cookie.

The cookie value is base64url("login|expiry_unix") + "." + HMAC-SHA256 hex,
signed with ADMIN_TOKEN. Mutating requests authenticated by the cookie must
carry the header X-Requested-With: pb-admin (CSRF guard; a cross-site form or
a simple fetch cannot set it).
"""

import base64
import hashlib
import hmac
import threading
import time
from collections import deque

from fastapi import Header, Request

from .config import settings
from .errors import ApiError

COOKIE_NAME = "pb_admin"
SESSION_TTL_SEC = 7 * 24 * 3600
CSRF_HEADER_VALUE = "pb-admin"
SAFE_METHODS = ("GET", "HEAD", "OPTIONS")

LOGIN_MAX_FAILS = 5
LOGIN_WINDOW_SEC = 10 * 60

_fails: dict[str, deque] = {}
_fails_lock = threading.Lock()


def _eq(a: str, b: str) -> bool:
    return hmac.compare_digest(a.encode("utf-8"), b.encode("utf-8"))


def login_enabled() -> bool:
    return bool(settings.ADMIN_LOGIN and settings.ADMIN_PASSWORD and settings.ADMIN_TOKEN)


def check_credentials(login: str, password: str) -> bool:
    if not login_enabled():
        return False
    ok_login = _eq(login or "", settings.ADMIN_LOGIN)
    ok_pass = _eq(password or "", settings.ADMIN_PASSWORD)
    return ok_login & ok_pass


def _sign(data: str) -> str:
    return hmac.new(settings.ADMIN_TOKEN.encode("utf-8"), data.encode("utf-8"), hashlib.sha256).hexdigest()


def make_session(login: str, now: float | None = None) -> str:
    expires = int((now or time.time()) + SESSION_TTL_SEC)
    payload = base64.urlsafe_b64encode(f"{login}|{expires}".encode("utf-8")).decode("ascii").rstrip("=")
    return f"{payload}.{_sign(payload)}"


def verify_session(value: str | None, now: float | None = None) -> bool:
    if not value or not login_enabled() or "." not in value:
        return False
    payload, sig = value.rsplit(".", 1)
    if not _eq(sig, _sign(payload)):
        return False
    try:
        raw = base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)).decode("utf-8")
        login, expires = raw.rsplit("|", 1)
        expires_at = int(expires)
    except (ValueError, UnicodeDecodeError):
        return False
    if expires_at <= (now or time.time()):
        return False
    return _eq(login, settings.ADMIN_LOGIN)


def cookie_secure() -> bool:
    return settings.BASE_URL.lower().startswith("https")


def client_ip(request: Request) -> str:
    real_ip = (request.headers.get("x-real-ip") or "").strip()
    if real_ip:
        return real_ip
    return request.client.host if request.client else "unknown"


def _recent(ip: str, now: float) -> deque:
    q = _fails.setdefault(ip, deque())
    while q and q[0] <= now - LOGIN_WINDOW_SEC:
        q.popleft()
    return q


def is_rate_limited(ip: str) -> bool:
    now = time.monotonic()
    with _fails_lock:
        return len(_recent(ip, now)) >= LOGIN_MAX_FAILS


def register_failure(ip: str) -> None:
    now = time.monotonic()
    with _fails_lock:
        _recent(ip, now).append(now)
        if len(_fails) > 10000:  # drop stale entries so memory stays bounded
            for key in [k for k, q in _fails.items() if not q or q[-1] <= now - LOGIN_WINDOW_SEC]:
                _fails.pop(key, None)


def clear_failures(ip: str) -> None:
    with _fails_lock:
        _fails.pop(ip, None)


def reset_rate_limit() -> None:
    with _fails_lock:
        _fails.clear()


def require_admin(request: Request,
                  x_admin_token: str | None = Header(default=None),
                  x_requested_with: str | None = Header(default=None)) -> str:
    """FastAPI dependency. Returns "token" or "cookie" (the auth method used)."""
    expected = settings.ADMIN_TOKEN
    if expected and x_admin_token and _eq(x_admin_token, expected):
        return "token"
    if verify_session(request.cookies.get(COOKIE_NAME)):
        if request.method.upper() not in SAFE_METHODS and x_requested_with != CSRF_HEADER_VALUE:
            raise ApiError(403, "csrf")
        return "cookie"
    raise ApiError(401, "unauthorized")
