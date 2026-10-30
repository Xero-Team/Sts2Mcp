"""MCP server bridge for Slay the Spire 2.

Connects to the Sts2Mcp mod's HTTP server and exposes game actions
as MCP tools for Claude Desktop / Claude Code.
"""

import argparse
import asyncio
import json
import os
from pathlib import Path
import sys

import httpx
from mcp.server.fastmcp import FastMCP

_server_dir = str(Path(__file__).resolve().parent)
if _server_dir not in sys.path:
    sys.path.insert(0, _server_dir)

mcp = FastMCP("sts2")

_base_url: str = "http://localhost:15526"
_trust_env: bool = True
_http: httpx.AsyncClient | None = None
DEFAULT_HOST = os.environ.get("STS2_HOST", "localhost")
DEFAULT_PORT = int(os.environ.get("STS2_PORT", "15526"))
_base_url = f"http://{DEFAULT_HOST}:{DEFAULT_PORT}"


def _sp_url() -> str:
    return f"{_base_url}/api/v1/singleplayer"


def _root_url() -> str:
    return f"{_base_url}/"


def _mp_url() -> str:
    return f"{_base_url}/api/v1/multiplayer"


def _profile_url() -> str:
    return f"{_base_url}/api/v1/profile"


def _compendium_url() -> str:
    return f"{_base_url}/api/v1/compendium"


def _wiki_url() -> str:
    return f"{_base_url}/api/v1/wiki"


def _settings_url() -> str:
    return f"{_base_url}/api/v1/settings"


def _bestiary_url() -> str:
    return f"{_base_url}/api/v1/bestiary"


def _glossary_url(kind: str) -> str:
    return f"{_base_url}/api/v1/glossary/{kind}"


def _profiles_url() -> str:
    return f"{_base_url}/api/v1/profiles"


def _map_drawings_url() -> str:
    return f"{_base_url}/api/v1/mapdrawings"


def _get_client() -> httpx.AsyncClient:
    global _http
    if _http is None:
        # Must exceed the mod's own 10 s main-thread timeout, otherwise the client gives up at
        # the same moment the mod is about to answer 503 and the caller never learns that the
        # request was NOT executed (and is therefore safe to retry).
        _http = httpx.AsyncClient(timeout=httpx.Timeout(15), trust_env=_trust_env)
    return _http


async def _get(params: dict | None = None) -> str:
    r = await _get_client().get(_sp_url(), params=params)
    r.raise_for_status()
    return r.text


async def _root_get() -> str:
    r = await _get_client().get(_root_url())
    r.raise_for_status()
    return r.text


async def _post(body: dict, wait: bool = False) -> str:
    """POST a singleplayer action.

    With wait=True the mod holds the response until the game has settled and embeds the
    settled state, so the caller does not need a GET poll loop. That makes the request
    outlive the shared 15 s client timeout, hence the per-request override: the mod's own
    settle timeout is 10 s on top of the 10 s main-thread timeout.
    """
    if not wait:
        r = await _get_client().post(_sp_url(), json=body)
    else:
        r = await _get_client().post(
            _sp_url(), json={**body, "wait": True}, timeout=httpx.Timeout(35)
        )
    r.raise_for_status()
    return r.text


async def _mp_get(params: dict | None = None) -> str:
    r = await _get_client().get(_mp_url(), params=params)
    r.raise_for_status()
    return r.text


async def _mp_post(body: dict) -> str:
    r = await _get_client().post(_mp_url(), json=body)
    r.raise_for_status()
    return r.text


async def _profile_get() -> str:
    r = await _get_client().get(_profile_url())
    r.raise_for_status()
    return r.text


async def _compendium_get() -> str:
    r = await _get_client().get(_compendium_url())
    r.raise_for_status()
    return r.text


async def _wiki_get(query: str, item_type: str = "all", limit: int | None = None,
                    scope: str = "discovered") -> str:
    params: dict[str, object] = {"query": query, "item_type": item_type, "scope": scope}
    if limit is not None:
        params["limit"] = limit
    r = await _get_client().get(_wiki_url(), params=params)
    r.raise_for_status()
    return r.text


async def _settings_get() -> str:
    r = await _get_client().get(_settings_url())
    r.raise_for_status()
    return r.text


async def _bestiary_get() -> str:
    r = await _get_client().get(_bestiary_url())
    r.raise_for_status()
    return r.text


async def _glossary_get(kind: str) -> str:
    r = await _get_client().get(_glossary_url(kind))
    r.raise_for_status()
    return r.text


async def _profiles_get() -> str:
    r = await _get_client().get(_profiles_url())
    r.raise_for_status()
    return r.text


async def _profiles_post(body: dict) -> str:
    r = await _get_client().post(_profiles_url(), json=body)
    r.raise_for_status()
    return r.text


async def _map_drawings_get() -> str:
    r = await _get_client().get(_map_drawings_url())
    r.raise_for_status()
    return r.text


async def _wait_for_profile(profile_id: int, fallback: str) -> str:
    last_profiles: dict | None = None
    for _ in range(30):
        await asyncio.sleep(0.1)
        profiles_text = await _profiles_get()
        profiles = json.loads(profiles_text)
        last_profiles = profiles
        if profiles.get("current_profile_id") == profile_id:
            return json.dumps(
                {
                    "status": "ok",
                    "message": f"Switched to profile {profile_id}",
                    "current_profile_id": profile_id,
                    "profiles": profiles.get("profiles", []),
                },
                indent=2,
            )

    return json.dumps(
        {
            "status": "error",
            "error": f"Timed out waiting for profile {profile_id} to become active",
            "current_profile_id": (
                last_profiles.get("current_profile_id")
                if isinstance(last_profiles, dict)
                else None
            ),
            "profiles": (
                last_profiles.get("profiles", [])
                if isinstance(last_profiles, dict)
                else []
            ),
            "initial_response": fallback,
        },
        indent=2,
    )


