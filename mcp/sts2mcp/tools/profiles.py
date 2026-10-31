"""Profile, compendium, wiki, glossary, and telemetry reads."""

import asyncio
import json
from pathlib import Path

from ..app import mcp
from ..client import (
    _bestiary_get,
    _compendium_get,
    _get,
    _glossary_get,
    _profile_get,
    _profiles_get,
    _profiles_post,
    _settings_get,
    _wait_for_profile,
    _wiki_get,
)
from ..errors import _handle_error


@mcp.tool()
async def get_profile() -> str:
    """Get the current profile's persistent progress summary.

    Includes character stats, discovered content, achievements, epochs, and global
    run totals for the active profile, plus status/kind, profile_id, progress_path,
    resolved_progress_path, profile_root, save_scope, and current_run when a run
    is active.
    """
    try:
        return await _profile_get()
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_compendium() -> str:
    """Get the active profile's Compendium-shaped progress summary.

    Mirrors the visible Compendium cards: Card Library, Relic Collection,
    Potion Lab, Bestiary, Character Stats, and Run History. Some sections point
    to detail endpoints such as glossary or bestiary when model-level metadata is
    separate from profile discovery/progress data. The top level includes the
    same status/kind/profile_id/progress_path/resolved_progress_path/profile_root/
    save_scope/current_run context as get_profile().
    This is a profile-level view, so it is useful at the main menu as well as
    during a run. Card/relic/potion rules text is still supplied by game state
    when those objects are visible in run-specific contexts.
    """
    try:
        return await _compendium_get()
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_playtest_metrics(
    ledger_path: str | None = None,
    character: str | None = None,
    game_build: str | None = None,
    mod_version: str | None = None,
    ascension: int | None = None,
    start_time_min: int | None = None,
    start_time_max: int | None = None,
    mcp_version: str | None = None,
    report_path: str | None = None,
    development_test: bool = False,
) -> str:
    """Ingest run history and report longitudinal playtest metrics.

    Primary rates exclude abandoned and explicitly marked development/test runs.
    Results include version cohorts, sample sizes, and Wilson 95% intervals.
    Existing ledger records are immutable, so metadata applies only to newly
    discovered runs.
    """
    try:
        from ..telemetry import aggregate_metrics, ingest_runs

        compendium = json.loads(await _compendium_get())
        profile_id = int(compendium["profile_id"])
        run_history = compendium["sections"]["run_history"]
        history_path = Path(run_history["history_path"])
        entries = run_history.get("entries") or []
        run_id = entries[0].get("run_id", "") if entries else ""
        save_scope = (
            run_id.split(":", 1)[0]
            if ":" in run_id
            else ("modded" if "modded" in history_path.parts else "vanilla")
        )
        target_ledger = (
            Path(ledger_path)
            if ledger_path
            else history_path.parent / "sts2-mcp-playtest-telemetry.jsonl"
        )
        ingest_runs(
            history_path=history_path,
            ledger_path=target_ledger,
            profile_id=profile_id,
            save_scope=save_scope,
            metadata={
                "mod_version": mod_version or "unknown",
                "mcp_version": mcp_version or "unknown",
                "report_path": report_path,
                "development_test": development_test,
            },
        )
        metrics = aggregate_metrics(
            target_ledger,
            character=character,
            game_build=game_build,
            mod_version=mod_version,
            ascension=ascension,
            start_time_min=start_time_min,
            start_time_max=start_time_max,
        )
        metrics["ledger_path"] = str(target_ledger)
        return json.dumps(metrics)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_settings() -> str:
    """Get current game settings and preferences.

    Returns status/kind plus the saved display, audio, gameplay, and mod-loading
    preferences, the selected language, and whether the intro is skipped, as
    exposed by the Sts2Mcp HTTP settings endpoint.
    """
    try:
        return await _settings_get()
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def search_wiki(
    query: str = "", item_type: str = "all", limit: int | None = None, scope: str = "discovered"
) -> str:
    """Search or list card and relic wiki entries.

    With a query this is a fuzzy search, so agents can ask for a card or relic
    by approximate name. With no query it LISTS everything in scope, in name
    order, which is what building a static catalog wants. Card results include
    both base and upgraded variants when the card can be upgraded.

    Args:
        query: Search text such as "ironclad perfect strike" or "silver spoon".
            Omit it to list rather than search.
        item_type: "all", "card", or "relic".
        limit: Maximum results. Defaults to 10 when searching and to the whole
            list otherwise; the mod clamps it to a bounded maximum, which is
            higher for a listing than for a search.
        scope: "discovered" (the default) honours the active profile's
            discovery progress; "all" ignores it and covers every card and
            relic the game defines. Use "all" only for tooling -- it spoils
            content the profile has not met. Every result carries a
            `discovered` flag either way.
    """
    try:
        return await _wiki_get(query, item_type, limit, scope)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_bestiary() -> str:
    """Get reflected monster and encounter metadata.

    This is model/reference metadata and is separate from Compendium profile
    encounter stats. The response includes status/kind, monster and encounter
    counts, and deterministic monster/encounter arrays. The in-game Bestiary
    card is currently marked locked/future.
    """
    try:
        return await _bestiary_get()
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_glossary_cards() -> str:
    """Get active-run card pool metadata.

    Requires a run in progress. This is scoped to the current run/character pool,
    not profile-wide discovered cards; use get_compendium() for profile progress.
    The response includes profile/save context, current_run save context,
    count, and items. current_run.run_id and current_run.seed are included
    when current_run.save exposes them.
    """
    try:
        return await _glossary_get("cards")
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_glossary_relics() -> str:
    """Get active-run relic pool metadata.

    Requires a run in progress. This is scoped to the current run/character pool,
    not profile-wide discovered relics; use get_compendium() for profile progress.
    The response includes profile/save context, current_run save context,
    count, and items. current_run.run_id and current_run.seed are included
    when current_run.save exposes them.
    """
    try:
        return await _glossary_get("relics")
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_glossary_potions() -> str:
    """Get active-run potion pool metadata.

    Requires a run in progress. This is scoped to the current run/character pool,
    not profile-wide discovered potions; use get_compendium() for profile progress.
    The response includes profile/save context, current_run save context,
    count, and items. current_run.run_id and current_run.seed are included
    when current_run.save exposes them.
    """
    try:
        return await _glossary_get("potions")
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_glossary_keywords() -> str:
    """Get active-run keyword metadata collected from active pools.

    Requires a run in progress. Keywords come from cards, relics, and potions
    reachable in the current run context. The response includes profile/save
    context, current_run save context, count, and items. current_run.run_id
    and current_run.seed are included when current_run.save exposes them.
    """
    try:
        return await _glossary_get("keywords")
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def list_profiles() -> str:
    """List the three profile slots and identify the active slot.

    The response includes status/kind/count plus per-slot profile_id,
    progress_path, resolved_progress_path, profile_root, save_scope, and
    has_data. The has_data field indicates whether a slot currently has
    progress data.
    """
    try:
        return await _profiles_get()
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def switch_profile(profile_id: int) -> str:
    """Switch to a profile slot through the game's profile UI.

    Use an empty slot to create or enter a fresh profile for testing. This cannot
    be used while a run is in progress. HTTP validation errors include
    structured error_code values from the profile endpoint.

    Args:
        profile_id: Profile slot to switch to. Must be 1, 2, or 3.
    """
    try:
        body = {"action": "switch", "profile_id": profile_id}
        result = await _profiles_post(body)
        try:
            parsed = json.loads(result)
            if parsed.get("status") == "error":
                return result

            message = parsed.get("message", "")
            if isinstance(message, str) and message.startswith("Opened profile screen"):
                for _ in range(20):
                    await asyncio.sleep(0.1)
                    state_text = await _get({"format": "json"})
                    state = json.loads(state_text)
                    if state.get("menu_screen") == "profile_select":
                        result = await _profiles_post(body)
                        parsed = json.loads(result)
                        if parsed.get("status") == "error":
                            return result
                        break
            result = await _wait_for_profile(profile_id, result)
        except json.JSONDecodeError:
            pass
        return result
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def delete_profile(profile_id: int) -> str:
    """Delete an inactive profile slot.

    The active profile cannot be deleted through this tool. Switch away first if
    you intend to remove a slot after backing up any data you need. Attempting to
    delete the active profile returns the endpoint's active_profile_delete error.

    Args:
        profile_id: Profile slot to delete. Must be 1, 2, or 3.
    """
    try:
        return await _profiles_post({"action": "delete", "profile_id": profile_id})
    except Exception as e:
        return _handle_error(e)
