"""Rest site, shop, and event screen actions."""

from ..app import mcp
from ..client import _post
from ..errors import _handle_error


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
