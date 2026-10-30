"""Post-combat reward screen actions."""

from ..app import mcp
from ..client import _post
from ..errors import _handle_error


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
