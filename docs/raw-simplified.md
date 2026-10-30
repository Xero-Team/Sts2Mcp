# API Quick Reference

HTTP API on `localhost:15526`. No authentication.

- `GET /` — health check; returns `{ message, status, version, commit, schema_version }`
- `GET /api/v1/singleplayer` — read game state
- `POST /api/v1/singleplayer` — perform action
- `GET /api/v1/multiplayer` — read multiplayer state
- `POST /api/v1/multiplayer` — perform multiplayer action
- `GET /api/v1/mapdrawings` — read saved map strokes and live node anchors
- `GET /api/v1/profile` — read current profile progress
- `GET /api/v1/compendium` — read Compendium-shaped profile progress
- `GET /api/v1/wiki` — fuzzy-search, or list, card/relic wiki entries
- `GET /api/v1/cardpools` — every unlocked card pool in roll order (static per run)
- `GET /api/v1/profiles` — list profile slots
- `POST /api/v1/profiles` — switch or delete profile slots
- `GET /api/v1/record` — combat recorder status (see [recording.md](recording.md))
- `GET /api/v1/record/inspect?file=<.mcr>` — decode a combat replay with the game's reader (verification only; contains hidden checksums)
- `POST /api/v1/singleplayer {"action": "set_recording", "enabled": true|false}` — turn the combat recorder on/off (works without a run)

Singleplayer and multiplayer endpoints are mutually exclusive (HTTP 409 if mismatched).

## GET — Query Parameters

| Parameter | Values                                        | Default   | Description                                                     |
|-----------|-----------------------------------------------|-----------|-----------------------------------------------------------------|
| `format`  | `json`, `markdown`                            | `json`    | Response format                                                 |
| `pretty`  | `1`, `true`, `yes` (or present with no value) | off       | Indent the JSON. Off by default — compact output is ~32% smaller |
| `detail`  | `summary`, `full`                             | `summary` | Multiplayer only. `full` expands the top-level player roster with every player's master deck, relics, potions, status, stable IDs, and player RNG. |
| `values`  | `powered`, `unpowered`                        | `powered` | Whether card descriptions and enemy intent labels arrive with the player's standing powers and relics baked into their numbers; unknown values return HTTP 400. |

`pretty` applies to POST requests as well (e.g. `POST /api/v1/singleplayer?pretty=1`) and to error
responses. It has no effect on `format=markdown`.

`values=unpowered` strips the player's standing powers and relics back out of card descriptions and enemy intent labels, leaving base + enchantment, and marks each card `"text_baked": false` and each rebuilt intent `"unbaked": true`. Keywords (including relic-removed Exhaust), enchantment text, energy cost and orb values are identical in both modes. See raw-full.md for the full contract.

Combat player objects include `play_pile` and `play_pile_count`, also for remote players. Play contains executing cards and reserved autoplays in the game's pile order; cards remain there through repeated plays and blocking selections. These are normal pile card objects, including `combat_card_id` and `target_type`. The pile reports physical cards, not their remaining execution commands.

Pael's Tooth includes `relics[].runtime.return_cards`: full card records in saved
order, rendered with their pending upgrade applied. Each entry also reports its
original `stored_upgrade_level`. Reading the list changes no cards or RNG.

## GET — State Types

