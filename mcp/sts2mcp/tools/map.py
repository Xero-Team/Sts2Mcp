"""Map navigation and map drawing actions."""

from ..app import mcp
from ..client import _map_drawings_get, _post
from ..errors import _handle_error


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
