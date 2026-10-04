"""ORM models: Order, License, Device.

Datetimes are stored as naive UTC (SQLite has no timezone support) and
converted to timezone-aware UTC values by the helpers below.
"""

from datetime import datetime, timezone

from sqlalchemy import Boolean, DateTime, ForeignKey, Integer, String, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column, relationship

from .db import Base


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def to_db(dt: datetime | None) -> datetime | None:
    """Aware datetime -> naive UTC for storage."""
    if dt is None:
        return None
    if dt.tzinfo is not None:
        dt = dt.astimezone(timezone.utc).replace(tzinfo=None)
    return dt


def from_db(dt: datetime | None) -> datetime | None:
    """Naive UTC from storage -> aware UTC."""
    if dt is None:
        return None
    if dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def iso_z(dt: datetime | None) -> str | None:
    dt = from_db(dt)
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ") if dt else None


def _now_db() -> datetime:
    return to_db(utcnow())


class Order(Base):
    __tablename__ = "orders"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    token: Mapped[str] = mapped_column(String(64), unique=True, index=True, nullable=False)
    plan_id: Mapped[str] = mapped_column(String(32), nullable=False)
    email: Mapped[str] = mapped_column(String(254), nullable=False)
    method: Mapped[str] = mapped_column(String(16), nullable=False)
    status: Mapped[str] = mapped_column(String(16), nullable=False, default="pending")
    amount_rub: Mapped[int] = mapped_column(Integer, nullable=False)
    provider_payment_id: Mapped[str | None] = mapped_column(String(128), index=True, nullable=True)
    key: Mapped[str | None] = mapped_column(String(32), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, nullable=False, default=_now_db)
    paid_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)

    license: Mapped["License | None"] = relationship(back_populates="order", uselist=False)


class License(Base):
    __tablename__ = "licenses"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    key: Mapped[str] = mapped_column(String(32), unique=True, index=True, nullable=False)
    plan_id: Mapped[str] = mapped_column(String(32), nullable=False)
    email: Mapped[str | None] = mapped_column(String(254), nullable=True)
    issued_at: Mapped[datetime] = mapped_column(DateTime, nullable=False, default=_now_db)
    expires_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)
    revoked: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    source: Mapped[str] = mapped_column(String(16), nullable=False, default="order")
    # One license per order: the unique constraint makes "mark paid" idempotent at DB level.
    order_id: Mapped[int | None] = mapped_column(ForeignKey("orders.id"), unique=True, nullable=True)

    order: Mapped["Order | None"] = relationship(back_populates="license")
    devices: Mapped[list["Device"]] = relationship(back_populates="license", cascade="all, delete-orphan")

    def is_expired(self, now: datetime | None = None) -> bool:
        if self.expires_at is None:
            return False
        now = now or utcnow()
        return from_db(self.expires_at) <= now


class Device(Base):
    __tablename__ = "devices"
    __table_args__ = (UniqueConstraint("license_id", "hwid", name="uq_device_license_hwid"),)

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    license_id: Mapped[int] = mapped_column(ForeignKey("licenses.id"), nullable=False, index=True)
    hwid: Mapped[str] = mapped_column(String(64), nullable=False)
    app_version: Mapped[str] = mapped_column(String(32), nullable=False, default="")
    activated_at: Mapped[datetime] = mapped_column(DateTime, nullable=False, default=_now_db)
    last_seen: Mapped[datetime] = mapped_column(DateTime, nullable=False, default=_now_db)

    license: Mapped["License"] = relationship(back_populates="devices")
