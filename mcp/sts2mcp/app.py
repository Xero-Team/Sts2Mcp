"""Shared FastMCP application instance.

Tool modules register themselves on this object with ``@mcp.tool()``.
"""

from mcp.server.fastmcp import FastMCP

mcp = FastMCP("sts2")
