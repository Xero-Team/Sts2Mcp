"""Combat turn and in-combat card selection actions."""

from ..app import mcp
from ..client import _post
from ..errors import _handle_error


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
