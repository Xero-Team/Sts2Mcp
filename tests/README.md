# Test surface

What is actually checked, for every production unit in this repository, and what is not. A row
with no headless check says so instead of leaving the cell empty; a row that is checked only by
metadata says the runtime behaviour is still unverified.

```sh
make verify        # build + format-check + every harness + the manifest check
make verify-mcp    # ruff lint/format + Python MCP bridge tests
make record-selftest
make audit         # static endpoint/doc audit (known pre-existing failures, see below)
```

`make verify` runs every `tests/*/` harness and then `scripts/test_game_surface_manifest.py`. The
harnesses are exempt from analyzers (`tests/Directory.Build.targets`) and the projects target
`net9.0`, so `DOTNET_ROLL_FORWARD=Major` is exported by the Makefile on newer-runtime machines.

## Kinds

- **harness** — a `tests/<Name>` executable that compiles the real production source (or the real
  assembly) and asserts behaviour. Runs headless, no network.
- **python** — a `scripts/` or `tools/` test that imports real modules and asserts behaviour.
- **metadata** — `tests/GameSurface`: the member names the unit reaches by reflection exist in the
  installed `sts2.dll`. Proves the binding, not the behaviour.
- **manual-only** — the only proof is running the game. Each such row names the exact in-game
  action in *Manual checks owed* below.

Direct calls to game APIs are compile-checked by `make build` against the configured DLL: a removed
type or member fails the build. That is not repeated in every row.

## Matrix

### Mod core

| Unit | Kind | Entry point | What it pins | Why not more |
|---|---|---|---|---|
| `McpMod.cs` | manual-only | in-game start | Compile-time only: version/schema constants and the mod initialiser. It reads no game member by name. | HTTP listener, main-thread queue, Harmony patch application and the `/` greeting need Godot's runtime; only a live start proves them. |
| `McpMod.Warnings.cs` | manual-only | in-game state read | — | No reflection names and no pure logic: it only collects what other builders swallowed. Observed through `GET /state` warnings. |
| `McpMod.Formatting.cs` | manual-only | in-game `GET /state?format=markdown` | Reward subsection is covered by `tests/RewardFormatting`. | The state markdown needs a live state dictionary; `RewardContract` pins the reward side only. |
| `MainThreadUnavailableException.cs` | manual-only | in-game long action | — | A marker exception type; its meaning is the 503 the handler emits, which needs the game thread. |
| `McpPortResolver.cs` | harness | `tests/Config` | Argument/conf/default precedence and the 1–65535 range. | Startup file discovery and the actual listener are the manual `Sts2Mcp.conf` check. |
| `McpMod.Config.cs` | harness + manual-only | `tests/Config`; in-game start | `STS2_PORT` / `.env` / conf ordering, malformed values falling through, `.env` quoting. | The `Sts2Mcp.conf` read path (missing/corrupt JSON, `GD.PrintErr`, recorder options) needs the real filesystem. |

### State, actions, surfaces

| Unit | Kind | Entry point | What it pins | Why not more |
|---|---|---|---|---|
| `McpMod.StateBuilder.cs` | harness + metadata | `tests/CreatureIdentity`, `tests/CombatDamageHistory`, `make test` | The compiled creature-id exporter against the real allocator; 68 reflection bindings. | The full state payload needs a live run; each `state_type` is a manual check. |
| `McpMod.Actions.cs` | metadata + manual-only | `make test` | 34 reflection bindings across menu, reward, selection and game-over screens. | Actions mutate live screens; each one is an in-game action check. |
| `McpMod.Helpers.cs` | metadata + manual-only | `make test` | 19 member-name bindings, including the candidate-name lists. | The helpers swallow failures by design; only a live screen shows the value. |
| `McpMod.ForkEndpoints.cs` | metadata + manual-only | `make test` | 6 bindings for settings/profile endpoints. | Settings read needs the live settings model. |
| `McpMod.Profile.cs` | metadata + manual-only | `make test` | 3 bindings for profile switching. | Multi-profile switching is a live-save operation. |
| `McpMod.MultiplayerState.cs` | metadata + manual-only | `make test` | 2 bindings used to read lobby/queue state. | Co-op state needs a second peer. |
| `McpMod.MultiplayerActions.cs` | manual-only | in-game co-op | Compiles against `RunManager`/`NetService`; no literal member names. | Host/client actions need two processes. |
| `McpMod.SettingsUI.cs` | metadata + manual-only | `make test` | 8 bindings and the `NFastModeTickbox` Harmony targets. | The injected settings row is only visible in the game menu. |
| `McpMod.Compendium.cs` | metadata + manual-only | `make test` | 4 bindings; records the **known Steam mismatch** below. | Compendium screens need a live run history. |
| `McpMod.Wiki.cs` | manual-only | in-game `GET /wiki` | Compiles against the model catalog; limits are plain constants. | Search/listing walks live model data. |
| `McpMod.RecordInspect.cs` | manual-only | in-game `GET /record/inspect` | Compiles against the game's `PacketReader`. | Reading a real `.mcr` needs a recorded run; `tools/record_check.py` checks the file format separately. |
| `McpMod.Debug.cs` | manual-only | in-game debug actions | Compiles against `CombatRoom`/`RunManager`. | Encounter jump/play-phase forcing is destructive and live-only. |
| `McpMod.GenerationPool.cs` | manual-only | in-game `GET /pool` | Compiles against `CardModel`; hashes the installed assembly. | Enumerates live model tables. |
| `McpMod.CardPools.cs` | manual-only | in-game pool fetch | Compiles against `CardPoolModel`. | Run-scoped unlock state is live. |
| `McpMod.PotionPools.cs` | manual-only | in-game pool fetch | Compiles against `PotionPoolModel`. | Same: run-scoped unlock state. |
| `McpMod.MapDrawings.cs` | manual-only | in-game drawing endpoint | Compiles against Godot node types; limits are constants. | Drawing state lives in map nodes. |
| `McpMod.MapDrawingCoordinates.cs` | harness | `tests/MapDrawingCoordinates` | Coordinate mapping and clamping between map space and node space. | Needs the real map scene to prove placement. |
| `McpMod.PunchOff.cs` | metadata + manual-only | `make test` | The `PunchOff.PunchEachOther` Harmony target. | The hang it prevents only occurs under Instant fast mode in the real event. |
| `McpMod.CrystalSphere.cs` | metadata + manual-only | `make test` | 7 bindings, the minigame type name and both Harmony targets. | Minigame clicks need the live screen. |
| `McpMod.LoadingDiagnostics.cs` | metadata + manual-only | `make test` | The `NetLoadingHandle` constructor Harmony target. | Leak detection needs a live load that never completes. |