def _handle_error(e: Exception) -> str:
    if isinstance(e, httpx.ConnectError):
        return "Error: Cannot connect to Sts2Mcp mod. Is the game running with the mod enabled?"
    if isinstance(e, httpx.HTTPStatusError):
        structured = _format_structured_http_error(e.response)
        if structured is not None:
            return structured
        return f"Error: HTTP {e.response.status_code} — {e.response.text}"
    return f"Error: {e}"


def _format_structured_http_error(response: httpx.Response) -> str | None:
    try:
        data = response.json()
    except ValueError:
        return None

    if not isinstance(data, dict) or data.get("status") != "error":
        return None

    payload = dict(data)
    payload.setdefault("http_status", response.status_code)
    return json.dumps(payload, indent=2)


def _response_error_code(response: httpx.Response) -> str | None:
    try:
        data = response.json()
    except ValueError:
        return None
    return data.get("error_code") if isinstance(data, dict) else None


async def _menu_select_post(body: dict) -> str:
    try:
        return await _post(body)
    except httpx.HTTPStatusError as e:
        if (
            e.response.status_code == 409
            and _response_error_code(e.response) == "multiplayer_run_active"
        ):
            return await _mp_post(body)
        raise


# ---------------------------------------------------------------------------
# General
# ---------------------------------------------------------------------------


