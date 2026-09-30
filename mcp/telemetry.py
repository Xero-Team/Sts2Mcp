"""Longitudinal playtest telemetry derived from immutable STS2 run files."""

from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any, Iterable


UNKNOWN = "unknown"


def ingest_runs(
    history_path: str | Path,
    ledger_path: str | Path,
    profile_id: int,
    save_scope: str,
    metadata: dict[str, Any] | None = None,
) -> dict[str, int]:
    """Append unseen run files to a normalized JSONL ledger."""
    history = Path(history_path)
    ledger = Path(ledger_path)
    metadata = metadata or {}
    existing_ids = {
        record.get("run_id") for record in _read_records(ledger)
    }

    records = []
    for run_path in sorted(history.glob("*.run")):
        with run_path.open(encoding="utf-8") as stream:
            run = json.load(stream)
        run_id = f"{save_scope}:profile{profile_id}:{run['start_time']}"
        if run_id in existing_ids:
            continue
        records.append(_normalize_run(run, run_id, metadata))
        existing_ids.add(run_id)

    if records:
        ledger.parent.mkdir(parents=True, exist_ok=True)
        with ledger.open("a", encoding="utf-8", newline="\n") as stream:
            for record in records:
                stream.write(json.dumps(record, separators=(",", ":")))
                stream.write("\n")

    return {"discovered": len(existing_ids), "appended": len(records)}


def aggregate_metrics(
    ledger_path: str | Path,
    *,
    character: str | None = None,
    game_build: str | None = None,
    mod_version: str | None = None,
    ascension: int | None = None,
    start_time_min: int | None = None,
    start_time_max: int | None = None,
) -> dict[str, Any]:
    """Aggregate ledger records with explicit denominators and version cohorts."""
    records = [
        record
        for record in _read_records(Path(ledger_path))
        if _matches_filters(
            record,
            character=character,
            game_build=game_build,
            mod_version=mod_version,
            ascension=ascension,
            start_time_min=start_time_min,
            start_time_max=start_time_max,
        )
    ]
    metrics = _summarize(records)
    metrics["cohorts"] = {
        "game_build": _build_cohorts(records, "build_id"),
        "mod_version": _build_cohorts(records, "mod_version"),
    }
    return metrics


def _normalize_run(
    run: dict[str, Any], run_id: str, metadata: dict[str, Any]
) -> dict[str, Any]:
    players = run.get("players") or []
    player = players[0] if players else {}
    abandoned = bool(run.get("was_abandoned"))
    development_test = bool(metadata.get("development_test"))
    eligible = not abandoned and not development_test

    if development_test:
        eligibility_status = "development_test"
    elif abandoned:
        eligibility_status = "abandoned"
    else:
        eligibility_status = "eligible"

    return {
        "run_id": run_id,
        "start_time": run.get("start_time"),
        "build_id": run.get("build_id") or UNKNOWN,
        "character_id": player.get("character") or UNKNOWN,
        "ascension": run.get("ascension", 0),
        "win": bool(run.get("win")),
        "was_abandoned": abandoned,
        "development_test": development_test,
        "eligible": eligible,
        "eligibility_status": eligibility_status,
        "terminal_cause": _terminal_cause(run),
        "final_deck": _final_deck(player),
        "card_choices": list(_card_choices(run.get("map_point_history") or [])),
        "mod_version": metadata.get("mod_version") or UNKNOWN,
        "mcp_version": metadata.get("mcp_version") or UNKNOWN,
        "report_path": metadata.get("report_path"),
    }


def _terminal_cause(run: dict[str, Any]) -> str:
    if run.get("win"):
        return "victory"
    if run.get("was_abandoned"):
        return "abandoned"
    for key in ("killed_by_encounter", "killed_by_event"):
        cause = run.get(key)
        if cause and cause not in {"NONE", "NONE.NONE"}:
            return str(cause)
    return "defeat"


def _final_deck(player: dict[str, Any]) -> list[str]:
    result = []
    for entry in player.get("deck") or []:
        if not isinstance(entry, dict):
            continue
        card = entry.get("card") if isinstance(entry.get("card"), dict) else entry
        card_id = card.get("id")
        if card_id:
            result.append(card_id)
    return result


