"""Admin endpoints protected by the X-Admin-Token header."""

import hmac

from fastapi import APIRouter, Depends, Header, Query
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.orm import Session

from .. import mailer
from ..config import settings
from ..db import get_db
from ..errors import ApiError
from ..keys import normalize_key
from ..models import License, Order
from ..orders import issue_license, order_to_dict
from ..plans import get_plan
from ..routers.checkout import is_valid_email

router = APIRouter(prefix="/api/admin", tags=["admin"])


def require_admin(x_admin_token: str | None = Header(default=None)) -> None:
    expected = settings.ADMIN_TOKEN
    if not expected or not x_admin_token or not hmac.compare_digest(x_admin_token, expected):
        raise ApiError(401, "unauthorized")


class IssueKeyRequest(BaseModel):
    plan_id: str = ""
    email: str | None = None


@router.post("/keys", dependencies=[Depends(require_admin)])
def issue_key(req: IssueKeyRequest, db: Session = Depends(get_db)):
    if get_plan(req.plan_id) is None:
        raise ApiError(400, "bad_plan")
    email = (req.email or "").strip().lower() or None
    if email and not is_valid_email(email):
        raise ApiError(400, "bad_email")
    lic = issue_license(db, req.plan_id, email, source="admin")
    db.commit()
    if email:
        mailer.send_key_email(email, lic.key, lic.plan_id)
    return {"key": lic.key}


@router.get("/orders", dependencies=[Depends(require_admin)])
def list_orders(limit: int = Query(default=50, ge=1, le=500), db: Session = Depends(get_db)):
    orders = db.scalars(select(Order).order_by(Order.id.desc()).limit(limit)).all()
    return [order_to_dict(o, admin=True) for o in orders]


@router.post("/keys/{key}/revoke", dependencies=[Depends(require_admin)])
def revoke_key(key: str, db: Session = Depends(get_db)):
    norm = normalize_key(key)
    if norm is None:
        raise ApiError(400, "bad_request")
    lic = db.scalar(select(License).where(License.key == norm))
    if lic is None:
        raise ApiError(404, "not_found")
    lic.revoked = True
    db.commit()
    return {"ok": True}
