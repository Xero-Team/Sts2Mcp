"""MCP server bridge for Slay the Spire 2.

Connects to the Sts2Mcp mod's HTTP server and exposes game actions
as MCP tools for Claude Desktop / Claude Code.

This module is the executable entry point; the implementation lives in the
``sts2mcp`` package next to it.
"""

import argparse

import sts2mcp.tools  # noqa: F401  # registers every @mcp.tool on the shared app
from sts2mcp.app import mcp
from sts2mcp.client import DEFAULT_HOST, DEFAULT_PORT, _get_client, configure


def main() -> None:
    parser = argparse.ArgumentParser(description="STS2 MCP Server")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="Game HTTP server port")
    parser.add_argument("--host", type=str, default=DEFAULT_HOST, help="Game HTTP server host")
    parser.add_argument(
        "--no-trust-env",
        action="store_true",
        help="Ignore HTTP_PROXY/HTTPS_PROXY environment variables",
    )
    args = parser.parse_args()

    configure(args.host, args.port, trust_env=not args.no_trust_env)

    # Eagerly initialize the shared httpx client so the first request is fast.
    _get_client()

    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