@mcp.tool()
async def get_api_index() -> str:
    """Get the Sts2Mcp HTTP server status and endpoint index.

    Returns status/kind, version, the mod version greeting, bound listener
    prefixes, endpoint_count, and the current list of HTTP API routes exposed by
    the loaded mod.
    """
    try:
        return await _root_get()
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_game_state(format: str = "markdown", values: str = "powered") -> str:
    """Get the current Slay the Spire 2 game state.

    Returns the full game state including player stats, hand, enemies, potions, etc.
    The state_type field indicates the current screen (combat, map, event, shop,
    fake_merchant, etc.).

    Args:
        format: "markdown" for human-readable output, "json" for structured data.
        values: "powered" (default) renders card descriptions and enemy intent
            labels the way the game does, with the player's standing Strength,
            Dexterity, Vigor, Focus, Weak and relic bonuses already applied to the
            numbers. "unpowered" strips that back to the card's own base value plus
            its enchantment, and marks each card `text_baked: false` and each
            rebuilt intent `unbaked: true`. Keywords, enchantment text and energy
            cost are identical either way.
    """
    try:
        return await _get({"format": format, "values": values})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def menu_select(option: str, seed: str | None = None, ascension: int | None = None) -> str:
    """Select a visible menu option.

    Use with state_type "menu" or "game_over". Covers main-menu navigation,
    singleplayer / multiplayer submenus, multiplayer host & join lobbies,
    multiplayer load lobby (resume saved co-op run), character select for SP
    and MP (with `unready` once readied in MP), custom-run setup, profile
    switching, timeline controls, tutorial prompts, blocking popups, and
    game-over main-menu return.

    On menu_screen "settings" (reported both on the main menu and mid-run) the
    only option is "back", which closes the screen and returns to what was under it.

    On menu_screen "pause" (the in-run pause menu, opened with open_pause_menu):
      - "resume" (aliases back / close / continue) returns to the run.
      - "settings" and "compendium" open screens that "back" leaves again.
      - "give_up" (alias abandon) ABANDONS THE RUN PERMANENTLY. It opens the
        game's confirmation popup, which the next state reports as menu_screen
        "popup"; it is never auto-confirmed, so answer it with "yes" or "no".
      - "save_and_quit" (alias main_menu) saves and returns to the main menu,
        where "continue" resumes the run. "disconnect" appears in multiplayer.
    On the screens the pause menu opens the only option is "back".

    Timeline entry from the main menu may still return `manual_action_required`
    when obtained epochs are pending, but `advance` on an already-open Timeline
    can reveal visible obtained epoch slots and may return `retry` while the
    screen is animating. Pending epochs without visible slots still return
    `manual_action_required`.

    Multiplayer flow tips:
      - On menu_screen "multiplayer_join", use refresh / back / join_<index> /
        join_<player_id> (e.g. "join_0" or "join_76561198000000000").
      - On menu_screen "multiplayer_load_lobby", use confirm (or embark) to
        ready up; the run resumes once everyone is connected and ready.
      - On menu_screen "character_select" while in MP, the state includes a
        `lobby` block with the roster, ready states, and ascension; "unready"
        becomes available after you confirm/embark.

    Custom mode ("custom_run"): pick a character, then confirm/embark. Unlike
    standard singleplayer, a seed IS supported here. Toggle run modifiers with
    the "modifier_<key>" option names the state advertises (the bare key, or the
    raw modifier id when unambiguous, also work — the per-character card
    modifiers all share the id CHARACTER_CARDS and must be addressed by key).
    Modifiers can be mutually exclusive, so the response returns the resulting
    list of ticked modifier keys rather than only the one toggled.

    Ascension ("character_select" and "custom_run"): "ascension_up" /
    "ascension_down" move the level by one and are advertised only while that
    direction is available; the state reports the current level as `ascension`,
    the unlocked cap as `max_ascension`, and whether the panel is on screen as
    `ascension_selectable`.
    The `selected` block reflects the current character and enabled modifier ids.

    Args:
        option: Option ID from the current menu state's options list. If an
            option is listed under blocked_options, selecting it returns the
            API's manual-action response instead of forcing UI entry.
        seed: Optional seed for supported embark flows (custom run, multiplayer
            lobbies, daily). Standard mode rejects seeds.
        ascension: Optional ascension level, applied on character select before the
            option is handled. Send it with the character pick, with confirm/embark,
            or on its own (with option ""). The current level and the unlocked
            maximum are reported as `ascension` / `max_ascension` in state.
    """
    body: dict = {"action": "menu_select", "option": option}
    if seed is not None:
        body["seed"] = seed
    if ascension is not None:
        body["ascension"] = ascension
    try:
        return await _menu_select_post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def open_pause_menu() -> str:
    """Open the in-run pause menu (the top-bar gear / Esc).

    This is the only API route out of a run: the state that follows is
    state_type "menu", menu_screen "pause", and menu_select drives it from
    there. Requires a run in progress; it answers `already_open: true` when the
    menu is already up, and errors when another screen is stacked on top of it
    (leave that with menu_select "back") or while a card is still being played.

    WARNING: on that menu "give_up" abandons the run permanently (it asks for
    confirmation first) and "save_and_quit" ends the session at the main menu.
    """
    try:
        return await _post({"action": "open_pause_menu"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def timeline_reveal_epochs() -> str:
    """Reveal every obtained-but-unrevealed timeline epoch.

    A finished run leaves its epochs "obtained". Until they are revealed the main
    menu hides `singleplayer`, so no new run can be started. Call this from the
    main menu and repeat until the response has `done: true`; `pending_epoch_ids`
    shows what is left. It opens the Timeline, reveals each pending epoch through
    the game's own reveal path (granting the epoch's unlocks and writing progress),
    and returns to the main menu when finished.
    """
    try:
        return await _post({"action": "timeline_reveal_epochs"})
    except Exception as e:
        return _handle_error(e)


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
        from telemetry import aggregate_metrics, ingest_runs

        compendium = json.loads(await _compendium_get())
        profile_id = int(compendium["profile_id"])
        run_history = compendium["sections"]["run_history"]
        history_path = Path(run_history["history_path"])
        entries = run_history.get("entries") or []
        run_id = entries[0].get("run_id", "") if entries else ""
        save_scope = run_id.split(":", 1)[0] if ":" in run_id else (
            "modded" if "modded" in history_path.parts else "vanilla"
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
async def search_wiki(query: str = "", item_type: str = "all", limit: int | None = None,
                      scope: str = "discovered") -> str:
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


@mcp.tool()
async def use_potion(slot: int, target: str | None = None, wait: bool = False) -> str:
    """Use a potion from the player's potion slots.

    Works both during and outside of combat. Combat-only potions require an active battle.
    Enemy-targeted potions require combat and a target from state `valid_targets`.

    Args:
        slot: The belt slot from game state `player.potions[].slot` — not a position
            in the potions list. After using the potion in slot 0, the remaining one
            is still slot 1.
        target: Target enemy, as either its entity_id ("JAW_WORM_0") or its combat_id
            ("3"), or one of the potion's `valid_targets` from game state. Required
            for enemy-targeted potions unless exactly one enemy is alive, in which
            case it is chosen automatically.
        wait: Hold the response until the game has settled (animations, card
            resolution and the redraw finished) and return the settled state with it,
            instead of answering as soon as the action was dispatched. Saves a
            follow-up get_game_state(). The reply adds `settled`, `changed`,
            `waited_ms`, `polls` and `state`.
    """
    body: dict = {"action": "use_potion", "slot": slot}
    if target is not None:
        body["target"] = target
    try:
        return await _post(body, wait=wait)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def sell_potion(slot: int) -> str:
    """Sell a potion to the merchant by using it on them.

    Foul Potion's second mode is worth 100 gold. Only valid in a shop or fake
    merchant, and only for potions that have a merchant interaction — shop state
    lists those under `sellable_potions`.

    Args:
        slot: The belt slot from game state `player.potions[].slot`.
    """
    try:
        return await _post({"action": "sell_potion", "slot": slot})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def discard_potion(slot: int) -> str:
    """Discard a potion from the player's potion slots to free up space.

    Use this when all potion slots are full and you need room for incoming potions
    (e.g. before collecting a potion reward). Queued potions cannot be discarded.

    Args:
        slot: Potion slot index to discard (as shown in game state).
    """
    try:
        return await _post({"action": "discard_potion", "slot": slot})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def proceed_to_map(wait: bool = False) -> str:
    """Proceed from the current screen to the map.

    Works from: rewards screen, rest site, shop, fake merchant, and treasure.
    Does NOT work for events — use event_choose_option() with the Proceed option's index.

    Args:
        wait: Hold the response until the game has settled (animations, card
            resolution and the redraw finished) and return the settled state with it,
            instead of answering as soon as the action was dispatched. Saves a
            follow-up get_game_state(). The reply adds `settled`, `changed`,
            `waited_ms`, `polls` and `state`.
    """
    try:
        return await _post({"action": "proceed"}, wait=wait)
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Combat (state_type: monster / elite / boss)
# ---------------------------------------------------------------------------


@mcp.tool()
async def combat_play_card(
    card_index: int | None = None,
    target: str | None = None,
    card_uid: str | None = None,
    wait: bool = False,
) -> str:
    """[Combat] Play a card from the player's hand.

    Args:
        card_index: Index of the card in hand (0-based, as shown in game state).
        target: Target enemy, as either its entity_id ("JAW_WORM_0") or its
            combat_id ("3"), or one of the hand card's `valid_targets` from game
            state. Required for single-target cards (when the card reports
            `requires_target: true`), except when only one enemy is alive, in which
            case it is chosen automatically.
        card_uid: The card's uid from game state (e.g. "STRIKE#2"). Preferred over
            card_index: it identifies the card instance itself and stays valid for
            the whole combat, so a multi-card plan does not need recomputing after
            every play.
        wait: Hold the response until the game has settled (animations, card
            resolution and the redraw finished) and return the settled state with it,
            instead of answering as soon as the action was dispatched. Saves a
            follow-up get_game_state(). The reply adds `settled`, `changed`,
            `waited_ms`, `polls` and `state`.

    card_index is a position in the hand, so playing a card shifts the indices of
    everything behind it; re-read state between plays if you use it.
    """
    if card_uid is None and card_index is None:
        return "Error: provide card_uid (preferred) or card_index."
    body: dict = {"action": "play_card"}
    if card_uid is not None:
        body["card_uid"] = card_uid
    if card_index is not None:
        body["card_index"] = card_index
    if target is not None:
        body["target"] = target
    try:
        return await _post(body, wait=wait)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def combat_end_turn(wait: bool = False) -> str:
    """[Combat] End the player's current turn.

    Only call this when battle state reports `can_end_turn`; when it is false the
    state names the reason in `end_turn_blocked_reason`.

    Args:
        wait: Hold the response until the game has settled (animations, card
            resolution and the redraw finished) and return the settled state with it,
            instead of answering as soon as the action was dispatched. Saves a
            follow-up get_game_state(). The reply adds `settled`, `changed`,
            `waited_ms`, `polls` and `state`.
    """
    try:
        return await _post({"action": "end_turn"}, wait=wait)
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# In-Combat Card Selection (state_type: hand_select)
# ---------------------------------------------------------------------------


@mcp.tool()
async def combat_select_card(card_index: int) -> str:
    """[Combat Selection] Select a card from hand during an in-combat card selection prompt.

    Used when a card effect asks you to select a card to exhaust, discard, etc.
    This is different from deck_select_card which handles out-of-combat card selection overlays.

    Args:
        card_index: 0-based index of the card in the selectable hand cards (as shown in game state).
    """
    try:
        return await _post({"action": "combat_select_card", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def combat_confirm_selection() -> str:
    """[Combat Selection] Confirm the in-combat card selection.

    After selecting the required number of cards from hand (exhaust, discard, etc.),
    use this to confirm the selection. Only works when the confirm button is enabled.
    """
    try:
        return await _post({"action": "combat_confirm_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Rewards (state_type: rewards / card_reward)
# ---------------------------------------------------------------------------


@mcp.tool()
async def rewards_claim(reward_index: int) -> str:
    """[Rewards] Claim a reward from the post-combat rewards screen.

    Gold, potion, and relic rewards are claimed immediately.
    Card rewards open the card selection screen (state changes to card_reward).
    Only visible enabled rewards are actionable.

    Args:
        reward_index: 0-based index of the reward on the rewards screen.

    Note that claiming a reward may change the indices of remaining rewards, so refer to the latest game state for accurate indices.
    Claiming from right to left can help maintain more stable indices for remaining rewards, as rewards will always shift left to fill in gaps.
    """
    try:
        return await _post({"action": "claim_reward", "index": reward_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def rewards_pick_card(card_index: int) -> str:
    """[Rewards] Select a card from the card reward selection screen.

    Args:
        card_index: 0-based index of the card to add to the deck.
    """
    try:
        return await _post({"action": "select_card_reward", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def rewards_skip_card() -> str:
    """[Rewards] Skip the card reward without selecting a card.

    Skips specifically — it picks the Skip option by id, not whichever button is
    first. If a relic added its own alternative (see rewards_card_alternative),
    skipping does NOT take it.
    """
    try:
        return await _post({"action": "skip_card_reward"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def rewards_card_alternative(option_id: str) -> str:
    """[Rewards] Take one of the card reward's alternative options.

    The options are listed in game state under card_reward.alternatives. "Skip" is
    one of them; relics add others — Pael's Wing adds "SACRIFICE", which turns the
    reward into progress towards a relic. Skipping does not count as sacrificing,
    so use this rather than rewards_skip_card when you mean to take the relic's
    option.

    Args:
        option_id: The option_id from card_reward.alternatives (e.g. "SACRIFICE").
    """
    try:
        return await _post({"action": "select_card_reward_alternative", "option_id": option_id})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Map (state_type: map)
# ---------------------------------------------------------------------------


@mcp.tool()
async def get_map_drawings() -> str:
    """Get saved map drawing strokes and live map-node drawing positions.

    This uses the dedicated /api/v1/mapdrawings endpoint; drawing data is not
    included in the normal game-state response. Coordinates use the game's
    resolution-independent map_normalized space.
    """
    try:
        return await _map_drawings_get()
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def map_choose_node(node_index: int) -> str:
    """[Map] Choose a map node to travel to.

    Only visible travelable nodes from next_options are actionable.

    Args:
        node_index: 0-based index of the node from the next_options list.
    """
    try:
        return await _post({"action": "choose_map_node", "index": node_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def map_draw(strokes: list[dict]) -> str:
    """[Map] Draw or erase one or more programmatic strokes on the open map.

    Each stroke is {"mode": "draw"|"erase", "points": [...]}. A point may
    be a normalized coordinate {"x": number, "y": number} or a live map-node
    reference {"col": int, "row": int}. Node anchors are included as
    draw_position on map.nodes and by get_map_drawings().

    Args:
        strokes: Up to 64 strokes and 2048 total points.
    """
    try:
        return await _post({"action": "map_draw", "strokes": strokes})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def map_clear_drawings() -> str:
    """[Map] Clear all map drawing strokes belonging to the local player."""
    try:
        return await _post({"action": "map_clear_drawings"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Rest Site (state_type: rest_site)
# ---------------------------------------------------------------------------


@mcp.tool()
async def rest_choose_option(
    option_index: int | None = None,
    option_id: str | None = None,
) -> str:
    """[Rest Site] Choose a rest site option (rest, smith, dig, etc.).

    Prefer option_id: relics add options (Shovel's DIG, Girya, the Byrdonis egg),
    so the id says what you meant while a bare index is only meaningful against
    the state read you took it from.

    Only visible enabled options are actionable.

    Args:
        option_id: The id from rest_site.options[].id (e.g. "HEAL", "SMITH", "DIG").
        option_index: 0-based rest_site.options[].index, if you have no id.
    """
    payload: dict = {"action": "choose_rest_option"}
    if option_id is not None:
        payload["option_id"] = option_id
    elif option_index is not None:
        payload["index"] = option_index
    try:
        return await _post(payload)
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Shop (state_type: shop)
# ---------------------------------------------------------------------------


@mcp.tool()
async def shop_open_inventory() -> str:
    """[Shop / Fake Merchant] Open the shop inventory.

    State queries never open shop UI. Call this before purchasing after any
    out-of-combat potion actions that require the inventory to remain closed.
    """
    try:
        return await _post({"action": "shop_open"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def shop_purchase(item_index: int) -> str:
    """[Shop / Fake Merchant] Purchase an item from the shop.

    Works for both regular shops (state_type: shop) and the fake merchant
    event (state_type: fake_merchant). Call shop_open_inventory first. The fake
    merchant only sells relics.

    Args:
        item_index: 0-based index of the item from the shop state.
    """
    try:
        return await _post({"action": "shop_purchase", "index": item_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Event (state_type: event)
# ---------------------------------------------------------------------------


@mcp.tool()
async def event_choose_option(option_index: int) -> str:
    """[Event] Choose an event option.

    Works for both regular events and ancients (after dialogue ends).
    Also used to click the Proceed option after an event resolves.
    Locked or disabled options are reported as errors.

    Args:
        option_index: 0-based index of the option from the event state.
    """
    try:
        return await _post({"action": "choose_event_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def event_advance_dialogue() -> str:
    """[Event] Advance ancient event dialogue.

    Click through dialogue text in ancient events. Call repeatedly until options appear.
    """
    try:
        return await _post({"action": "advance_dialogue"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Card Selection (state_type: card_select)
# ---------------------------------------------------------------------------


@mcp.tool()
async def deck_select_card(card_index: int) -> str:
    """[Card Selection] Select or deselect a card in the card selection screen.

    Used when the game asks you to choose cards from your deck (transform, upgrade,
    enchant, remove, discard) or pick a card from offered choices (potions, effects).

    For deck selections: toggles card selection - a card that is already selected comes
    back out. The result reports `selected` and `selected_count`; game state carries
    `cards[].selected`, `selected_count` and `required_count`, so there is no need to
    count your own presses. For choose-a-card: picks immediately.

    Once `max_select` cards are selected the screen opens its preview by itself, and
    further selecting is refused until you confirm or cancel it (a preview open
    blocks toggling).

    Args:
        card_index: 0-based index of the card (as shown in game state).
    """
    try:
        return await _post({"action": "select_card", "index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def deck_confirm_selection() -> str:
    """[Card Selection] Confirm the current card selection.

    These screens have two steps. With a preview showing, this applies the selection.
    Without one, it presses the screen's own confirm, which usually just opens the
    preview - so a screen may need two calls; the message says which step happened.

    Errors instead of silently doing nothing when the count does not match the screen's
    min/max. Not needed for choose-a-card screens where picking is immediate.
    """
    try:
        return await _post({"action": "confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def deck_cancel_selection() -> str:
    """[Card Selection] Cancel the current card selection.

    If a preview is showing, goes back to the selection grid AND clears the selection -
    this is the way out of a preview that was opened with the wrong cards.
    For choose-a-card screens, clicks the skip button (if available).
    Otherwise, closes the card selection screen (only if cancellation is allowed).
    """
    try:
        return await _post({"action": "cancel_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Bundle Selection (state_type: bundle_select)
# ---------------------------------------------------------------------------


@mcp.tool()
async def bundle_select(bundle_index: int) -> str:
    """[Bundle Selection] Open a bundle preview.

    Only visible enabled bundles are actionable.

    Args:
        bundle_index: 0-based index of the bundle.
    """
    try:
        return await _post({"action": "select_bundle", "index": bundle_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def bundle_confirm_selection() -> str:
    """[Bundle Selection] Confirm the currently previewed bundle."""
    try:
        return await _post({"action": "confirm_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def bundle_cancel_selection() -> str:
    """[Bundle Selection] Cancel the current bundle preview."""
    try:
        return await _post({"action": "cancel_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Relic Selection (state_type: relic_select)
# ---------------------------------------------------------------------------


@mcp.tool()
async def relic_select(relic_index: int) -> str:
    """[Relic Selection] Select a relic from the relic selection screen.

    Used when the game offers a choice of relics (e.g., boss relic rewards).
    Only visible enabled relic choices are actionable.

    Args:
        relic_index: 0-based index of the relic (as shown in game state).
    """
    try:
        return await _post({"action": "select_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def relic_skip() -> str:
    """[Relic Selection] Skip when the skip button is visible and enabled."""
    try:
        return await _post({"action": "skip_relic_selection"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Treasure (state_type: treasure)
# ---------------------------------------------------------------------------


@mcp.tool()
async def treasure_claim_relic(relic_index: int) -> str:
    """[Treasure] Claim a relic from the treasure chest.

    The chest is auto-opened when entering the treasure room.
    After claiming, use proceed_to_map() to continue.
    Only visible enabled relics are actionable.

    Args:
        relic_index: 0-based index of the relic (as shown in game state).
    """
    try:
        return await _post({"action": "claim_treasure_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Crystal Sphere (state_type: crystal_sphere)
# ---------------------------------------------------------------------------


@mcp.tool()
async def crystal_sphere_set_tool(tool: str) -> str:
    """[Crystal Sphere] Switch the active divination tool.

    Args:
        tool: Either "big" or "small".
    """
    try:
        return await _post({"action": "crystal_sphere_set_tool", "tool": tool})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def crystal_sphere_click_cell(x: int, y: int) -> str:
    """[Crystal Sphere] Click a hidden cell on the Crystal Sphere grid.

    Args:
        x: Cell x-coordinate.
        y: Cell y-coordinate.
    """
    try:
        return await _post({"action": "crystal_sphere_click_cell", "x": x, "y": y})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def crystal_sphere_proceed() -> str:
    """[Crystal Sphere] Continue after the Crystal Sphere minigame finishes."""
    try:
        return await _post({"action": "crystal_sphere_proceed"})
    except Exception as e:
        return _handle_error(e)


# ===========================================================================
# MULTIPLAYER tools — all route through /api/v1/multiplayer
# ===========================================================================


@mcp.tool()
async def mp_get_game_state(format: str = "markdown", values: str = "powered") -> str:
    """[Multiplayer] Get the current multiplayer game state.

    Returns a summary of all players (HP, gold, alive status) plus full
    detail for the local player (relics, potions, deck, etc.), along with
    multiplayer-specific data: map votes, event votes, treasure bids,
    end-turn ready status. Only works during a multiplayer run.

    Args:
        format: "markdown" for human-readable output, "json" for structured data.
        values: "powered" (default) renders card descriptions and enemy intent
            labels the way the game does, with the player's standing Strength,
            Dexterity, Vigor, Focus, Weak and relic bonuses already applied to the
            numbers. "unpowered" strips that back to the card's own base value plus
            its enchantment, and marks each card `text_baked: false` and each
            rebuilt intent `unbaked: true`. Keywords, enchantment text and energy
            cost are identical either way.
    """
    try:
        return await _mp_get({"format": format, "values": values})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_play_card(card_index: int, target: str | None = None) -> str:
    """[Multiplayer Combat] Play a card from the local player's hand.

    Same as singleplayer combat_play_card but routed through the multiplayer
    endpoint for sync safety.

    Args:
        card_index: Index of the card in hand (0-based).
        target: Entity ID from the hand card's `valid_targets` list, or the target combat_id as a string.
            Required when `requires_target` is true.
    """
    body: dict = {"action": "play_card", "card_index": card_index}
    if target is not None:
        body["target"] = target
    try:
        return await _mp_post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_end_turn() -> str:
    """[Multiplayer Combat] Submit end-turn vote.

    In multiplayer, ending the turn is a VOTE — the turn only ends when ALL
    players have submitted. Use battle can_end_turn/end_turn_blocked_reason
    to check local readiness. Use mp_combat_undo_end_turn() to retract.
    """
    try:
        return await _mp_post({"action": "end_turn"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_undo_end_turn() -> str:
    """[Multiplayer Combat] Retract end-turn vote.

    If you submitted end turn but want to play more cards, use this to undo.
    Use battle can_undo_end_turn/undo_end_turn_blocked_reason to check readiness.
    Only works if other players haven't all committed yet.
    """
    try:
        return await _mp_post({"action": "undo_end_turn"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_use_potion(slot: int, target: str | None = None) -> str:
    """[Multiplayer] Use a potion from the local player's potion slots.

    Enemy-targeted potions require combat and a target from state `valid_targets`.

    Args:
        slot: Potion slot index (as shown in game state).
        target: Entity ID of the target enemy, or the target combat_id as a string.
            Required for enemy-targeted potions.
    """
    body: dict = {"action": "use_potion", "slot": slot}
    if target is not None:
        body["target"] = target
    try:
        return await _mp_post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_discard_potion(slot: int) -> str:
    """[Multiplayer] Discard a potion from the local player's potion slots to free up space.

    Queued potions cannot be discarded.

    Args:
        slot: Potion slot index to discard (as shown in game state).
    """
    try:
        return await _mp_post({"action": "discard_potion", "slot": slot})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_map_vote(node_index: int) -> str:
    """[Multiplayer Map] Vote for a map node to travel to.

    In multiplayer, map selection is a vote — travel happens when all players
    agree. Re-voting for the same node sends a ping to other players.
    Only visible travelable nodes from next_options are actionable.

    Args:
        node_index: 0-based index of the node from the next_options list.
    """
    try:
        return await _mp_post({"action": "choose_map_node", "index": node_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_map_draw(strokes: list[dict]) -> str:
    """[Multiplayer Map] Draw or erase strokes and broadcast them to peers.

    Uses the same stroke schema and normalized/node coordinates as map_draw.
    Existing local and remote strokes can be read with get_map_drawings().

    Args:
        strokes: Up to 64 strokes and 2048 total points.
    """
    try:
        return await _mp_post({"action": "map_draw", "strokes": strokes})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_map_clear_drawings() -> str:
    """[Multiplayer Map] Clear local strokes and broadcast the clear to peers."""
    try:
        return await _mp_post({"action": "map_clear_drawings"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_event_choose_option(option_index: int) -> str:
    """[Multiplayer Event] Choose or vote for an event option.

    For shared events: this is a vote (resolves when all players vote).
    For individual events: immediate choice, same as singleplayer.
    Locked or disabled options are reported as errors.

    Args:
        option_index: 0-based index of the option from the event state.
    """
    try:
        return await _mp_post({"action": "choose_event_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_event_advance_dialogue() -> str:
    """[Multiplayer Event] Advance ancient event dialogue."""
    try:
        return await _mp_post({"action": "advance_dialogue"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_rest_choose_option(option_index: int) -> str:
    """[Multiplayer Rest Site] Choose a rest site option (rest, smith, etc.).

    Only visible enabled options are actionable.

    Per-player choice — no voting needed.

    Args:
        option_index: 0-based index of the option.
    """
    try:
        return await _mp_post({"action": "choose_rest_option", "index": option_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_shop_open_inventory() -> str:
    """[Multiplayer Shop] Open the local player's shop inventory."""
    try:
        return await _mp_post({"action": "shop_open"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_shop_purchase(item_index: int) -> str:
    """[Multiplayer Shop] Purchase an item from the shop.

    Call mp_shop_open_inventory first. Per-player inventory — no voting needed.

    Args:
        item_index: 0-based index of the item.
    """
    try:
        return await _mp_post({"action": "shop_purchase", "index": item_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_rewards_claim(reward_index: int) -> str:
    """[Multiplayer Rewards] Claim a reward from the post-combat rewards screen.

    Only visible enabled rewards are actionable.

    Args:
        reward_index: 0-based index of the reward.
    """
    try:
        return await _mp_post({"action": "claim_reward", "index": reward_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_rewards_pick_card(card_index: int) -> str:
    """[Multiplayer Rewards] Select a card from the card reward screen.

    Args:
        card_index: 0-based index of the card to add to the deck.
    """
    try:
        return await _mp_post({"action": "select_card_reward", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_rewards_skip_card() -> str:
    """[Multiplayer Rewards] Skip the card reward."""
    try:
        return await _mp_post({"action": "skip_card_reward"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_proceed_to_map() -> str:
    """[Multiplayer] Proceed from the current screen to the map.

    Works from: rewards screen, rest site, shop, fake merchant, and treasure.
    """
    try:
        return await _mp_post({"action": "proceed"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_deck_select_card(card_index: int) -> str:
    """[Multiplayer Card Selection] Select or deselect a card in the card selection screen.

    For deck selections: toggles card selection unless a preview is open. For
    choose-a-card: picks immediately.

    Args:
        card_index: 0-based index of the card.
    """
    try:
        return await _mp_post({"action": "select_card", "index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_deck_confirm_selection() -> str:
    """[Multiplayer Card Selection] Confirm the current card selection."""
    try:
        return await _mp_post({"action": "confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_deck_cancel_selection() -> str:
    """[Multiplayer Card Selection] Cancel the current card selection."""
    try:
        return await _mp_post({"action": "cancel_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_bundle_select(bundle_index: int) -> str:
    """[Multiplayer Bundle Selection] Open a bundle preview.

    Only visible enabled bundles are actionable.

    Args:
        bundle_index: 0-based index of the bundle.
    """
    try:
        return await _mp_post({"action": "select_bundle", "index": bundle_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_bundle_confirm_selection() -> str:
    """[Multiplayer Bundle Selection] Confirm the currently previewed bundle."""
    try:
        return await _mp_post({"action": "confirm_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_bundle_cancel_selection() -> str:
    """[Multiplayer Bundle Selection] Cancel the current bundle preview."""
    try:
        return await _mp_post({"action": "cancel_bundle_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_select_card(card_index: int) -> str:
    """[Multiplayer Combat Selection] Select a card from hand during in-combat card selection.

    Args:
        card_index: 0-based index of the card in the selectable hand cards.
    """
    try:
        return await _mp_post({"action": "combat_select_card", "card_index": card_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_combat_confirm_selection() -> str:
    """[Multiplayer Combat Selection] Confirm the in-combat card selection."""
    try:
        return await _mp_post({"action": "combat_confirm_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_relic_select(relic_index: int) -> str:
    """[Multiplayer Relic Selection] Select a relic (boss relic rewards).

    Only visible enabled relic choices are actionable.

    Args:
        relic_index: 0-based index of the relic.
    """
    try:
        return await _mp_post({"action": "select_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_relic_skip() -> str:
    """[Multiplayer Relic Selection] Skip when the skip button is visible and enabled."""
    try:
        return await _mp_post({"action": "skip_relic_selection"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_treasure_claim_relic(relic_index: int) -> str:
    """[Multiplayer Treasure] Bid on / claim a relic from the treasure chest.

    In multiplayer, this is a bid — if multiple players pick the same relic,
    a "relic fight" determines the winner. Others get consolation prizes.
    Only visible enabled relics are actionable.

    Args:
        relic_index: 0-based index of the relic.
    """
    try:
        return await _mp_post({"action": "claim_treasure_relic", "index": relic_index})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_crystal_sphere_set_tool(tool: str) -> str:
    """[Multiplayer Crystal Sphere] Switch the active divination tool.

    Args:
        tool: Either "big" or "small".
    """
    try:
        return await _mp_post({"action": "crystal_sphere_set_tool", "tool": tool})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_crystal_sphere_click_cell(x: int, y: int) -> str:
    """[Multiplayer Crystal Sphere] Click a hidden cell on the Crystal Sphere grid.

    Args:
        x: Cell x-coordinate.
        y: Cell y-coordinate.
    """
    try:
        return await _mp_post({"action": "crystal_sphere_click_cell", "x": x, "y": y})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def mp_crystal_sphere_proceed() -> str:
    """[Multiplayer Crystal Sphere] Continue after the Crystal Sphere minigame finishes."""
    try:
        return await _mp_post({"action": "crystal_sphere_proceed"})
    except Exception as e:
        return _handle_error(e)


# ---------------------------------------------------------------------------
# Session and debug actions absorbed from the active forks.
# ---------------------------------------------------------------------------


@mcp.tool()
async def return_to_main_menu(wait: bool = False) -> str:
    """Save the current run and return to the main menu.

    Unlike open_pause_menu + give_up, this keeps the run on disk, so `continue`
    can resume it later.
    """
    try:
        return await _post({"action": "return_to_main_menu"}, wait=wait)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def set_time_scale(scale: float) -> str:
    """Set the Godot engine time scale (animation and game speed).

    Args:
        scale: Multiplier between 0.25 and 10.0. 1.0 is normal speed; values above
            1.0 fast-forward animations, which shortens the settle time a `wait`
            request has to cover.
    """
    if not 0.25 <= scale <= 10.0:
        return "Error: scale must be between 0.25 and 10.0."
    try:
        return await _post({"action": "set_time_scale", "scale": scale})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def set_ascension(level: int) -> str:
    """Set the run ascension level on the open character-select screen.

    Args:
        level: Ascension level to select. The cap is per character and is reported
            as `max_ascension` in the character-select state; asking for more is an
            error. Select the character first.
    """
    if level < 0:
        return "Error: level must be >= 0."
    try:
        return await _post({"action": "set_ascension", "level": level})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_start_encounter(encounter: str) -> str:
    """[Debug] Start the named encounter immediately, skipping map navigation.

    Args:
        encounter: Encounter id, for example "JAW_WORM_NORMAL". The state names the
            available ids under `combat_pools` when they are exposed.
    """
    try:
        return await _post({"action": "debug_start_encounter", "encounter": encounter})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_start_event(event: str) -> str:
    """[Debug] Enter the named event.

    Args:
        event: Event id as used by the game's event registry.
    """
    try:
        return await _post({"action": "debug_start_event", "event": event})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_start_shop() -> str:
    """[Debug] Enter a shop room immediately."""
    try:
        return await _post({"action": "debug_start_shop"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_force_play_phase() -> str:
    """[Debug] Force the current combat into the player's play phase."""
    try:
        return await _post({"action": "debug_force_play_phase"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_add_card(card: str, pile: str | None = None) -> str:
    """[Debug] Add a card to a pile.

    Args:
        card: Card id, for example "STRIKE_IRONCLAD".
        pile: Target pile name (hand, draw, discard, exhaust). Defaults to the
            hand when omitted.
    """
    body: dict = {"action": "debug_add_card", "card": card}
    if pile is not None:
        body["pile"] = pile
    try:
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_add_power(power: str, amount: int, target: str | None = None) -> str:
    """[Debug] Apply a power to a creature.

    Args:
        power: Power id as used by the game.
        amount: Stack amount to apply.
        target: entity_id or combat_id; defaults to the player.
    """
    body: dict = {"action": "debug_add_power", "power": power, "amount": amount}
    if target is not None:
        body["target"] = target
    try:
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_enchant_card(enchantment: str, pile: str | None = None) -> str:
    """[Debug] Enchant a card in the given pile.

    Args:
        enchantment: Enchantment id as used by the game.
        pile: Pile holding the target card. Defaults to the hand when omitted.
    """
    body: dict = {"action": "debug_enchant_card", "enchantment": enchantment}
    if pile is not None:
        body["pile"] = pile
    try:
        return await _post(body)
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_set_energy(amount: int) -> str:
    """[Debug] Set the player's energy to an exact value."""
    try:
        return await _post({"action": "debug_set_energy", "amount": amount})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_set_stars(amount: int) -> str:
    """[Debug] Set the player's stars to an exact value (the Regent resource).

    Useful for staging a card whose star cost would otherwise be unreachable.
    """
    try:
        return await _post({"action": "debug_set_stars", "amount": amount})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_gain_max_hp(amount: int) -> str:
    """[Debug] Raise the player's max HP by the given amount."""
    try:
        return await _post({"action": "debug_gain_max_hp", "amount": amount})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_upgrade_deck() -> str:
    """[Debug] Upgrade every card in the current deck."""
    try:
        return await _post({"action": "debug_upgrade_deck"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def debug_enter_next_act() -> str:
    """[Debug] Advance the run to the next act."""
    try:
        return await _post({"action": "debug_enter_next_act"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def set_recording(enabled: bool) -> str:
    """Turn the combat recorder on or off for this session.

    Args:
        enabled: True to record, false to stop. The recorder writes into the
            directory reported by the state's recording fields.
    """
    try:
        return await _post({"action": "set_recording", "enabled": enabled})
    except Exception as e:
        return _handle_error(e)


def main():
    parser = argparse.ArgumentParser(description="STS2 MCP Server")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="Game HTTP server port")
    parser.add_argument("--host", type=str, default=DEFAULT_HOST, help="Game HTTP server host")
    parser.add_argument("--no-trust-env", action="store_true", help="Ignore HTTP_PROXY/HTTPS_PROXY environment variables")
    args = parser.parse_args()

    global _base_url, _trust_env
    _base_url = f"http://{args.host}:{args.port}"
    _trust_env = not args.no_trust_env

    # Eagerly initialize the shared httpx client so the first request is fast
    _get_client()

    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
