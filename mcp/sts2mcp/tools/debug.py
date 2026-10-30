"""Session recorder and debug sandbox actions."""

from ..app import mcp
from ..client import _post
from ..errors import _handle_error


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
