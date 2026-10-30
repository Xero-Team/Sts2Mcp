# Game surface check

What the mod reaches inside the shipped game by **name** — reflection strings, string-target
Harmony patches, string type names — resolved against the installed `sts2.dll` with
`System.Reflection.Metadata`. A game update that removes or renames one of those members fails
here, headlessly, before anyone launches the game.

```sh
dotnet run --project tests/GameSurface -c Release -- "<game>/data_sts2_linuxbsd_x86_64"
# or with STS2_GAME_DATA_DIR / STS2_GAME_DIR set, no argument:
make test
```

`sts2.dll` is read as bytes and parsed with `PEReader`; it is never loaded into the process, so no
Godot native library and no static initialiser runs. The harness needs no NuGet package and no
network.

## What it pins

- `tests/GameSurface/game-surface.json` lists every literal member name the mod passes to
  `GetField` / `GetProperty` / `GetMethod` / `GetEvent`, to its own member-name helpers
  (`GetInstanceFieldValue`, `GetInstanceMemberValue`, `GetMemberValue`, `GetBoolMemberValue`,
  `GetPropertyValue`, `ReadModelText`, `ClickMenuButtonField`), to `AccessTools.*`, to
  `Assembly.GetType("…")`, and every `[HarmonyPatch(typeof(X), "Member")]` string target.
- `scope: "type"` means the source states the owning type (`typeof(X).GetField(...)`,
  `[HarmonyPatch(typeof(X), "…")]`); the harness requires that type to declare the member.
- `scope: "assembly"` means the owning type is only known at run time (the mod reads a live game
  object). The harness requires that *some* type in `sts2.dll` still declares the member of that
  kind. That catches a rename or removal; it cannot prove which type the live object will be.
- `required: false` marks a read the mod deliberately tolerates when absent: `IsPlayPhase` is
  version-gated with a fallback, and `MarkedActIndex`, `_textLabel`, `_buttonLabel`, `Label`,
  `BbcodeText` are alternative names in a candidate list. If a future build *does* declare one,
  the check notices nothing because the code does not require it.
- `external: "bcl"` marks members declared on a BCL type (`ValueTuple.Item1`, `Stack<T>.Count`,
  `.Peek()`, `.Pop()`), which live outside the game assembly. They are reported as `skip` and not
  graded.

The manifest is derived from source by `scripts/test_game_surface_manifest.py`; `make test` runs
that script after the harnesses. It fails when a new reflection string or string Harmony target
is not in the manifest, when a manifest entry no longer exists in source, or when a recorded line
has moved. Regenerate with:

```sh
python3 scripts/test_game_surface_manifest.py --write
```

## Known mismatch in v0.111.0

`McpMod.Compendium.cs:584` looks up `MegaCrit.Sts2.Core.Platform.Steam.SteamUtil` and calls
`GetSteamID64` on it. This build ships `MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamUtil`
instead, and that type has no `GetSteamID64` (the id accessor is a Steamworks extension). The
manifest records both entries as `broken`, the harness prints `BROKEN …` for them, and the exit
code stays 0 so the rest of the surface is still graded. Fixing it is its own change: it alters
what `GetActiveSteamId` returns.

## What it cannot check

- Whether the member's **signature** matches the call. The manifest pins a name and kind, not
  parameter types or return type; the reflection call sites themselves are name-only.
- Members declared outside `sts2.dll` (BCL and `GodotSharp.dll`).
- Members reached through a variable (`AccessTools.DeclaredMethod(type, name)` where both come
  from data), and Harmony targets whose method list is built from `nameof` (those are already
  compile-checked when the mod builds against the new DLL).
- Any runtime behavior: this is a metadata assertion, not an execution. The manual in-game
  checks in `tests/README.md` remain the proof that a path works.
