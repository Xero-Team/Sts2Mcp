# 0003: Duplicate features resolved during the merge

- Context: three forks independently implemented the custom-run screen, and two
  implemented ascension control and lobby-max-players reads.
- Decision:
  - Custom run: keep `jehubba`'s `AddStartRunScreenState`/`ExecuteStartRunScreenOption`
    pair, which shares the character-select code path and supports run modifiers. Drop
    `evangambit`'s standalone `McpMod.CustomRun.cs`, which faked `menu_screen` as
    `character_select` and knew nothing about modifiers.
  - Ascension: keep both call shapes because they serve different callers — the
    `set_ascension` action (absolute level, `ExecuteSetAscension`) and the
    `ascension_up`/`ascension_down` menu options (`ExecuteAscensionChange`, which clicks
    the arrow the game has left visible).
  - Lobby max players: keep the single reflection read with a `Players.Count` fallback
    (`BuildStartRunLobbyState`); drop the helper that returned `null`.
  - Reward and card markdown: `McpMod.RewardFormatting.cs` is authoritative; the older
    formatter copies were deleted rather than kept as aliases.
- Consequences: one implementation per feature, so the contract docs describe one shape.
- Debt: the two ascension paths are a small duplication that a future
  `POST /action`-only client could remove.