### Combat, cards, rewards, RNG

| Unit | Kind | Entry point | What it pins | Why not more |
|---|---|---|---|---|
| `McpMod.CardValues.cs` | harness | `tests/CardValues` | Enchanted/preview value restoration, nested descriptions, cleanup after failure. | Game type stand-ins; no Godot preview hook. |
| `McpMod.CombatDamageHistory.cs` | harness + metadata | `tests/CombatDamageHistory` | Owner/side turn history against the compiled exporter. | Constructs damage results directly; command ordering is not exercised. |
| `McpMod.Predicates.cs` | harness | `tests/PredicateScan` | The real scanner over lambdas in the shapes the decompiled monsters use (Roslyn IL shapes). | Covers the shapes in the cases, not every shipped monster. |
| `McpMod.MoveEffects.cs` | harness | `tests/PredicateScan` | Move-effect scan over the same lowered IL. | Same: case list, not full catalog. |
| `McpMod.Il.cs` | harness | `tests/PredicateScan` | Instruction widths used by both scanners, via the scans above. | No standalone fuzz of the width table. |
| `McpMod.Prediction.cs` | metadata + manual-only | `make test` | 7 bindings for the intent state machine. | Needs a live monster to compare a predicted branch with the resolved move. |
| `McpMod.CardSelectionContext.cs` | harness + metadata | `tests/CardSelection`, `make test` | Surface/origin-kind/effect mapping, published state, id monotonicity; the `CardSelectCmd` prefix patch. | Which overload the game calls, and the live screen, are runtime facts. |
| `McpMod.RewardFormatting.cs` | harness | `tests/RewardFormatting` | Both markdown formatters, under `tr-TR`. | Input dictionary correctness is `RewardContract`'s job. |
| `Rewards/RewardSession.cs` | harness | `tests/RewardContract` | Reward enumeration, id stability, claim/skip/sacrifice/reroll transitions, additive compatibility. | Async resolution and the live reward UI. |
| `McpMod.Rewards.cs` | harness + metadata + manual-only | `tests/RewardContract`, `make test` | The session state machine and 3 reflection bindings. | The game screen it mirrors needs a live reward. |
| `McpMod.RelicEligibility.cs` | harness | `tests/RelicEligibility` | Party eligibility predicate across order and profile counts. | Sourced from the decompiled predicate; a live run with mixed profiles is still owed. |
| `McpMod.RelicBagGeneration.cs` | metadata + manual-only | `make test` | 4 bindings and the `RelicGrabBag` method set. | Void-mutation invalidation only observable in a live bag. |
| `McpMod.RngStreams.cs` | harness + metadata | `tests/RelicBagFallback`, `make test` | Relic-bag fallback export (empty/missing/unreadable); 8 bindings. | Stream coordinates need a live run RNG; the pure seam is only `SnakeCase`. |

### Recorder

