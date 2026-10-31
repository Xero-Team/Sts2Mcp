#!/usr/bin/env python3
"""Regression cases for the extractors in scripts/audit_endpoints.py.

The audit reads the C# transport as text, so its extractors are the part that can
silently stop seeing a route after a refactor. Two failures are covered here:

- a spelling change (`x == "v"` -> `string.Equals(x, "v", ...)`, the MA0006
  sweep) must not hide a comparison;
- a structural change (standalone `if` blocks instead of `else if`, a
  `SendGetOnly` wrapper, a route that delegates to a `HandleXxx` member) must
  still be resolved to the method it serves.

Needs no game and no packages:

    /usr/bin/python3 scripts/test_audit_endpoints.py
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import audit_endpoints  # noqa: E402


def test_equality_both_spellings() -> None:
    direct = 'if (path == "/api/v1/singleplayer")'
    equals = 'if (string.Equals(path, "/api/v1/singleplayer", StringComparison.Ordinal))'

    assert audit_endpoints.extract_equality_values(direct, "path") == {"/api/v1/singleplayer"}
    assert audit_endpoints.extract_equality_values(equals, "path") == {"/api/v1/singleplayer"}
    assert audit_endpoints.has_equality(direct, "path", "/api/v1/singleplayer")
    assert audit_endpoints.has_equality(equals, "path", "/api/v1/singleplayer")
    assert audit_endpoints.find_equality_positions(equals, "path", "/api/v1/singleplayer")
    assert not audit_endpoints.has_equality(direct, "path", "/api/v1/multiplayer")


def test_equality_is_exact_and_name_bounded() -> None:
    # /api/v1/profile must not be satisfied by a /api/v1/profiles route.
    profiles = 'if (string.Equals(path, "/api/v1/profiles", StringComparison.Ordinal))'
    assert audit_endpoints.has_equality(profiles, "path", "/api/v1/profiles")
    assert not audit_endpoints.has_equality(profiles, "path", "/api/v1/profile")

    # A different identifier that merely ends with the name must not match.
    other = 'if (string.Equals(actionName, "menu_select", StringComparison.Ordinal))'
    assert not audit_endpoints.has_equality(other, "action", "menu_select")
    assert not audit_endpoints.has_equality('if (obj.action == "menu_select")', "action", "menu_select")
    assert audit_endpoints.has_equality('if (action == "menu_select")', "action", "menu_select")


def test_route_block_old_else_if_chain() -> None:
    source = "\n".join(
        [
            'if (path == "/")',
            "{",
            "    return;",
            "}",
            'else if (path == "/api/v1/profile")',
            "{",
            '    if (request.HttpMethod == "GET")',
            "        HandleGetProfile(request, response);",
            "    else",
            '        SendError(response, 405, "Method not allowed");',
            "}",
            'else if (path == "/api/v1/compendium")',
            "{",
            "    HandleGetCompendium(response);",
            "}",
        ]
    )

    block = audit_endpoints.extract_route_block(source, "/api/v1/profile")
    assert block is not None
    assert 'request.HttpMethod == "GET"' in block
    assert 'HandleGetProfile(request, response);' in block
    # The next route is not part of this block.
    assert "/api/v1/compendium" not in block
    assert audit_endpoints.extract_route_block(source, "/api/v1/missing") is None


def test_route_block_standalone_if_statements() -> None:
    source = "\n".join(
        [
            "private static bool DispatchReadOnlyEndpoint(string path, HttpListenerRequest request, HttpListenerResponse response)",
            "{",
            '    if (string.Equals(path, "/api/v1/profile", StringComparison.Ordinal))',
            "        SendGetOnly(request, response, () => HandleGetProfile(request, response));",
            '    else if (string.Equals(path, "/api/v1/profiles", StringComparison.Ordinal))',
            "        SendGetOnly(request, response, () => HandleProfilesEndpoint(request, response));",
            "}",
            "",
            "internal static void DispatchPostAction()",
            "{",
            '    if (string.Equals(path, "/api/v1/singleplayer", StringComparison.Ordinal))',
            "    {",
            "        HandleSingleplayerEndpoint(request, response, pretty);",
            "        return;",
            "    }",
            "",
            '    if (string.Equals(path, "/api/v1/multiplayer", StringComparison.Ordinal))',
            "    {",
            "        HandleMultiplayerEndpoint(request, response, pretty);",
            "        return;",
            "    }",
            "}",
        ]
    )

    block = audit_endpoints.extract_route_block(source, "/api/v1/singleplayer")
    assert block is not None
    assert "HandleSingleplayerEndpoint(request, response, pretty);" in block
    assert "/api/v1/multiplayer" not in block

    # The last route has no next route to stop at.
    last = audit_endpoints.extract_route_block(source, "/api/v1/multiplayer")
    assert last is not None
    assert "HandleMultiplayerEndpoint(request, response, pretty);" in last

    # A prefix match would let /api/v1/profiles answer for /api/v1/profile.
    profile = audit_endpoints.extract_route_block(source, "/api/v1/profile")
    assert profile is not None
    assert "HandleProfilesEndpoint" not in profile


def test_route_serves_method_direct_and_wrapper() -> None:
    direct = 'else if (path == "/api/v1/record")\n{\n    if (request.HttpMethod == "GET")\n        SendJson(response, status);\n}'
    assert audit_endpoints.route_serves_method('', direct, "GET")

    equals = 'if (string.Equals(path, "/api/v1/relicbag", StringComparison.Ordinal))\n{\n    if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))\n        HandleGetRelicBag(response);\n}'
    assert audit_endpoints.route_serves_method('', equals, "GET")
    assert not audit_endpoints.route_serves_method('', equals, "POST")

    wrapped = 'if (path == "/api/v1/settings")\n    SendGetOnly(request, response, () => HandleGetSettings(response));'
    assert audit_endpoints.route_serves_method('', wrapped, "GET")
    assert not audit_endpoints.route_serves_method('', wrapped, "POST")


def test_route_serves_method_delegated_handler() -> None:
    source = "\n".join(
        [
            "public static partial class McpMod",
            "{",
            "    private static void HandleSingleplayerEndpoint(",
            "        HttpListenerRequest request, HttpListenerResponse response, bool pretty)",
            "    {",
            '        if (request.HttpMethod != "GET" && request.HttpMethod != "POST")',
            "        {",
            '            SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");',
            "            return;",
            "        }",
            '        if (request.HttpMethod == "GET")',
            "            HandleGetSingleplayer(request, response);",
            '        else if (request.HttpMethod == "POST")',
            "            HandlePostAction(request, response);",
            "    }",
            "}",
        ]
    )

    assert audit_endpoints.route_serves_method(source, "HandleSingleplayerEndpoint(request, response, pretty);", "GET")
    assert audit_endpoints.route_serves_method(source, "HandleSingleplayerEndpoint(request, response, pretty);", "POST")

    missing = "public static partial class McpMod\n{\n    private static void HandleOther()\n    {\n        SendJson(response, 1);\n    }\n}"
    assert not audit_endpoints.route_serves_method(missing, "HandleOther(response);", "GET")


def test_member_body_and_method_guard_order() -> None:
    source = "\n".join(
        [
            "public static partial class McpMod",
            "{",
            "    private static void HandleSingleplayerEndpoint(",
            "        HttpListenerRequest request, HttpListenerResponse response, bool pretty)",
            "    {",
            '        if (!string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal)',
            '            && !string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))',
            "        {",
            '            SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");',
            "            return;",
            "        }",
            "        if (IsMultiplayerRun())",
            "            return;",
            "    }",
            "",
            "    private static void HandleNext()",
            "    {",
            "    }",
            "}",
        ]
    )

    body = audit_endpoints.extract_member_body(source, "HandleSingleplayerEndpoint")
    assert body is not None
    assert "HandleNext" not in body

    guard = audit_endpoints.find_method_guard(body)
    mode = body.find("IsMultiplayerRun()")
    assert guard >= 0
    assert mode > guard, "the method rejection must come before the run-mode guard"

    # The old spelling is the other shape the guard has to recognise.
    old_guard = 'if (request.HttpMethod != "GET" && request.HttpMethod != "POST")'
    assert audit_endpoints.find_method_guard(old_guard) >= 0
    assert audit_endpoints.find_method_guard("if (path == \"x\")") == -1
    assert audit_endpoints.extract_member_body(source, "HandleMissing") is None


def main() -> None:
    test_equality_both_spellings()
    test_equality_is_exact_and_name_bounded()
    test_route_block_old_else_if_chain()
    test_route_block_standalone_if_statements()
    test_route_serves_method_direct_and_wrapper()
    test_route_serves_method_delegated_handler()
    test_member_body_and_method_guard_order()
    print("audit extractor tests passed")


if __name__ == "__main__":
    main()
