"""MCP-facing error rendering and cross-mode menu fallback."""

import json

import httpx

from .client import _mp_post, _post


def _handle_error(e: Exception) -> str:
    if isinstance(e, httpx.ConnectError):
        return "Error: Cannot connect to Sts2Mcp mod. Is the game running with the mod enabled?"
    if isinstance(e, httpx.HTTPStatusError):
        structured = _format_structured_http_error(e.response)
        if structured is not None:
            return structured
        return f"Error: HTTP {e.response.status_code} — {e.response.text}"
    return f"Error: {e}"


def _format_structured_http_error(response: httpx.Response) -> str | None:
    try:
        data = response.json()
    except ValueError:
        return None

    if not isinstance(data, dict) or data.get("status") != "error":
        return None

    payload = dict(data)
    payload.setdefault("http_status", response.status_code)
    return json.dumps(payload, indent=2)


def _response_error_code(response: httpx.Response) -> str | None:
    try:
        data = response.json()
    except ValueError:
        return None
    return data.get("error_code") if isinstance(data, dict) else None


async def _menu_select_post(body: dict) -> str:
    try:
        return await _post(body)
    except httpx.HTTPStatusError as e:
        if (
            e.response.status_code == 409
            and _response_error_code(e.response) == "multiplayer_run_active"
        ):
            return await _mp_post(body)
        raise
