from app import mailer


def _parts(msg):
    text = msg.get_body(preferencelist=("plain",)).get_content()
    html = msg.get_body(preferencelist=("html",)).get_content()
    return text, html


def test_key_email_has_key_links_and_both_formats():
    msg = mailer.build_message("buyer@example.com", "PB-AAAA-BBBB-CCCC-DDDD", "lifetime")
    assert msg["To"] == "buyer@example.com"
    assert msg["Subject"] == "Ваш ключ ProxyBridge"
    assert "ProxyBridge" in msg["From"]
    assert msg["Date"] and msg["Message-ID"]
    text, html = _parts(msg)
    for body in (text, html):
        assert "PB-AAAA-BBBB-CCCC-DDDD" in body
        assert "Навсегда" in body
        assert mailer.WINDOWS_URL in body
        assert mailer.MAC_ARM_URL in body
        assert mailer.MAC_X64_URL in body
        assert "/skachat/" in body
    assert "<html" in html and "<html" not in text
    assert "—" not in text and "—" not in html


def test_trial_email_differs():
    msg = mailer.build_message("t@example.com", "PB-1111-2222-3333-4444", "trial")
    assert "Пробный" in msg["Subject"]
    text, _ = _parts(msg)
    assert "1 день" in text and "Пробный день" in text
    assert "Спасибо за покупку" not in text


def test_key_email_escapes_html():
    msg = mailer.build_message("x@example.com", "<b>key</b>", "month")
    _, html = _parts(msg)
    assert "<b>key</b>" not in html and "&lt;b&gt;key&lt;/b&gt;" in html
