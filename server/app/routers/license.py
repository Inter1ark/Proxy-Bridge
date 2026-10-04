"""License endpoints used by the Windows client: activate, verify, deactivate."""

import re

from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.orm import Session

from ..config import settings
from ..db import get_db
from ..errors import ApiError
from ..keys import normalize_key
from ..models import Device, License, iso_z, to_db, utcnow

router = APIRouter(prefix="/api/license", tags=["license"])

_HWID_RE = re.compile(r"^[0-9a-fA-F]{64}$")


class LicenseRequest(BaseModel):
    key: str = ""
    hwid: str = ""
    app_version: str = ""


def _parse(req: LicenseRequest) -> tuple[str, str]:
    key = normalize_key(req.key)
    hwid = (req.hwid or "").strip().lower()
    if key is None or not _HWID_RE.match(hwid):
        raise ApiError(400, "bad_request")
    return key, hwid


def _load_license(db: Session, key: str) -> License:
    lic = db.scalar(select(License).where(License.key == key))
    if lic is None:
        raise ApiError(404, "not_found")
    if lic.revoked:
        raise ApiError(403, "revoked")
    if lic.is_expired():
        raise ApiError(410, "expired")
    return lic


def _ok(lic: License) -> dict:
    return {
        "ok": True,
        "plan": lic.plan_id,
        "expires_at": iso_z(lic.expires_at),
        "activated_devices": len(lic.devices),
        "max_devices": settings.MAX_DEVICES,
    }


@router.post("/activate")
def activate(req: LicenseRequest, db: Session = Depends(get_db)):
    key, hwid = _parse(req)
    lic = _load_license(db, key)
    device = next((d for d in lic.devices if d.hwid == hwid), None)
    if device is None:
        if len(lic.devices) >= settings.MAX_DEVICES:
            raise ApiError(403, "device_limit")
        device = Device(license=lic, hwid=hwid)
        db.add(device)
    device.app_version = (req.app_version or "")[:32]
    device.last_seen = to_db(utcnow())
    db.commit()
    db.refresh(lic)
    return _ok(lic)


@router.post("/verify")
def verify(req: LicenseRequest, db: Session = Depends(get_db)):
    key, hwid = _parse(req)
    lic = _load_license(db, key)
    device = next((d for d in lic.devices if d.hwid == hwid), None)
    if device is None:
        raise ApiError(403, "not_activated")
    device.app_version = (req.app_version or "")[:32] or device.app_version
    device.last_seen = to_db(utcnow())
    db.commit()
    return _ok(lic)


@router.post("/deactivate")
def deactivate(req: LicenseRequest, db: Session = Depends(get_db)):
    key, hwid = _parse(req)
    lic = db.scalar(select(License).where(License.key == key))
    if lic is None:
        raise ApiError(404, "not_found")
    device = next((d for d in lic.devices if d.hwid == hwid), None)
    if device is not None:
        db.delete(device)
        db.commit()
    return {"ok": True}
