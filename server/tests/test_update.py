import json

from app.main import SITE_DIR


def test_update_manifest_served_as_json_without_cache(client):
    r = client.get("/update/latest.json")
    assert r.status_code == 200
    assert r.headers["content-type"].startswith("application/json")
    assert r.headers["cache-control"] == "no-cache"
    data = r.json()
    on_disk = json.loads((SITE_DIR / "update" / "latest.json").read_text(encoding="utf-8"))
    assert data["version"] == on_disk["version"]
    assert "windows" in data and "url" in data["windows"]
