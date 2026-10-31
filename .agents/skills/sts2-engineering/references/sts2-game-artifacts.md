# STS2 game artifacts and this repository

Reference for the `sts2-engineering` skill. It answers one question: given a task
in Sts2Mcp, which shipped artifact do I inspect, with which REA call, and what
do I do with the answer?

## What the build contains

The mod compiles against the game's own assemblies, referenced from
`$(STS2GameDataDir)` in `Sts2Mcp.csproj`:

| File | Role | REA route |
| --- | --- | --- |
| `sts2.dll` | Game code, namespace root `MegaCrit.Sts2.Core.*` (`Runs`, `Combat`, `Saves`, `Saves.Managers`, `Models`, `Models.Events`, `Entities.Players`, `Entities.Cards`, `Rooms`, `Rewards`, `Multiplayer.Game`, `Platform.Steam`, `Nodes.*`) | `inspect_managed_artifact` |
| `GodotSharp.dll` | Godot 4 .NET bindings (`Godot.Node`, `Godot.Control`, signals, scene tree) | `inspect_managed_artifact` |
| `0Harmony.dll` | Harmony patch runtime used by `TryApplyHarmonyPatches` | `inspect_managed_artifact` |
| `*.pck` | Godot resource packs (scenes, scripts, assets) | `open_binary` + `inspect_artifact` |
| native host binary | Engine executable; managed evidence is usually enough | native provider, last resort |

Confirm the actual file list with a directory listing before naming a pack file:
pack names differ between Windows, macOS, and Linux builds, and no path should be
assumed.

## Question to evidence to code

| Task | Evidence you need | Where the answer is consumed |
| --- | --- | --- |
| Expose a new state field | Declaring type, member name, member kind, value type, nullability hints (`NullableAttribute` when present) | `McpMod.StateBuilder.cs`, `McpMod.Formatting.cs` |
| Fix a Harmony patch after a game update | Declaring type, method name, parameter list, `static`/`virtual`/`abstract` flags, generic arity | `McpMod.cs` (`TryApplyHarmonyPatches`) |
| Drive a screen, button, or overlay | Node class under `MegaCrit.Sts2.Core.Nodes.*`, the member that performs the transition | `McpMod.Actions.cs`, `McpMod.MultiplayerActions.cs` |
| Profile, compendium, run-history data | Save manager members, progress members, history file layout | `McpMod.Profile.cs`, `McpMod.Compendium.cs` |
| Wiki or catalog lookup | Model registry and card/relic model members | `McpMod.Wiki.cs` |
| Multiplayer state | Multiplayer game/state types | `McpMod.MultiplayerState.cs` |
| Resource exists in a pack | Inventory logical path, declared MD5, observed SHA-256 | documentation or asset tooling, not runtime code |

`docs/raw-full.md` and `docs/raw-simplified.md` describe the HTTP surface, not
the game. When the HTTP surface changes, both files and the `mcp/server.py`
docstrings change with it.

## Recipe: a managed member lookup

1. Resolve the data directory (see the game-path steps in `SKILL.md`).
2. `inspect_managed_artifact` with `path: "<data dir>/sts2.dll"` and
   `detail: "summary"`. Keep the returned `parent_evidence_id`.
3. `inspect_managed_members` with `detail: "summary"` and exact
   `type_names: ["MegaCrit.Sts2.Core.Runs.<Type>"]`. Read `unresolved_filter_rows`
   and coverage: an unresolved row means the name may still exist under a
   different declaring type.
4. Page the collection you need through `inspect_analysis_view`:
   `view: {kind: "managed-members", collection: "fields", offset: 0, limit: 64}`,
   or `methods`, `call_edges`, `field_accesses`, `user_strings`.
5. For a compact method index use `fields: ["name", "declaring_type", "signature"]`
   and omit method bodies until you need CIL.

Typical follow-ups and what they prove:

- `fields` — the member is declared on that type; it does not prove it is
  populated in every game state.
- `call_edges` — some method references it; it does not prove the call is
  reachable from the state the mod observes.
- `user_strings` — a literal exists in the assembly; it is not UI text the player
  sees, and localization may live in pack resources.
- CIL body — the control flow the compiler emitted; decompiled pseudocode is
  inference, and original source text is not recoverable.

## Recipe: a Harmony target that stopped applying

After a game patch, the mod's patches fail silently unless logging is read. Treat
the target method as a complete contract:

1. Look up the declared method on the exact declaring type.
2. Compare the signature the patch expects (name, parameter types and order,
   generic arity) against the returned `signature`.
3. Check `static`/`virtual`/`abstract` and the declaring type's inheritance: a
   member moving from a base class to an override changes what the patch sees.
4. Only then update `TryApplyHarmonyPatches` and re-run the in-game smoke test.

Do not widen a patch signature to make it compile; a patch that binds the wrong
method can corrupt state in subtle ways. If the member is gone, search other
declaring types before concluding removal, and report the search boundary.

## Recipe: driving a screen or action

1. Find the node class that owns the interaction (`Nodes.Screens.*`,
   `Nodes.CommonUi`, `Nodes.Rewards`, `Nodes.Rooms`, `Nodes.Combat`, ...).
2. Find the member that performs the state transition rather than the member
   that merely reflects state.
3. Check whether the member is guarded by visibility, animation, or
   already-resolved state; the mod must skip the action when the guard is closed
   instead of forcing the transition.

The mod reads Godot nodes through `Godot.Node` APIs and calls the game's methods
directly. A metadata match is not proof the node exists in the current scene
tree — that part is runtime evidence from a live game.

## Recipe: pack and save artifacts

- Pack: `open_binary` on the `.pck`, then `inspect_artifact` with
  `detail: "summary"`. Filter inventory views by exact `logical_paths`. Preserve
  declared MD5 versus observed SHA-256, and read `source_coverage` before
  claiming completeness. No Godot resource decoding is provided; a `.tscn` or
  `.gd` inside the pack stays opaque bytes.
- Saves: run history lives in the profile's `saves/history` directory as `*.run`
  files, and the mod already summarizes at most 20 of them. Confirm the real
  container format before choosing a tool; `inspect_sqlite_database` applies
  only when the bytes are SQLite. Prefer the game's own save manager members
  when the question is about meaning rather than layout.

## Version bumps

When the README "Tested against" line changes:

1. Re-resolve the data directory; do not reuse a stale path.
2. Re-run the managed lookups for every member the mod binds directly and every
   patch target. Direct bindings fail at compile time; reflection and Harmony
   targets fail at runtime, which is the dangerous case.
3. Rebuild, install into `mods/`, launch, and exercise the affected path. That
   run is the runtime evidence.
4. Report which conclusions are now build-local to the new version and which
   remain unchanged.

## Guardrails

- Read-only on the install directory. Never patch game files, never copy game
  assemblies into this repository, and keep `Private=false` on the references.
- Do not commit extracted game content; treat packs and dumps as local artifacts.
- Do not add a game-behavior claim to `docs/raw-*.md` without tool evidence.
- A finding is not a fix. The change still needs a zero-warning build and an
  exercised in-game path before it is done.
- Keep game logic in C#. If a REA finding tempts you to add interpretation to
  `mcp/server.py`, put it in the mod instead.
