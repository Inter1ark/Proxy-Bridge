"""Placeholder used when the vendor adapters are not installed.

The adapters (traffic.py, dedicated.py) are deployed with the server and are not part of the
public repository. Without them the store has no accounts and offers nothing.
"""

BYTES_PER_GB = 1_000_000_000


class VendorMissing(Exception):
    def __init__(self, message: str = "vendor adapter is not installed", status: int | None = None, sent: bool = False):
        super().__init__(message)
        self.status = status
        self.sent = sent


TrafficError = DedicatedError = VendorMissing


class Account:
    fp = ""


def accounts() -> list:
    return []


def account_by_fp(fp: str):
    return None


def __getattr__(name: str):
    def missing(*args, **kwargs):
        raise VendorMissing()
    return missing
