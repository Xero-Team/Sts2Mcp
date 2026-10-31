# Sts2Mcp target profile

Read this before reconnaissance; it replaces guesswork about the target's shape.
Ground truth for how the code works is `.agents/skills/sts2-engineering/SKILL.md`,
`AGENTS.md`, `docs/raw-*.md`, and the code itself.

## What the target is

A Slay the Spire 2 mod (`Sts2Mcp.dll`, C#/.NET, Harmony patches, built by
`Sts2Mcp.csproj` + `build.ps1`) that runs *inside the game process* and exposes a
JSON HTTP API, plus a Python MCP bridge (`mcp/server.py`, FastMCP, `httpx`) that
forwards to it. There is no server-side tenant model, no user accounts, and no
remote deployment: the trust boundary is "any local process on the machine"
versus "the player's game process and save/profile data".

## Surfaces and their companions

| Surface | Files | Companion |
| --- | --- | --- |
| Local HTTP API: routing, status mapping, body parsing, CORS/host binding | `McpMod.cs` | [WEB-PROTOCOL-AND-AUTH.md](WEB-PROTOCOL-AND-AUTH.md), [PROTOCOLS-RPC-AND-MESSAGING.md](PROTOCOLS-RPC-AND-MESSAGING.md) |
| Action dispatch, index and phase validation, multiplayer actions | `McpMod.Actions.cs`, `McpMod.MultiplayerActions.cs` | [PROTOCOLS-RPC-AND-MESSAGING.md](PROTOCOLS-RPC-AND-MESSAGING.md) |
| State serialization and path/name interpolation into responses and logs | `McpMod.StateBuilder.cs`, `McpMod.Formatting.cs`, `McpMod.Helpers.cs` | [MEMORY-SAFETY-AND-BINARY.md](MEMORY-SAFETY-AND-BINARY.md) |
| Profile, run history, compendium, wiki file and game-data access | `McpMod.Profile.cs`, `McpMod.Compendium.cs`, `McpMod.Wiki.cs` | [DATA-ISOLATION-AND-LIFECYCLE.md](DATA-ISOLATION-AND-LIFECYCLE.md) |
| MCP bridge: tool schemas, URL/proxy handling, error surfaces | `mcp/server.py` | [AI-AND-LLM.md](AI-AND-LLM.md), [CLIENT-SIDE.md](CLIENT-SIDE.md) |
| Build, NuGet restore, Harmony patch targets, release of the DLL | `Sts2Mcp.csproj`, `build.ps1`, `mod_manifest.json` | [SUPPLY-CHAIN-AND-RELEASE.md](SUPPLY-CHAIN-AND-RELEASE.md) |

Skip companions that have no surface here (cloud deployment, browser client,
mobile, kernel). Say which ones you skipped and why instead of silently narrowing
coverage.

## Boundaries worth naming first

Listener prefixes are `localhost`/`127.0.0.1` only — treat a concrete
loopback-to-remote escalation, a request that mutates profile or save data a
caller should not reach, or path traversal into files outside the intended game
directory as real boundary crossings. A localhost API with no auth is by design
for a single-player game: do not report "no authentication" as a finding unless
you can name the principal that gains something it does not already have (for
example another OS user, a sandboxed page, or a malicious webpage using CSRF
against the loopback port).

## Execution reality

The mod cannot be unit-tested outside the game (`CONTRIBUTING.md` is explicit).
`dotnet build` works offline only if packages are already restored, and running
the mod requires a game install and launch, which is *not* a sandbox. Therefore:
default to source-only evidence, use bounded local checks on the Python bridge
(pure transforms, `node`/`python` fixtures) when a control can be enforced, and
route anything needing a live game into `needs_validation` with the exact missing
fact and a safe owner-observed plan. Never launch the game, the mod, or a network
probe as part of a hunter's check.

## Tooling available here

`node` (v26) runs `scripts/validate-findings.cjs`,
`scripts/validate-coverage-ledger.cjs`, and their tests; `dotnet` builds the mod.
Both validators' suites pass as shipped — rerun them before relying on them:

```sh
node --test .agents/skills/security-audit/scripts/*.test.cjs
```

## Output directory

The repository `.gitignore` covers only `bin/`, `obj/`, `out/`, and Python/env
artifacts — no audit directory is ignored. So the in-target option described in
`SKILL.md` does not apply unless the user adds an ignore rule and confirms it; use
the external default. That default lives under the home directory, which is fine
for parent-owned retained output; the "no `/tmp`, no home" rule still governs
sandboxed *scratch* for target-controlled processes.
