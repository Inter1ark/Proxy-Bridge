"""FastAPI application factory.

Run locally: uvicorn app.main:app --reload --port 8080
"""

import logging
import threading
import time
from contextlib import asynccontextmanager
from datetime import datetime, timedelta

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles

from . import db
from .config import SERVER_DIR, settings
from .errors import ApiError
from .routers import admin, checkout, license, webhook
from .models import Order
from .orders import check_order_with_provider

logger = logging.getLogger("proxybridge")

SITE_DIR = SERVER_DIR.parent / "site"


POLL_INTERVAL_SEC = 60
POLL_MAX_AGE_HOURS = 48


def pending_order_poller(stop: threading.Event) -> None:
    """Re-checks pending orders with the provider so a webhook is optional.

    YooKassa and CryptoBot orders whose payment was created less than
    POLL_MAX_AGE_HOURS ago are re-checked every POLL_INTERVAL_SEC seconds.
    A paid order gets its key issued exactly once (mark_order_paid is idempotent).
    """
    while not stop.wait(POLL_INTERVAL_SEC):
        try:
            cutoff = datetime.utcnow() - timedelta(hours=POLL_MAX_AGE_HOURS)
            with db.SessionLocal() as session:
                pending = (session.query(Order)
                           .filter(Order.status == "pending",
                                   Order.provider_payment_id.isnot(None),
                                   Order.created_at >= cutoff)
                           .all())
                for order in pending:
                    try:
                        check_order_with_provider(session, order)
                    except Exception as exc:  # noqa: BLE001
                        logger.warning("poller: order %s check failed: %s", order.id, exc)
        except Exception as exc:  # noqa: BLE001
            logger.warning("poller: cycle failed: %s", exc)


@asynccontextmanager
async def lifespan(app: FastAPI):
    db.init_db()
    if not settings.ADMIN_TOKEN:
        logger.warning("ADMIN_TOKEN is not set: admin endpoints are disabled")
    stop = threading.Event()
    thread = threading.Thread(target=pending_order_poller, args=(stop,), name="order-poller", daemon=True)
    thread.start()
    yield
    stop.set()


def create_app() -> FastAPI:
    logging.basicConfig(level=getattr(logging, settings.LOG_LEVEL.upper(), logging.INFO),
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    app = FastAPI(title="ProxyBridge API", docs_url=None, redoc_url=None, lifespan=lifespan)

    app.add_middleware(
        CORSMiddleware,
        allow_origins=[settings.BASE_URL],
        allow_methods=["GET", "POST"],
        allow_headers=["Content-Type", "X-Admin-Token"],
    )

    @app.exception_handler(ApiError)
    async def api_error_handler(request: Request, exc: ApiError):
        return JSONResponse({"ok": False, "error": exc.code}, status_code=exc.status_code)

    @app.exception_handler(RequestValidationError)
    async def validation_error_handler(request: Request, exc: RequestValidationError):
        return JSONResponse({"ok": False, "error": "bad_request"}, status_code=400)

    @app.get("/api/health")
    def health():
        return {"ok": True}

    app.include_router(license.router)
    app.include_router(checkout.router)
    app.include_router(webhook.router)
    app.include_router(admin.router)

    # Static site mounted last so API routes always win.
    if SITE_DIR.is_dir():
        app.mount("/", StaticFiles(directory=str(SITE_DIR), html=True), name="site")
    else:
        logger.warning("Site directory %s not found; static files are not served", SITE_DIR)
    return app


app = create_app()