Every JSON response includes:
- `game_version` — the game's own release version (e.g. `"0.108.2"`), from its `release_info.json`; omitted when the build has none
- `state_type` — which screen the game is on (see below)
- `mod_version` — mod version string, e.g. `0.4.0`
- `mod_commit` — git commit the mod was built from, e.g. `2de67cd` (`2de67cd-dirty` for a build from an edited tree), `null` when the build could not determine one
- `schema_version` — integer version of this contract, currently `3`. **Bumped only when an existing field or action changes meaning or is removed.** Added fields, actions, parameters and `state_type`s do not bump it, so feature-detect additions by presence and use `schema_version` only to notice a breaking change. `3` removed `map.next_encounters`, which exposed future rooms before the player could see them; `map.encounters_seen` reports only travelled encounters. `2` changed enemy targeting: `battle.enemies` can now contain a dead enemy the game keeps in the fight (`alive: false`), so it is no longer the same thing as "the enemies you can target".
- `warnings` — **present only when something went wrong.** A list of strings, one per part of the state that could not be built, as `"<field or section>: <ExceptionType>: <message>"` (e.g. `"map.encounters_seen: NullReferenceException: ..."`). The state builders read the live game through reflection and swallow whatever throws, so a member the game renamed simply stops appearing; this list says which one. **A warning naming a field means that field may be absent, partial or stale below** — the rest of the payload is still valid. Duplicates are collapsed, and the list stops at 50 entries plus a final `"... N more warnings suppressed"`. Markdown renders the count and the first three under the `STILL RESOLVING` block. Not every swallowed exception is reported: a few catches are expected to fire during normal play (node-tree churn, probing alternative property names) and are deliberately silent.
- `run` — `{ act, floor, ascension, player_count, card_multiplayer_constraint }` (absent for `menu`)
- `player` — full player state: character, HP, gold, relics, potions, `max_potion_slots` (belt capacity, grows with relics), and during combat: energy, hand, piles, orbs (absent for `menu`)
  - `character_id` / `player_id` — 非ローカライズのモデルID / `Player.NetId` の文字列。追加項目。
  - `generation_pool` — 同じ snapshot のローカル Player 自身の解放済み戦闘生成候補。`{ character_id, player_id, players, multiplayer_constraint, known, cards: [{id,type}], game_assembly_sha256, filter_version: 1, filter_source }`。単独・マルチともに出す。取得失敗は `known:false, cards:[], reason` と warnings。`GetUnlockedCards(Player.UnlockState, run.CardMultiplayerConstraint)` に実人数・`FilterForCombat`・Attack/Skill/Power の filter を適用する。抽選/RNG/カード作成は行わない。SHA256はロードした本体DLLの版、filter出典の正確な文字列と公開範囲の注意は [raw-full.md](raw-full.md#解放済みの戦闘生成-pool追加schema-3) を参照。図鑑の discovered_cards と同一視しない。図鑑は未発見札を NotSeen にし、ラン固有の一覧を直接見せない。年代記の解放画面は解放対象札を表示する。Markdown は件数・人数か unknown と理由だけ。追加なので schema は 3 のまま、旧 MOD は欄を省略。
  - `relics[]` — owned relic entries include boolean `used_up` (`RelicModel.IsUsedUp`) and `melted` (`RelicModel.IsMelted`), including the multiplayer local player. Markdown adds `(used)` / `(melted)` when true in singleplayer outside combat, and during combat. Multiplayer outside combat does not list relics in Markdown. Feature-detect these additive fields; schema stays at 3. Older builds omit them. Melted wax is a separate state, not a synonym for consumed or every disabled relic.
  - `deck_count` — number of cards in the master deck. Present on every screen, combat included (combat clones the deck into the draw pile, so the deck itself stays intact).
  - `deck` — the master deck itself, one entry per card: `index` (position in this list), `id`, `name`, `type`, `cost`, `star_cost`, `description`, `rarity`, `is_upgraded`, `keywords`. **Outside combat only** — during a fight the hand plus `draw_pile` / `discard_pile` / `exhaust_pile` already enumerate every card. Empty (and `deck_count` 0) before a run's starting deck is dealt out. Markdown renders it as `### Deck (N cards)` with identical copies collapsed into one `×N` line.
- `battle` — combat state (`monster` / `elite` / `boss`, and included for context on the overlays a fight can open): `round`, `turn`, `is_play_phase`, `enemies`.
  - `enemies[].alive` — **on every enemy**. Almost always `true`: a killed enemy leaves the fight and stops being listed. The exception is a corpse the game refuses to remove — a Decimillipede segment held by `REATTACH_POWER` — which stays listed with `alive: false` and `hp: 0`, keeping the `entity_id` it already had. **Filter targets on `alive`, not on `hp > 0`**; `play_card` / `use_potion` already refuse a dead target.
  - `enemies[].revive` — present only when that corpse is scheduled to come back: `{ in_turns, hp, power_id, power_name }`. `in_turns` counts enemy turns until it heals itself back up (2 on the turn it dies, 1 on the turn before it stands up, when its `intents` also show `Heal`); `hp` is what it returns with. Both are `null` when they cannot be read. A corpse kept in the fight by a power that never revives it has `alive: false` and no `revive`. Markdown marks the enemy heading `— DEAD` and prints a `⚠ WAITING TO REVIVE in N enemy turn(s) at H HP` line.

Every card object carries `is_upgraded`, `is_upgradable`, `upgrade_level` and `max_upgrade_level` (a card can have more than one upgrade level, so `is_upgraded` does not mean "no upgrade left"). On the screens where an upgrade or a pick is being decided — `card_select` with `screen_type: "upgrade"`, `hand_select` with `mode: "upgrade_select"`, and `card_reward` — a card that still has an upgrade left also carries `upgrade_preview`: `{ name, cost, star_cost, description, keywords }` of the card as it would read after one upgrade, built from the game's own upgraded clone. It is absent everywhere else (combat hand, piles, deck list) to keep the payload small.

| `state_type` | Screen | Available Actions |
|---|---|---|
| `menu` | Main menu, menu submenu (incl. multiplayer host/join/load lobby), character select, `menu_screen: settings`, the in-run pause menu (`menu_screen: pause`) and the screens it opens (`menu_screen: compendium`, …), or a blocking FTUE/tutorial/popup that can also appear mid-run | `menu_select` |
| `unknown` | Unrecognized room or null state | None |
| `monster` / `elite` / `boss` | In combat | `play_card`, `use_potion`, `end_turn` |
| `hand_select` | In-combat card selection (exhaust, discard, upgrade) | `combat_select_card`, `combat_confirm_selection` |
| `rewards` | Rewards screen (post-combat or event-triggered) | `claim_reward`, `proceed` |
| `card_reward` | Pick a card to add to deck | `select_card_reward`, `skip_card_reward`, `select_card_reward_alternative` |
| `map` | Map navigation and annotation | `choose_map_node`, `map_draw`, `map_clear_drawings` |
| `event` | Event or Ancient encounter | `choose_event_option`, `advance_dialogue` |
| `rest_site` | Rest site | `choose_rest_option`, `proceed` |
| `shop` | Shop (auto-opens inventory) | `shop_open`, `shop_purchase`, `sell_potion`, `proceed` |
| `fake_merchant` | Fake Merchant event (relic-only shop) | `shop_open`, `shop_purchase`, `sell_potion`, `proceed` |
| `treasure` | Treasure room (auto-opens chest) | `claim_treasure_relic`, `proceed` |
| `card_select` | Deck card selection overlay (transform, upgrade, enchant, remove, choose-a-card) | `select_card`, `confirm_selection`, `cancel_selection` |
| `bundle_select` | Card bundle choice overlay | `select_bundle`, `confirm_bundle_selection`, `cancel_bundle_selection` |
| `relic_select` | Relic choice overlay (boss relics) | `select_relic`, `skip_relic_selection` |
| `crystal_sphere` | Crystal Sphere minigame | `crystal_sphere_set_tool`, `crystal_sphere_click_cell`, `crystal_sphere_proceed` |
| `game_over` | Run ended | `menu_select` with `main_menu` |
| `overlay` | Unhandled overlay (catch-all, prevents soft-lock) | None (manual interaction needed) |

**Note:** `use_potion` and `discard_potion` work during any state where potions are accessible (combat, map, events, etc.).

**Note:** the settings screen is reported as `state_type: menu`, `menu_screen: "settings"` wherever it is
open, with `in_run: true` when a run is paused underneath (`run` and `player` are still included there) and
`in_run: false` on the main menu. Its only option is `back`; changing settings values is not exposed.

**Note:** the in-run pause menu is reported as `state_type: menu`, `menu_screen: "pause"`, `in_run: true`
(`run` and `player` are still included), with one `{name, enabled}` option per visible button: `resume`,
`settings`, `compendium`, `give_up`, `disconnect`, `save_and_quit` (`give_up` / `save_and_quit` are hidden
for multiplayer clients, `disconnect` only appears for them). Open it with the `open_pause_menu` action.
Anything the pause menu opens on the same stack is reported the same way with a derived `menu_screen`
(e.g. `compendium`, `card_library`), `screen_class` naming the game's node type, and a single `back`
option — so a screen the API can enter is always a screen it can leave. While any of these is open,
gameplay actions are refused with an error instead of being applied to the room underneath.

## POST — Actions

All POST requests use a JSON body with an `"action"` field.

**Response contract — one shape for every POST.** Every POST response body carries a `status` field
that is either `"ok"` or `"error"`. A client only needs that one key:

| Outcome | HTTP | Body |
|---|---|---|
| Success | 200 | `{ "status": "ok", "message": "...", ... }` |
| Action failure (the action was understood but could not be performed) | 200 | `{ "status": "error", "error": "..." }` |
| Transport failure (invalid JSON, missing `action`, unknown route, wrong method, wrong run mode, unhandled exception) | 4xx / 5xx | `{ "status": "error", "error": "..." }` |

`error` is present if and only if `status` is `"error"`. GET responses return state payloads and
are not covered by this contract (`GET /` is the exception and reports `"status": "ok"`).

### Waiting for the Game to Settle

By default a POST returns as soon as the action has been dispatched, so the animation, card
resolution and redraw it triggers may still be running. Add `"wait": true` and the server does the
settle loop for you and answers with the settled state — one round trip instead of a GET poll loop.

| Parameter | Type | Default | Meaning |
|---|---|---|---|
| `wait` | bool | `false` | Wait for the game to settle before answering. Also accepts `1`, `"1"`, `"true"`, or the query string `?wait=1`. |
| `wait_timeout_ms` | int | `10000` | How long to wait, clamped to `500`–`30000`. |
| `include_state` | bool | `true` when `wait` is set | Include the settled state in the response. |

```json
{ "action": "end_turn", "wait": true }
```

Extra response fields, appended after the action's own keys:

| Field | Meaning |
|---|---|
| `waited_ms` | How long the wait took. |
| `polls` | How many state reads it took. |
| `settled` | `true` if the game came to rest, `false` on timeout. |
| `changed` | `false` means nothing observable moved — the action was a no-op. |
| `state` | The settled state, same shape as `GET /api/v1/singleplayer` (omitted when `include_state` is `false`). |
| `wait_error` | Only present if the wait itself failed; the action still ran. |

A state counts as settled when `is_resolving` is false and — in `monster` / `elite` / `boss` — the
battle is back in its play phase, and that has held for 3 consecutive polls (~180 ms). One poll is
not enough: a click takes effect a frame later, and the game looks idle for a frame between two
queued actions. If the state has not changed at all within the first 3 s the wait stops early and
returns `settled: true, changed: false`.

Nothing is waited on when the action returned `"status": "error"`. Singleplayer only —
`POST /api/v1/multiplayer` ignores these parameters.

### Errors

| Status | Meaning |
|---|---|
| `200` with `"status": "error"` | The action ran and was rejected (bad parameters, illegal move). Retrying the same request will fail the same way. |
| `500` | The action ran and threw. |
| `503` with `"retry": true` | **The action did NOT run.** The game's main thread did not answer within 10 s, or more than 32 requests are already queued — the game is not processing frames (scene load, hang, minimized/paused app). Body: `{ "error": "...", "status": "error", "retry": true }`. Nothing was applied to the run, so the request may be sent again once the game is responsive. |

This applies to GET endpoints too; every request is served from the game's main thread.

### Menu / Game Over

| Action | Parameters | When to Use |
|---|---|---|
| `menu_select` | `option`: string, `seed`?: string, `ascension`?: int | Choose an advertised menu option. Options are case-insensitive. Submenus include `back` where visible, including `profile_select` options `profile_1`, `profile_2`, `profile_3`, and `back`. Blocking popups expose normalized button labels such as `ignore` or `back`. On `settings` (opened with `settings` from the main menu, or from the pause menu during a run) the only option is `back` (aliases `close`, `resume`), which leaves the screen and returns to whatever was underneath; settings values themselves cannot be changed through the API. On `pause` the options are `resume` (aliases `back`, `close`, `continue`) to close the menu and return to the run, `settings` and `compendium` (both left again with `back`), `give_up` (alias `abandon`) which **abandons the run permanently** — it opens the game's confirmation popup, reported next as `menu_screen: "popup"`, and is never auto-confirmed, so answer it with `yes` / `no` — `save_and_quit` (alias `main_menu`) which saves and returns to the main menu, where `continue` resumes the run, and `disconnect` in multiplayer. On the screens the pause menu opens (`compendium` and below) the only option is `back`. `game_over` supports `main_menu` only; `continue` returns an error. Supplying `seed` in unsupported contexts such as standard singleplayer character select returns an error and does not start a run — a seeded run must go through `custom` (`main` -> `singleplayer` -> `custom` -> character ID -> `embark` with `seed`), whose screen is reported as `custom_run`. If Timeline has pending obtained epochs that require manual reveal, it may appear in `blocked_options`; selecting `timeline` returns `manual_action_required: true` with `pending_epoch_ids` instead of opening Timeline — use the `timeline_reveal_epochs` action to clear them. Multiplayer flow: on `multiplayer_join` use `refresh` / `back` / `join_<index>` / `join_<player_id>`. On `multiplayer_load_lobby` use `confirm` (or `embark`) to ready up, `unready` to retract, `back` to leave. On `character_select` while in MP, an additional `unready` option becomes available after readying, plus a `lobby` block in state lists ascension, all_ready, and per-player roster. `ascension` sets the ascension level on character select (applied before `option`, so it can ride along with the character pick or with `confirm`, or be sent alone with an empty `option`); character select state reports the current `ascension`, the unlocked `max_ascension`, and `ascension_selectable`. |
| `open_pause_menu` (alias `pause`) | (none) | Open the in-run pause menu (the top-bar gear / Esc), the only API route out of a run. Requires a run in progress; returns `already_open: true` if it was already up, and an error if the compendium or settings is stacked on top of it (leave that with `menu_select` `back` first) or if a card is still being played. The next state reports `menu_screen: "pause"`. ⚠️ From there `give_up` **abandons the run permanently** (it asks for confirmation first) and `save_and_quit` ends the session back at the main menu — neither is undoable from the API. |
| `timeline_reveal_epochs` | (none) | Reveal every obtained-but-unrevealed epoch. A finished run leaves epochs obtained, and the main menu hides `singleplayer` until they are revealed. Call from the main menu and repeat until the response has `done: true`; `pending_epoch_ids` lists what is left. Opens the Timeline, reveals each pending epoch through the game's own reveal path (granting its unlocks and writing progress), then returns to the main menu. Works with no run in progress. |

### Profiles

`GET /api/v1/profile` returns persistent progress for the active profile, including character stats, discoveries, achievements, epochs, and global run totals.

`GET /api/v1/compendium` returns the active profile grouped like the in-game Compendium:

When a run is active, the response includes `current_run.run_id` in `{save_scope}:profile{profile_id}:{start_time}` format. This identifies the specific run attempt, while `seed` identifies the generated run content.

| Section | Status |
|---|---|
| `card_library` | Discovered cards plus pick/skip/win/loss stats. Card rules text is supplied by game state when cards are visible in a run. |
| `relic_collection` | Discovered relic IDs. Profile data does not expose a typed per-relic description or obtained-count catalog. |
| `potion_lab` | Discovered potion IDs. Profile data does not expose a typed per-potion rules text or lab metadata catalog. |
| `bestiary` | Encounter/enemy profile fight stats. The game UI marks Bestiary as future/locked, so this is stats-only rather than a full enemy catalog. |
| `character_stats` | Per-character and global totals. |
| `run_history` | Summaries of the active profile's saved `saves/history/*.run` files, capped to the 20 most recent entries. |

Run history is resolved from the active Steam account's profile save root. `current_run.run_id` identifies one concrete attempt; `seed` identifies generated run content and can repeat across attempts.

`GET /api/v1/wiki?query=<text>&item_type=<all|card|relic>&limit=<n>&scope=<discovered|all>` searches or lists card and relic entries. With a `query` it fuzzy-ranks matches; with no query it LISTS everything in scope in name order (a higher internal limit applies). `item_type` defaults to `all`; `limit` defaults to 10 when searching and to the whole list otherwise. `scope` defaults to `discovered` (the active profile's own progress); `scope=all` covers every card and relic the game defines and is for tooling — a card catalog needs it, because an undiscovered card still has a fully resolved description. Every result carries a `discovered` flag either way. Card results include both `base` and `upgraded` objects when the card can be upgraded.

Example searches:

- `/api/v1/wiki?query=ironclad%20perfect%20strike&item_type=card`
- `/api/v1/wiki?query=silver%20spoon&item_type=relic&limit=5`
- `/api/v1/wiki?scope=all&item_type=card` (whole card list, discovery gate off)

`GET /api/v1/cardpools` returns every unlocked card pool (all five characters plus the shared Colorless, Curse, Deprecated, Event, Quest, Status and Token pools), each card carrying `id`, `name`, `type`, `rarity`, its own `pool` and `can_be_generated_in_combat`. Added in 0.5.0, replacing the five per-state `combat_*_pool` fields. **Card order is load-bearing** — the game's `Rng.NextItem` indexes straight into these lists — so never sort them. The payload is static for a run but not across runs (multiplayer unlock state is the union over all players), so fetch it at each run start.

`GET /api/v1/potionpools` returns every unlocked potion pool, each potion carrying `id`, `name`, `rarity` and `can_be_generated_in_combat`, plus a `characters` map (character title -> its pool id) and `shared_pool`.

Added in 0.5.5. **Order is load-bearing** — `PotionFactory.CreateRandomPotion` does `rng.NextItem(list.Where(rarity))` over the unlocked pool — so never sort them. `GetPotionOptions` concatenates the character's own pool with the shared one **in that order**, which is why both the per-character mapping and the shared pool id are exported: a client has to rebuild the same list to index into it.

Static for a run but not across runs (multiplayer unlock state is the union over all players), so fetch it at each run start — the same lifecycle as `/api/v1/cardpools`, unlike the mutating `/api/v1/relicbag`.


`GET /api/v1/relicbag` returns each player's **relic grab bag**: the ordered deques (`common` / `uncommon` / `rare` / `shop`, plus `mp_fallback`) used by individual reward factories and merchants. Treasure rooms draw from a separate shared bag. The `mp_fallback` list is always present, including when empty; an unreadable queue refuses the bag export.

```json
{"relic_bag_generation": 42,
 "players": [{"net_id": 0, "slot_index": 0, "is_me": true,
              "deques": {"common": ["AKABEKO", "ANCHOR"], "rare": ["..."], "shop": ["..."]}}]}
```

Added in 0.5.5. **Order is load-bearing, and so is both ends of it**: reward sources take the FRONT (`RelicFactory.PullNextRelicFromFront` — elites, chests, events, ancients) while a merchant takes the BACK (`MerchantRelicEntry` → `PullNextRelicFromBack`, filtered to `IsAllowedInShops`). Never sort them.

Deliberately not called `relicpools`, for all that it sits beside `/api/v1/cardpools`: a card pool is static for a run and is fetched once, while this is a **bag** that shrinks every time a relic leaves it. Do not cache across rooms — fetch it when planning a route and again when a shop opens.

The bag is per **player** (each draws from their own deques, which is what multiplayer needs). `RunState.SharedRelicGrabBag` is deliberately not exported: every use of it in the game is a `Remove()` and nothing ever pulls from it, so it is a de-duplication ledger rather than a source. A relic that was pulled and then *declined* is gone from these lists without ever appearing in `player.relics`, which is why the lists are exported rather than inferred from what the player holds.

For a consistent multiplayer planning snapshot, request
`/api/v1/multiplayer?format=json&detail=full`, then `/api/v1/relicbag`.
Both responses carry `relic_bag_generation`; join by `slot_index` and retry if
the generations differ. Reading the generation is constant-time and the main
multiplayer endpoint never serializes or hashes relic bags.


`GET /api/v1/profiles` returns the three profile slots:

```json
{
  "current_profile_id": 1,
  "profiles": [
    { "id": 1, "is_current": true, "has_data": true },
    { "id": 2, "is_current": false, "has_data": false },
    { "id": 3, "is_current": false, "has_data": true }
  ]
}
```

`POST /api/v1/profiles` supports:

| Action | Parameters | When to Use |
|---|---|---|
| `switch` | `profile_id`: 1-3 | Switch through the game profile UI. Empty slots can be used for fresh-profile testing. Cannot be used during a run. |
| `delete` | `profile_id`: 1-3 | Delete an inactive profile slot. The active profile is rejected. |

### Combat

Card objects include `deck_card_index`: the current zero-based master-deck index of the card itself (in the Deck pile) or its `DeckVersion` (in combat). It is `null` when no current deck entry exists. This position can change after deck edits; it is not a permanent ID. `has_deck_version` separately reports whether a parent pointer exists, including a parent removed from the deck.

Guilty card objects include `combats_seen`, its saved completed-fight counter (normally 0 through 4). `AfterCombatEnd` increments this field only for the master card in the Deck pile, removing it at 5. Combat copies retain their own saved value. The description shows `5 - combats_seen`, with singular `combat` at one remaining fight.

A `monster` / `elite` / `boss` state carries `battle` only while the fight is
live. `combat_starting: true` (no `battle`) means the room is entered but the
fight has not started yet — poll, don't act. `"Combat ended. Waiting for
rewards..."` (no `battle`) is the corresponding tail end.

| Action | Parameters | When to Use |
|---|---|---|
| `play_card` | `card_uid`?: string, `card_index`?: int, `target`?: string | Play a card from hand. Identify the card by `card_uid` (from state, e.g. `"STRIKE#2"`) — it names the card instance and stays valid for the whole combat — or by `card_index`, which shifts whenever a card leaves the hand. `target` is an `entity_id` (`"JAW_WORM_0"`) or a `combat_id` (`3`), required for single-target cards unless exactly one enemy is alive, in which case it is chosen automatically. MP `AnyAlly` cards take an optional teammate id (`players[].entity_id`, e.g. `"player_1"`; never self; defaults to lowest-HP living teammate). |
| `use_potion` | `slot`: int (aliases `potion_index`, `index`), `target`?: string | Use a potion. `slot` is the belt slot from state `player.potions[].slot`, not a position in the list. `target` (an `entity_id` or `combat_id`) is required for enemy-targeting potions unless exactly one enemy is alive, in which case it is chosen automatically; a targeted potion is never consumed against nothing. For `AnyPlayer` potions it may also be a teammate id (defaults to self — pass a teammate id to throw it to them). Works outside combat for non-combat-only potions. |
| `sell_potion` | `slot`: int (aliases `potion_index`, `index`) | Sell a potion to the merchant by using it on them (Foul Potion's 100-gold mode). Only valid in a shop or fake merchant, and only for potions with a merchant interaction; shop state lists those under `sellable_potions`. |
| `discard_potion` | `slot`: int | Discard a potion to free up the slot. Use when slots are full and you need room for incoming potions. |
| `end_turn` | _(none)_ | End the player's turn. |

### In-Combat Hand Selection (`hand_select`)

| Action | Parameters | When to Use |
|---|---|---|
| `combat_select_card` | `card_index`: int | Select/deselect a card during "choose a card to exhaust/discard" prompts. |
| `combat_confirm_selection` | _(none)_ | Confirm the hand card selection. |

When `mode` is `upgrade_select`, each selectable card with an upgrade left carries `upgrade_preview` (its upgraded form).

### Rewards (`rewards`)

| Action | Parameters | When to Use |
|---|---|---|
| `claim_reward` | `reward_id`: string (推奨)、または `index`: int + `screen_revision`: string | 現在の有効報酬を指定。古い revision、ID/index の矛盾、無効 ID はエラー。revision なしの旧 index は現在位置として互換受理。card は三択を開く。 |
| `proceed` | _(none)_ | Leave the rewards screen. |

各 item に `reward_id` / `selection_state` / `claimable` / `skipped` /
`reward_consumed` / `last_selection` / `last_option_id` を追加。
`rewards.screen_revision` は一覧と選択状態の版、`selection_history` は同じ画面内の取得済みを含む ID と状態。
ID は金取得で index が詰まっても固定。画面終了・マップ表示・別報酬画面・ラン変更で破棄し、再起動でも無効になる。
`selection_state` は `unopened / opened / skipped / claimed / unknown`。観測欠落は unknown。
既存キーの変更はなく、schema は 3 のまま。詳細は raw-full.md の報酬 ID 契約を参照。

### Card Reward (`card_reward`)

| Action | Parameters | When to Use |
|---|---|---|
| `select_card_reward` | `card_index`: int | Pick a card to add to deck. |
| `skip_card_reward` | _(none)_ | Skip the card reward (if allowed). Picks the `Skip` option by id, not whichever button is first — a relic can add its own alternative, and skipping does not take it. |
| `select_card_reward_alternative` | `option_id`: string (or `index`: int) | Take one of the card reward's alternative options, listed in state under `card_reward.alternatives`. `Skip` is one; relics add others (Pael's Wing adds `SACRIFICE`, which turns the reward into progress towards a relic — skipping does **not** count as sacrificing). |

Each offered card with an upgrade left carries `upgrade_preview`, so the upgraded form can weigh into the pick.

`card_reward.parent_reward_id` と `selection_state` で親報酬を識別する（不明なら null／unknown）。
Skip は三択を閉じるが親報酬を消費しない。応答に親 ID と
`closes_selection_without_consuming_reward:true` を追加。`action_stage:submitted` は送信段階で、
`wait:true` 後に `outcome_observed:true` / `selection_closed:true` / `reward_consumed:false` を確認する。
Skip 後も `claimable:true, skipped:true` となり再開封できる。
SACRIFICE の完了、REROLL の継続、中断はそれぞれ別に記録する。GET では状態を消去しない。

### Map (`map`)

`map.boss` includes the upcoming boss coordinate plus stable `id` and readable `name` fields when the run map exposes boss identity. `map.bosses` contains all boss entries for maps with more than one boss. The boss is shown on the game's own map, so reporting it reveals nothing the player cannot see.

`map.encounters_seen` lists what **this act has already served** — the rooms entered so far, grouped as `monster` / `elite` / `event`, each with the 1-based `floor` within the act, the encounter `id` and `name`, and `monsters` (the line-up the encounter actually resolved to, which differs between rooms sharing an id). A room entered from an event carries `via_event: true`. `elite_cycle` appears only once the act's elite pool has been exhausted at least once, and says how many complete passes have happened — an elite already fought does not come back until the pool wraps, so this is the field to read for "can that elite show up again". It mirrors what the game shows when the player hovers a travelled node on the map.

> **There is deliberately no "what comes next".** A previous version exposed `map.next_encounters`, read out of the act's pre-rolled room list. The game does not reveal the next monster / elite / event until the room is entered, so that field let a client route around matchups no human could see; it was removed in schema 3. For the same reason `encounters_seen` never reports the *unused* pool — listing it would give the next encounter away by elimination.

`map.point_history` lists the current act's visited points with the rooms they resolved into — the only place a `?` reports what it actually became. The top-level `act` block carries the act's pre-rolled encounter and event queues, boss identity, and consumed counters, so a client can name every fight or event behind a map node; `run.visited_event_ids` and `profile.number_of_runs` are the extra inputs needed to replay the game's event skip and its first-run `?` override.

`profile.first_run_ironclad_present` reports whether any party Ironclad has zero completed runs, using each player's own unlock state. Lasting Candy eligibility reads this fact; multiplayer `profile.number_of_runs` is the shared maximum and cannot substitute for it.

Every entry in `map.nodes` includes a live `draw_position` in the documented
`map.drawing_coordinate_space`. These positions include the map's random visual
jitter. Existing strokes are deliberately excluded from game state; use
`GET /api/v1/mapdrawings`, which returns strokes grouped by player plus the same
live node anchors.

| Action | Parameters | When to Use |
|---|---|---|
| `choose_map_node` | `index`: int | Travel to a node from `next_options`. Empty (and rejected) while the current room is unfinished or a travel is already resolving — see `map.travel_enabled` / `map.travel_in_flight`. |
| `map_draw` | `strokes`: array | Add native drawing/eraser polylines. Points may be normalized `{x,y}` values or live node references `{col,row}`. Saved and broadcast in multiplayer. |
| `map_clear_drawings` | _(none)_ | Clear only the local player's drawings; broadcast reliably in multiplayer. |

### Event (`event`)

| Action | Parameters | When to Use |
|---|---|---|
| `choose_event_option` | `index`: int | Choose an event option by index from state. Also used for "Proceed" options. Locked options error; so do options that are already `was_chosen`, not `is_enabled`, or picked while a room transition is resolving — poll for the next state rather than retrying. |
| `advance_dialogue` | _(none)_ | Click through Ancient dialogue until `in_dialogue` is false. |

### Rest Site (`rest_site`)

| Action | Parameters | When to Use |
|---|---|---|
| `choose_rest_option` | `option_id`: str (aliases `id`, `option`) — **preferred**; or `index`: int (alias `option_index`) | Choose rest, smith, or another option. `option_id` matches `rest_site.options[].id` case-insensitively (`HEAL`, `SMITH`, `DIG`, …). `index` is `rest_site.options[].index`, i.e. the position in the game's option list — *not* the on-screen button order. Both are resolved to the same model option and must agree if both are sent. Disabled options are refused; errors list every option as `[index] id (enabled/disabled)`. Replies with `option_id` and `index` of what was clicked. |
| `proceed` | _(none)_ | Leave the rest site. |

`rest_site.options[]` carries `index`, `id`, `name`, `description`, `is_enabled` and `has_button`
(false when the option exists in the run model but no button is drawn yet — markdown appends
`(no button on screen)` in that case, and `choose_rest_option` refuses it instead of pressing a neighbour).

### Shop (`shop`)

Card entries report `is_upgraded`, `enchantment`, and `enchantment_state` alongside
their card text and costs. These describe the offered card after relic effects;
refresh the state after a purchase because those effects can update the inventory.

| Action | Parameters | When to Use |
|---|---|---|
| `shop_open` | _(none)_ | Open the inventory. State queries never open it automatically. |
| `shop_purchase` | `index`: int | Buy an item by its index after opening the inventory. Must be stocked and affordable. |
| `proceed` | _(none)_ | Leave the shop. |

### Treasure (`treasure`)

| Action | Parameters | When to Use |
|---|---|---|
| `claim_treasure_relic` | `index`: int | Claim a relic from the opened chest. |
| `proceed` | _(none)_ | Leave the treasure room. |

### Card Selection Overlay (`card_select`)

In combat, the payload includes the full top-level `battle` snapshot. The overlay also reports `selection_effect`,
`selection_surface`, `source_pile`, origin metadata, selection bounds, and a stable `combat_card_id` on cards. Clients
can therefore model the actual continuation (move, exhaust, transform, copy, retain, and so on) instead of inferring
it from localized prompt text. Unknown continuations are reported as `origin_specific`.

| Action | Parameters | When to Use |
|---|---|---|
| `select_card` | `index`: int | Grid screens: toggle card selection. Choose-a-card: pick immediately. |
| `confirm_selection` | _(none)_ | Confirm (for grid screens with preview). Not needed for choose-a-card. |
| `cancel_selection` | _(none)_ | Cancel preview, skip (choose-a-card), or close screen. |

On the upgrade screen (`screen_type: "upgrade"`), each card with an upgrade left carries `upgrade_preview` (its upgraded form). The transform/remove screens do not.

**Selection state.** Every grid screen reports what it currently holds selected, so a caller never has to
count its own presses:

| Field | Meaning |
|---|---|
| `cards[].selected` | Whether that card is in the screen's selection. Stays `true` while a preview is open, where the game drops the grid highlight but keeps the card. |
| `selected_count` | How many cards the screen has selected. |
| `required_count` | The `N` of a "select N cards" prompt, or `null` when the screen accepts a range. |
| `min_select` / `max_select` | The two ends of that range (equal when `required_count` is set). |
| `selected_outside_grid` | Only present when the grid is scrolled and a selected card is outside the `cards[]` window. |

**These screens have two steps.** Picking cards comes first; then a preview panel opens - by itself once
`max_select` is reached, or when `confirm_selection` presses the screen's own confirm - and only the preview's
own confirm applies the selection. `confirm_selection` says which of the two it did
(`"Selection preview opened … call confirm_selection again to apply it"` vs `"Confirming selection…"`), and
`preview_showing` says where the screen stands.

While a preview is open the game takes the grid out of the input path, so `select_card` returns `status: error`
rather than pressing through it (pressing through appends duplicate cards to the preview row).
`cancel_selection` closes the preview **and clears the selection**, putting you back on an empty grid.

`confirm_selection` returns `status: error` - instead of an `ok` that does nothing - when the selection is
short of `min_select`, over `max_select`, or when the press was swallowed by the screen.

### Bundle Selection Overlay (`bundle_select`)

| Action | Parameters | When to Use |
|---|---|---|
| `select_bundle` | `index`: int | Open a bundle preview. |
| `confirm_bundle_selection` | _(none)_ | Confirm the previewed bundle. |
| `cancel_bundle_selection` | _(none)_ | Cancel the bundle preview. |

### Relic Selection Overlay (`relic_select`)

| Action | Parameters | When to Use |
|---|---|---|
| `select_relic` | `index`: int | Pick a relic (immediate). |
| `skip_relic_selection` | _(none)_ | Skip the relic choice. |

### Crystal Sphere (`crystal_sphere`)

| Action | Parameters | When to Use |
|---|---|---|
| `crystal_sphere_set_tool` | `tool`: `"big"` or `"small"` | Switch divination tool. |
| `crystal_sphere_click_cell` | `x`: int, `y`: int | Reveal a cell. |
| `crystal_sphere_proceed` | _(none)_ | Finish the minigame. |

## Multiplayer Additions

`GET /api/v1/multiplayer?detail=full` expands only the top-level `players`
array. Each entry adds `slot_index`, string `net_id`, locale-independent
`character_id`, master `deck`, `relics`, `potions`, `max_potion_slots`,
persistent `status`, and player RNG. Nested room-local player lists stay compact.
The default `detail=summary` retains the normal low-latency response.

`POST /api/v1/multiplayer` supports all singleplayer actions plus:

| Action | Parameters | When to Use |
|---|---|---|
| `end_turn` | _(none)_ | Vote to end turn. Turn ends when all players vote. |
| `undo_end_turn` | _(none)_ | Retract end-turn vote (before all players committed). |

MP battle state additionally exposes the full combat history for ALL players
(lockstep sync makes remote plays visible locally):

- `battle.card_plays` — every card play this combat: `index` (stable
  per-combat sequence for polling dedup), `round`, `player`, `is_local`,
  `card_id`, `card_name`, `target`.
- `battle.player_damage` — per player: `by_round` (unblocked HP damage dealt
  to enemies per round, index 0 = round 1) and `total`.


### Fixed card returns and card object identity

Mutable cards include `card_object_id`, an opaque positive integer stable for that physical object during the mod session. This is separate from the shifting `deck_card_index` and is not an action target. Card references in power `runtime` also include `deck_version`: the complete saved master card or `null`. For Swipe, the removed combat face and its master can have different upgrades or saved properties.

`battle.special_card_rewards` lists pending local-player fixed-card rewards, including cards returned when Hopper dies. Each entry has `type: "special_card"`, `card_reference` (the master's `card_object_id`), `card` (the full master snapshot), and `encounter_source`. Reward-screen items of type `special_card` expose the same fields. A returned card remains outside the deck until `claim_reward` succeeds; repeated entries can refer to the same physical card. This list survives the dead enemy's power cleanup. An empty list means no pending fixed-card rewards.

`battle.allies` lists the player's side of the board — living, non-player creatures such as Osty — in the same shape as `battle.enemies`, so one target lookup handles both. `battle.next_creature_id` is the combat id the next created creature receives; an id equal to it names a creature summoned this turn.