| Unit | Kind | Entry point | What it pins | Why not more |
|---|---|---|---|---|
| `Recorder/RecorderPatches.cs` | metadata + manual-only | `make test` | 10 hand-resolved Harmony/patch targets, so a renamed method fails here instead of silently unpatched. | Attaching them needs the game; `make record-selftest` covers only the file checker. |
| `Recorder/CombatRecorder.cs` | manual-only | in-game with `"record": true` | Compiles against the replayer writer. | Needs a real combat. |
| `Recorder/RecordObserve.cs` | metadata + manual-only | `make test` | 1 binding (`_counter` on the game RNG). | Observation values are live. |
| `Recorder/RecordWriter.cs` | manual-only | in-game recording | Plain file writing. | Produces the `.mcr`; `tools/record_check.py` verifies one after the fact. |

### Contracts and transport

| Unit | Kind | Entry point | What it pins | Why not more |
|---|---|---|---|---|
| `Core/Contracts/*.cs` (8 files) | harness | `tests/ContractShape` | Exact JSON bytes of the error, unavailable and settings bodies; omitted vs null fields. | Payload content beyond the envelope is each endpoint's concern. |

### Python bridge

| Unit | Kind | Entry point | What it pins | Why not more |
|---|---|---|---|---|
| `mcp/sts2mcp/client.py` | python | `make verify-mcp` | Structured HTTP errors preserved, non-endpoint errors stay text. | No live mod; transport is stubbed with `httpx`. |
| `mcp/sts2mcp/errors.py` | python | `make verify-mcp` | Error shape extraction from endpoint bodies. | — |
| `mcp/sts2mcp/tools/profiles.py` | python | `make verify-mcp` | Profile switch retry/wait, timeout keeps the first response, delete preserves endpoint errors. | Other tool modules are not imported by the test. |
| `mcp/sts2mcp/app.py` | python | `make verify-mcp` (via ruff) + `make audit` | Imports cleanly; endpoint strings audited statically. | FastMCP registration is exercised only by starting the server. |
| `mcp/sts2mcp/__init__.py`, `mcp/sts2mcp/telemetry.py`, `mcp/sts2mcp/tools/__init__.py`, `tools/{combat,debug,general,map,multiplayer,rewards,rooms,selection}.py` | python | `make verify-mcp` (ruff) + `make audit` | Import/format and endpoint-string audit. | No behavioural test per tool; they are thin wrappers over endpoints. |
| `mcp/server.py` | python | `make verify-mcp` (ruff) + `make audit` | Import/format and endpoint strings. | Needs a running mod to serve. |

### Scripts and tools

| Unit | Kind | Entry point | What it pins | Why not more |
|---|---|---|---|---|
| `scripts/audit_endpoints.py` | python | `python3 scripts/test_audit_endpoints.py`, `make audit` | The extractor against samples; `make audit` is the real run over the C# and Python sources. | The extractor test is not wired into a make target; `make audit` is. Capability checks need the live game (`make audit-live`). |
| `scripts/test_mcp_server.py` | python | `make test-mcp` | The bridge tests above. | — |
| `scripts/test_game_surface_manifest.py` | python | `make test` | Manifest matches the C# sources; missing/stale/moved entries fail. | — |
| `tools/record_check.py` | python | `make record-selftest` | Every way a record can be truncated or damaged is reported; a whole record passes. | Synthetic records, not a real capture. |
| `tools/record_check_selftest.py` | python | `make record-selftest` | The cases themselves. | — |

## Known mismatch in v0.111.0

`McpMod.Compendium.cs:584` looks up `MegaCrit.Sts2.Core.Platform.Steam.SteamUtil`; this build ships
`MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamUtil`, which has no `GetSteamID64`. Both
manifest entries are marked `broken`, `tests/GameSurface` prints `BROKEN …` for them and still
exits 0, and `tests/GameSurface/README.md` records the detail. The fix changes what
`GetActiveSteamId` returns, so it is its own change, not part of this test branch.

## Manual checks owed

Each is a specific action, not "test the mod":

1. Start the game with no `Sts2Mcp.conf`, then with a corrupt one: the mod must still listen on
   `McpPortResolver`'s default. Then with `"port": 17001`: `GET /` must answer with the greeting there.
2. Claim gold, a potion, a relic and a card reward in one run and compare `GET /rewards` markdown
   with the event.
3. Trigger a discard-a-card effect and confirm `GET /state` reports `selection_surface: hand` and
   `selection_effect: discard`.
4. Open the settings screen with Instant fast mode on and confirm the mod's row appears and toggles.
5. With `"record": true`, fight one combat, then run `tools/record_check.py` on the produced
   `.mcr` and `GET /record/inspect`.
6. Enter the Punch Off event under Instant fast mode: the event must open and the HTTP endpoint
   must keep answering.
7. If `GetActiveSteamId` is ever fixed: `GET /compendium` (or whichever caller) must still report
   the same profile identity in singleplayer.
