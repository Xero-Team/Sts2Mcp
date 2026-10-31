"""MCP server bridge for Slay the Spire 2.

The package is split by concern:

* :mod:`sts2mcp.app` holds the shared FastMCP instance.
* :mod:`sts2mcp.client` owns the localhost HTTP transport.
* :mod:`sts2mcp.errors` renders endpoint failures for MCP callers.
* :mod:`sts2mcp.tools` registers one module per game screen or action family.
* :mod:`sts2mcp.telemetry` aggregates the local playtest ledger.

``server.py`` at the repository's ``mcp/`` root is the executable entry point.
"""
