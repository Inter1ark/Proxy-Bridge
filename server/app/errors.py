"""API error type shared by all routers: {"ok": false, "error": "<code>"}."""


class ApiError(Exception):
    def __init__(self, status_code: int, code: str):
        super().__init__(code)
        self.status_code = status_code
        self.code = code