def _card_choices(value: Any) -> Iterable[dict[str, Any]]:
    if isinstance(value, dict):
        choices = value.get("card_choices")
        if isinstance(choices, list):
            for choice in choices:
                if not isinstance(choice, dict):
                    continue
                card = choice.get("card")
                card_id = card.get("id") if isinstance(card, dict) else None
                if card_id:
                    yield {
                        "card_id": card_id,
                        "was_picked": bool(choice.get("was_picked")),
                    }
        for key, child in value.items():
            if key != "card_choices":
                yield from _card_choices(child)
    elif isinstance(value, list):
        for child in value:
            yield from _card_choices(child)


def _read_records(ledger_path: Path) -> list[dict[str, Any]]:
    if not ledger_path.exists():
        return []
    records = []
    with ledger_path.open(encoding="utf-8") as stream:
        for line in stream:
            if line.strip():
                records.append(json.loads(line))
    return records


def _matches_filters(
    record: dict[str, Any],
    *,
    character: str | None,
    game_build: str | None,
    mod_version: str | None,
    ascension: int | None,
    start_time_min: int | None,
    start_time_max: int | None,
) -> bool:
    return not (
        (character is not None and record.get("character_id") != character)
        or (game_build is not None and record.get("build_id") != game_build)
        or (mod_version is not None and record.get("mod_version") != mod_version)
        or (ascension is not None and record.get("ascension") != ascension)
        or (
            start_time_min is not None
            and record.get("start_time", 0) < start_time_min
        )
        or (
            start_time_max is not None
            and record.get("start_time", 0) > start_time_max
        )
    )


def _summarize(records: list[dict[str, Any]]) -> dict[str, Any]:
    eligible = [record for record in records if record.get("eligible")]
    wins = sum(bool(record.get("win")) for record in eligible)
    losses = len(eligible) - wins
    cards: dict[str, dict[str, Any]] = {}

    for record in records:
        for card_id in record.get("final_deck") or []:
            cards.setdefault(card_id, _empty_card_metrics())
        for choice in record.get("card_choices") or []:
            card_id = choice.get("card_id")
            if card_id:
                cards.setdefault(card_id, _empty_card_metrics())

    for record in eligible:
        for choice in record.get("card_choices") or []:
            card_id = choice.get("card_id")
            if not card_id:
                continue
            card = cards.setdefault(card_id, _empty_card_metrics())
            card["offers"] += 1
            if choice.get("was_picked"):
                card["picks"] += 1
            else:
                card["skips"] += 1
        for card_id in set(record.get("final_deck") or []):
            card = cards.setdefault(card_id, _empty_card_metrics())
            key = (
                "final_deck_eligible_wins"
                if record.get("win")
                else "final_deck_eligible_losses"
            )
            card[key] += 1

    for card in cards.values():
        card["pick_rate"] = _rate(card["picks"], card["offers"])
        presence_total = (
            card["final_deck_eligible_wins"]
            + card["final_deck_eligible_losses"]
        )
        card["presence_win_rate"] = _rate(
            card["final_deck_eligible_wins"], presence_total
        )

    return {
        "total_records": len(records),
        "eligible_runs": len(eligible),
        "wins": wins,
        "losses": losses,
        "abandoned_runs": sum(
            bool(record.get("was_abandoned")) for record in records
        ),
        "development_test_runs": sum(
            bool(record.get("development_test")) for record in records
        ),
        "win_rate": _rate(wins, len(eligible)),
        "cards": cards,
    }


def _empty_card_metrics() -> dict[str, int]:
    return {
        "offers": 0,
        "picks": 0,
        "skips": 0,
        "final_deck_eligible_wins": 0,
        "final_deck_eligible_losses": 0,
    }


def _rate(numerator: int, denominator: int) -> dict[str, Any]:
    return {
        "numerator": numerator,
        "denominator": denominator,
        "rate": numerator / denominator if denominator else None,
        "wilson_95": _wilson_95(numerator, denominator),
    }


def _wilson_95(successes: int, trials: int) -> dict[str, float] | None:
    if not trials:
        return None
    z = 1.96
    proportion = successes / trials
    denominator = 1 + z * z / trials
    center = (proportion + z * z / (2 * trials)) / denominator
    margin = (
        z
        * math.sqrt(
            proportion * (1 - proportion) / trials
            + z * z / (4 * trials * trials)
        )
        / denominator
    )
    return {
        "lower": max(0.0, center - margin),
        "upper": min(1.0, center + margin),
    }


def _build_cohorts(
    records: list[dict[str, Any]], field: str
) -> dict[str, dict[str, Any]]:
    values = {record.get(field) or UNKNOWN for record in records}
    return {
        value: _summarize(
            [record for record in records if (record.get(field) or UNKNOWN) == value]
        )
        for value in sorted(values)
    }