"""Startup, menu, and run-wide actions."""

from ..app import mcp
from ..client import _get, _post, _root_get
from ..errors import _handle_error, _menu_select_post


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
