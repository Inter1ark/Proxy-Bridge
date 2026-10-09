"""Settings loaded from environment variables and an optional .env file.

No python-dotenv dependency: a tiny loader reads server/.env and fills
os.environ for keys that are not already set.
"""

import os
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parent.parent
ENV_FILE = SERVER_DIR / ".env"


def load_env_file(path: Path = ENV_FILE) -> None:
    """Read KEY=VALUE lines from .env into os.environ (existing vars win)."""
    if not path.is_file():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
            value = value[1:-1]
        if key and key not in os.environ:
            os.environ[key] = value


def _default_db_url() -> str:
    return "sqlite:///" + (SERVER_DIR / "data" / "proxybridge.db").as_posix()


class Settings:
    """Mutable settings object (tests override attributes directly)."""

    def __init__(self) -> None:
        load_env_file()
        env = os.environ
        self.BASE_URL: str = env.get("BASE_URL", "http://127.0.0.1:8080").rstrip("/")
        self.ADMIN_TOKEN: str = env.get("ADMIN_TOKEN", "")
        # Web admin panel (/admin): login form credentials; empty = login disabled.
        self.ADMIN_LOGIN: str = env.get("ADMIN_LOGIN", "")
        self.ADMIN_PASSWORD: str = env.get("ADMIN_PASSWORD", "")
        # Comma separated emails treated as test orders in the admin stats.
        self.ADMIN_TEST_EMAILS: str = env.get("ADMIN_TEST_EMAILS", "test@proxybridge.org")
        self.YOOKASSA_SHOP_ID: str = env.get("YOOKASSA_SHOP_ID", "")
        self.YOOKASSA_SECRET_KEY: str = env.get("YOOKASSA_SECRET_KEY", "")
        self.YOOKASSA_RETURN_URL: str = env.get(
            "YOOKASSA_RETURN_URL", self.BASE_URL + "/success.html?token={token}"
        )
        self.CRYPTOPAY_API_TOKEN: str = env.get("CRYPTOPAY_API_TOKEN", "")
        self.SMTP_HOST: str = env.get("SMTP_HOST", "")
        self.SMTP_PORT: int = int(env.get("SMTP_PORT", "587") or 587)
        self.TELEGRAM_BOT_TOKEN: str = env.get("TELEGRAM_BOT_TOKEN", "")
        self.TELEGRAM_ADMIN_IDS: str = env.get("TELEGRAM_ADMIN_IDS", "")
        self.TELEGRAM_PROXY: str = env.get("TELEGRAM_PROXY", "")
        self.SMTP_USER: str = env.get("SMTP_USER", "")
        self.SMTP_PASS: str = env.get("SMTP_PASS", "")
        self.SMTP_FROM: str = env.get("SMTP_FROM", "") or self.SMTP_USER
        self.DATABASE_URL: str = env.get("DATABASE_URL", _default_db_url())
        self.MAX_DEVICES: int = int(env.get("MAX_DEVICES", "2") or 2)
        self.LOG_LEVEL: str = env.get("LOG_LEVEL", "INFO")

    def return_url(self, token: str) -> str:
        return self.YOOKASSA_RETURN_URL.replace("{token}", token)

    @property
    def test_emails(self) -> set[str]:
        return {e.strip().lower() for e in (self.ADMIN_TEST_EMAILS or "").split(",") if e.strip()}

    @property
    def smtp_configured(self) -> bool:
        return bool(self.SMTP_HOST)


settings = Settings()
