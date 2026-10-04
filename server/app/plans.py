"""Subscription plans (prices in RUB, duration in days, None = lifetime)."""

from dataclasses import dataclass


@dataclass(frozen=True)
class Plan:
    id: str
    title: str
    price_rub: int
    days: int | None

    def to_dict(self) -> dict:
        return {"id": self.id, "title": self.title, "price_rub": self.price_rub, "days": self.days}


PLANS: dict[str, Plan] = {
    "month": Plan("month", "1 месяц", 99, 30),
    "3months": Plan("3months", "3 месяца", 249, 90),
    "lifetime": Plan("lifetime", "Навсегда", 699, None),
}


def get_plan(plan_id: object) -> Plan | None:
    if not isinstance(plan_id, str):
        return None
    return PLANS.get(plan_id)


def plans_list() -> list[dict]:
    return [p.to_dict() for p in PLANS.values()]
