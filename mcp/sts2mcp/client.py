"""HTTP transport for the Sts2Mcp mod's localhost API.

Every route helper here performs exactly one request and returns the raw
response body. Parsing and game rules stay in the mod; the Python layer only
adds MCP-friendly defaults and error rendering.
"""

import asyncio
import json
import os

import httpx

DEFAULT_HOST = os.environ.get("STS2_HOST", "localhost")
DEFAULT_PORT = int(os.environ.get("STS2_PORT", "15526"))

_base_url = f"http://{DEFAULT_HOST}:{DEFAULT_PORT}"
_trust_env = True
_http: httpx.AsyncClient | None = None


def configure(host: str, port: int, trust_env: bool = True) -> None:
    """Point the bridge at another host/port and drop any cached client."""
    global _base_url, _trust_env, _http
    _base_url = f"http://{host}:{port}"
    _trust_env = trust_env
    _http = None


def _sp_url() -> str:
    return f"{_base_url}/api/v1/singleplayer"


def _root_url() -> str:
    return f"{_base_url}/"


def _mp_url() -> str:
    return f"{_base_url}/api/v1/multiplayer"


def _profile_url() -> str:
    return f"{_base_url}/api/v1/profile"


def _compendium_url() -> str:
    return f"{_base_url}/api/v1/compendium"


def _wiki_url() -> str:
    return f"{_base_url}/api/v1/wiki"


def _settings_url() -> str:
    return f"{_base_url}/api/v1/settings"


def _bestiary_url() -> str:
    return f"{_base_url}/api/v1/bestiary"


def _glossary_url(kind: str) -> str:
    return f"{_base_url}/api/v1/glossary/{kind}"


def _profiles_url() -> str:
    return f"{_base_url}/api/v1/profiles"


def _map_drawings_url() -> str:
    return f"{_base_url}/api/v1/mapdrawings"


def _get_client() -> httpx.AsyncClient:
    global _http
    if _http is None:
        # Must exceed the mod's own 10 s main-thread timeout, otherwise the client gives up at
        # the same moment the mod is about to answer 503 and the caller never learns that the
        # request was NOT executed (and is therefore safe to retry).
        _http = httpx.AsyncClient(timeout=httpx.Timeout(15), trust_env=_trust_env)
    return _http


async def _get(params: dict | None = None) -> str:
    r = await _get_client().get(_sp_url(), params=params)
    r.raise_for_status()
    return r.text


async def _root_get() -> str:
    r = await _get_client().get(_root_url())
    r.raise_for_status()
    return r.text


async def _post(body: dict, wait: bool = False) -> str:
    """POST a singleplayer action.

    With wait=True the mod holds the response until the game has settled and embeds the
    settled state, so the caller does not need a GET poll loop. That makes the request
    outlive the shared 15 s client timeout, hence the per-request override: the mod's own
    settle timeout is 10 s on top of the 10 s main-thread timeout.
    """
    if not wait:
        r = await _get_client().post(_sp_url(), json=body)
    else:
        r = await _get_client().post(
            _sp_url(), json={**body, "wait": True}, timeout=httpx.Timeout(35)
        )
    r.raise_for_status()
    return r.text


async def _mp_get(params: dict | None = None) -> str:
    r = await _get_client().get(_mp_url(), params=params)
    r.raise_for_status()
    return r.text


async def _mp_post(body: dict) -> str:
    r = await _get_client().post(_mp_url(), json=body)
    r.raise_for_status()
    return r.text


async def _profile_get() -> str:
    r = await _get_client().get(_profile_url())
    r.raise_for_status()
    return r.text


async def _compendium_get() -> str:
    r = await _get_client().get(_compendium_url())
    r.raise_for_status()
    return r.text


async def _wiki_get(
    query: str, item_type: str = "all", limit: int | None = None, scope: str = "discovered"
) -> str:
    params: dict[str, object] = {"query": query, "item_type": item_type, "scope": scope}
    if limit is not None:
        params["limit"] = limit
    r = await _get_client().get(_wiki_url(), params=params)
    r.raise_for_status()
    return r.text


async def _settings_get() -> str:
    r = await _get_client().get(_settings_url())
    r.raise_for_status()
    return r.text


async def _bestiary_get() -> str:
    r = await _get_client().get(_bestiary_url())
    r.raise_for_status()
    return r.text


async def _glossary_get(kind: str) -> str:
    r = await _get_client().get(_glossary_url(kind))
    r.raise_for_status()
    return r.text


async def _profiles_get() -> str:
    r = await _get_client().get(_profiles_url())
    r.raise_for_status()
    return r.text


async def _profiles_post(body: dict) -> str:
    r = await _get_client().post(_profiles_url(), json=body)
    r.raise_for_status()
    return r.text


async def _map_drawings_get() -> str:
    r = await _get_client().get(_map_drawings_url())
    r.raise_for_status()
    return r.text


async def _wait_for_profile(profile_id: int, fallback: str) -> str:
    last_profiles: dict | None = None
    for _ in range(30):
        await asyncio.sleep(0.1)
        profiles_text = await _profiles_get()
        profiles = json.loads(profiles_text)
        last_profiles = profiles
        if profiles.get("current_profile_id") == profile_id:
            return json.dumps(
                {
                    "status": "ok",
                    "message": f"Switched to profile {profile_id}",
                    "current_profile_id": profile_id,
                    "profiles": profiles.get("profiles", []),
                },
                indent=2,
            )

    return json.dumps(
        {
            "status": "error",
            "error": f"Timed out waiting for profile {profile_id} to become active",
            "current_profile_id": (
                last_profiles.get("current_profile_id") if isinstance(last_profiles, dict) else None
            ),
            "profiles": (
                last_profiles.get("profiles", []) if isinstance(last_profiles, dict) else []
            ),
            "initial_response": fallback,
        },
        indent=2,
    )
