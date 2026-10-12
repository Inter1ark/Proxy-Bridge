import json

import pytest

from app.main import SITE_DIR


@pytest.mark.skipif(not (SITE_DIR / "update" / "latest.json").is_file(), reason="site folder is not in the repository")
def test_update_manifest_served_as_json_without_cache(client):
    r = client.get("/update/latest.json")
    assert r.status_code == 200
    assert r.headers["content-type"].startswith("application/json")
    assert r.headers["cache-control"] == "no-cache"
    data = r.json()
    on_disk = json.loads((SITE_DIR / "update" / "latest.json").read_text(encoding="utf-8"))
    assert data["version"] == on_disk["version"]
    assert "windows" in data and "url" in data["windows"]


def test_update_manifest_missing_is_404(client, monkeypatch, tmp_path):
    monkeypatch.setattr("app.main.SITE_DIR", tmp_path)
    r = client.get("/update/latest.json")
    assert r.status_code == 404 and r.headers["cache-control"] == "no-cache"
