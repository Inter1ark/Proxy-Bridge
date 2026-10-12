from datetime import timedelta

from app.models import License, from_db, utcnow

from .conftest import HWID_A


def test_trial_day_starts_at_activation(client, issue_key, db_session):
    key = issue_key("trial")
    lic = db_session.query(License).filter_by(key=key).one()
    assert lic.expires_at is None  # not running until activated

    r = client.post("/api/license/activate", json={"key": key, "hwid": HWID_A})
    assert r.status_code == 200, r.text
    assert r.json()["plan"] == "trial"
    db_session.expire_all()
    exp = from_db(db_session.query(License).filter_by(key=key).one().expires_at)
    assert timedelta(hours=23) < exp - utcnow() <= timedelta(days=1)

    # re-activation on the same device does not extend it
    client.post("/api/license/activate", json={"key": key, "hwid": HWID_A})
    db_session.expire_all()
    assert from_db(db_session.query(License).filter_by(key=key).one().expires_at) == exp


def test_trial_cannot_be_bought(client, mock_yookassa):
    r = client.post("/api/checkout", json={"plan_id": "trial", "email": "a@b.co", "method": "yookassa"})
    assert r.status_code == 400 and r.json()["error"] == "bad_plan"
    assert [p["id"] for p in client.get("/api/plans").json()] == ["month", "3months", "lifetime"]
