"""SQLAlchemy engine, session factory and table creation (sync)."""

from pathlib import Path

from sqlalchemy import create_engine
from sqlalchemy.orm import DeclarativeBase, sessionmaker

from .config import settings


class Base(DeclarativeBase):
    pass


engine = None
SessionLocal = sessionmaker(autoflush=False, expire_on_commit=False)


def configure(url: str | None = None):
    """Create the engine for the given URL and bind the session factory."""
    global engine
    url = url or settings.DATABASE_URL
    kwargs = {}
    if url.startswith("sqlite"):
        kwargs["connect_args"] = {"check_same_thread": False}
        file_part = url.split("///", 1)[1] if "///" in url else ""
        if file_part and file_part != ":memory:":
            Path(file_part).parent.mkdir(parents=True, exist_ok=True)
    engine = create_engine(url, **kwargs)
    SessionLocal.configure(bind=engine)
    return engine


# Columns added to existing tables after the first release: name -> SQL type.
# create_all() never alters existing tables, so they are added by migrate_sqlite().
ADDED_COLUMNS: dict[str, dict[str, str]] = {
    "orders": {
        "fail_reason": "VARCHAR(64)",
        "pay_type": "VARCHAR(32)",
        "income_rub": "FLOAT",
        "checked_at": "DATETIME",
    },
}


def migrate_sqlite(eng) -> list[str]:
    """Add missing columns to existing SQLite tables. Idempotent; returns added columns."""
    if eng.dialect.name != "sqlite":
        return []
    added = []
    with eng.begin() as conn:
        for table, columns in ADDED_COLUMNS.items():
            existing = {row[1] for row in conn.exec_driver_sql(f"PRAGMA table_info({table})").fetchall()}
            if not existing:
                continue
            for name, sql_type in columns.items():
                if name not in existing:
                    conn.exec_driver_sql(f"ALTER TABLE {table} ADD COLUMN {name} {sql_type}")
                    added.append(f"{table}.{name}")
    return added


def init_db() -> None:
    """Create tables if they do not exist and add new columns. Safe to call multiple times."""
    from . import models  # noqa: F401  (registers tables on Base)

    if engine is None:
        configure()
    Base.metadata.create_all(engine)
    migrate_sqlite(engine)


def get_db():
    """FastAPI dependency yielding a session."""
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
