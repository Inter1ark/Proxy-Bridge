"""License key generation and normalization.

Format: PB-XXXX-XXXX-XXXX-XXXX, uppercase letters and digits without 0/O/1/I.
"""

import re
import secrets

ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
PREFIX = "PB"
GROUPS = 4
GROUP_LEN = 4
_BODY_RE = re.compile(r"^[%s]{%d}$" % (ALPHABET, GROUPS * GROUP_LEN))


def generate_key() -> str:
    body = "".join(secrets.choice(ALPHABET) for _ in range(GROUPS * GROUP_LEN))
    return format_key(body)


def format_key(body: str) -> str:
    parts = [body[i : i + GROUP_LEN] for i in range(0, len(body), GROUP_LEN)]
    return PREFIX + "-" + "-".join(parts)


def normalize_key(raw: object) -> str | None:
    """Return the canonical key for any user spelling, or None if invalid."""
    if not isinstance(raw, str):
        return None
    cleaned = re.sub(r"[\s\-_]", "", raw).upper()
    if not cleaned.startswith(PREFIX):
        return None
    body = cleaned[len(PREFIX):]
    if not _BODY_RE.match(body):
        return None
    return format_key(body)
