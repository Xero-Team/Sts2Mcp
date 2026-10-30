# Reward markdown formatting

Compiles the shipped `McpMod.RewardFormatting.cs` and calls its private formatters through
reflection. The formatter is pure over the reward dictionary — no game state is read — so every
assertion here is the real production code. No game, no Godot, no NuGet.

```sh
dotnet run --project tests/RewardFormatting -c Release
```

Runs under `tr-TR`, so a culture-sensitive format string or casing call would change the text.

## What it pins

- The empty and absent reward lists still render a heading, the "no rewards" line and
  `**Can proceed:** No`.
- `gold_amount` renders as `(25 gold)`, and takes precedence over the potion fields.
- `potion_description` renders after ` - `, and `potion_name` is the `(name)` fallback.
- A `reward_id` is shown as `` `REWARD_1` `` with the selection state, and an absent state reads
  `unknown` rather than being omitted.
- `screen_revision`, `can_proceed` and `selection_history` lines, including the
  `consumed=unknown, last=unknown` fallbacks.
- A null value in the dictionary never leaks the literal `null` into the markdown.
- The card-reward screen: `**Parent reward**`, the per-card line with cost/type/rarity/keywords,
  the `→ **Upgraded**` preview line, `**Can skip**`, and the alternative options including a
  disabled one.

## What it cannot check

- Whether the reward dictionary is *correct* — that is `Rewards/RewardSession.cs` and the live
  game, covered by `tests/RewardContract` and the in-game reward flow.
- HTTP/MCP serialization of the same data (`tests/ContractShape`, `scripts/test_mcp_server.py`).
- Any live reward screen: the manual check is to claim gold, a potion, a relic and a card in game
  and compare the `GET /rewards` markdown with what the event shows.
