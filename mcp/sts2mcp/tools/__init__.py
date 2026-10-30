"""Every MCP tool module.

Importing this package registers all tools on the shared FastMCP instance.
"""

from . import (
    combat,
    debug,
    general,
    map,
    multiplayer,
    profiles,
    rewards,
    rooms,
    selection,
)

__all__ = [
    "combat",
    "debug",
    "general",
    "map",
    "multiplayer",
    "profiles",
    "rewards",
    "rooms",
    "selection",
]
