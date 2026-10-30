# Card-selection context

Compiles the shipped `McpMod.CardSelectionContext.cs` against small stand-ins for the game types it
touches (`CardSelectCmd`, `CardSelectorPrefs`, `CardPile`, a few models) and drives the real
prefix → state-export path. No game, no Godot, no NuGet.

```sh
dotnet run --project tests/CardSelection -c Release
```

Runs under `tr-TR`; `PileType.Hand` is lower-cased by the production code, so a culture-sensitive
`ToLower` would show up.

## What it pins

- `SelectionSurface`: the `CardSelectCmd` method-name prefix decides the surface
  (`hand`, `combat_pile`, `generated_choice`, `generated_bundle`, `deck`, and `simple_grid` for an
  unknown method).
- `SelectionOriginKind`: the model namespace decides `card` / `potion` / `power` / `relic` /
  `monster` / `event` / `model`, and a null type has no kind.
- `SelectionEffect`: exact method names first (`discard`, `upgrade`), then origin-type names, then
  the `generated_to_hand` / `origin_specific` fallbacks.
- The published state: `selection_id`, entry point, surface, effect, min/max select,
  manual confirmation, cancelable, source pile, and the origin id/type/kind/upgraded fields.
- Defaults when `prefs` or the source is absent: min/max `1`, both booleans `false`, and the
  optional keys omitted rather than written as null.
- A newer selection replaces the context and gets a strictly larger id, so a client holding an old
  id can tell it is stale.

## What it cannot check

- The Harmony patch actually attaching to this build's `CardSelectCmd` overloads. That is the
  `method-prefix` entry in `tests/GameSurface`; whether it then sees a live selection is a runtime
  fact.
- Which `CardSelectCmd` overload the game calls for a given card, and what the selection screen
  does next. The manual check: trigger a `FromHandForDiscard` (e.g. a discard-a-card effect) in
  game and confirm `GET /state` reports `selection_surface: hand` and `selection_effect: discard`.
