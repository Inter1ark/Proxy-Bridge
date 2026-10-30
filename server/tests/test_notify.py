import httpx

from app import notify


class _Resp:
    def __init__(self, code):
        self.status_code = code
        self.text = "{}"


def _fake_client(script, calls):
    class Client:
        def __init__(self, *a, **kw):
            pass

        def __enter__(self):
            return self

        def __exit__(self, *a):
            return False

        def post(self, url, json):
            calls.append(json["chat_id"])
            step = script.pop(0)
            if isinstance(step, Exception):
                raise step
            return _Resp(step)
    return Client


def test_retries_network_errors_then_delivers(monkeypatch):
    calls = []
    monkeypatch.setattr(notify.httpx, "Client", _fake_client([httpx.ConnectTimeout("handshake"), 502, 200], calls))
    monkeypatch.setattr(notify.time, "sleep", lambda s: None)
    notify._send_one("https://example/send", 42, "hi")
    assert calls == [42, 42, 42]


def test_client_errors_are_not_retried(monkeypatch):
    calls = []
    monkeypatch.setattr(notify.httpx, "Client", _fake_client([400], calls))
    monkeypatch.setattr(notify.time, "sleep", lambda s: None)
    notify._send_one("https://example/send", 7, "hi")
    assert calls == [7]


def test_gives_up_after_all_attempts(monkeypatch):
    calls = []
    errors = [httpx.ConnectTimeout("x") for _ in range(len(notify.RETRY_DELAYS) + 1)]
    monkeypatch.setattr(notify.httpx, "Client", _fake_client(errors, calls))
    monkeypatch.setattr(notify.time, "sleep", lambda s: None)
    notify._send_one("https://example/send", 9, "hi")
    assert len(calls) == len(notify.RETRY_DELAYS) + 1
