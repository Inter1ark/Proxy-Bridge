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


def init_db() -> None:
    """Create tables if they do not exist. Safe to call multiple times."""
    from . import models  # noqa: F401  (registers tables on Base)

    if engine is None:
        configure()
    Base.metadata.create_all(engine)


def get_db():
    """FastAPI dependency yielding a session."""
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
