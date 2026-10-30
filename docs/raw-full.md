# Full API Reference

HTTP API served by the Sts2Mcp mod on `localhost:15526`. No authentication. Local use only.

**Endpoints:**
- `GET  /` — health check: `{ "message", "status", "version", "commit", "schema_version" }`
- `GET  /api/v1/singleplayer` — read current game state
- `POST /api/v1/singleplayer` — perform a game action
- `GET  /api/v1/multiplayer` — read multiplayer game state
- `POST /api/v1/multiplayer` — perform a multiplayer action
- `GET  /api/v1/settings` — read settings and preferences
- `GET  /api/v1/profile` — read current profile progress
- `GET  /api/v1/compendium` — read Compendium-shaped profile progress
- `GET  /api/v1/bestiary` — read monster and encounter metadata
- `GET  /api/v1/glossary/cards` — read active-run card pool metadata
- `GET  /api/v1/glossary/relics` — read active-run relic pool metadata
- `GET  /api/v1/glossary/potions` — read active-run potion pool metadata
- `GET  /api/v1/glossary/keywords` — read active-run keyword metadata
- `GET  /api/v1/profiles` — list profile slots
- `POST /api/v1/profiles` — switch or delete profile slots
- `GET  /api/v1/record` — read combat recorder status
- `GET  /api/v1/record/inspect` — inspect a recorded combat replay
- `GET  /api/v1/wiki` — fuzzy-search, or list, card/relic wiki entries
- `GET  /api/v1/cardpools` — every unlocked card pool in roll order (static per run)
- `GET  /api/v1/potionpools` — every unlocked potion pool in roll order
- `GET  /api/v1/relicbag` — the generated relic bag
- `GET  /api/v1/mapdrawings` — read saved map strokes and live node anchors

HTTP error responses include `status: "error"` and `error`; route, validation, read-endpoint, and action failures include `error_code`.
Route-level failures include `error_code: "method_not_allowed"` for unsupported HTTP methods, `error_code: "not_found"` for unknown paths, and `error_code: "internal_error"` for unexpected top-level handler failures.
POST validation failures use stable `error_code` values where possible, including `invalid_json`, `missing_action`, `invalid_action_type`, `missing_profile_id`, `invalid_profile_id_type`, and `invalid_action_payload`.
Action failures include `error_code`; endpoint-specific conflicts keep their specific codes, while older generic action failures use the stable fallback `action_error`.
Read endpoint startup/data-availability failures use non-2xx structured errors where possible, including `save_manager_unavailable`, `settings_data_unavailable`, and `profile_data_unavailable`.
Read endpoint exceptions include endpoint-specific `error_code` values such as `settings_read_failed`, `bestiary_build_failed`, `glossary_build_failed`, `profile_build_failed`, `profiles_read_failed`, `compendium_build_failed`, `singleplayer_state_read_failed`, and `multiplayer_state_read_failed`.

`GET /` returns the API index with `status: "ok"`, `kind: "api_index"`, `version`, `endpoint_count`, `bound_prefixes`, and the advertised endpoint list.

Save/path context fields are normalized with forward slashes in JSON responses, including Windows absolute paths.

The singleplayer and multiplayer endpoints are mutually exclusive: calling singleplayer during a multiplayer run (or vice versa) returns HTTP 409. The mismatch response includes `error_code`: `multiplayer_run_active` when singleplayer is called during a multiplayer run, or `not_multiplayer_run` when multiplayer is called outside one.

---

## GET — Read Game State

### Query Parameters

| Parameter | Values                                        | Default   | Description                                                     |
|-----------|-----------------------------------------------|-----------|-----------------------------------------------------------------|
| `format`  | `json`, `markdown`                            | `json`    | Response format                                                 |
| `pretty`  | `1`, `true`, `yes` (or present with no value) | off       | Indent the JSON. Off by default — compact output is ~32% smaller |
| `detail`  | `summary`, `full`                             | `summary` | Multiplayer only. `full` adds every player's master deck, relics, potions, status, stable IDs, and player RNG to the top-level `players`; unknown values return HTTP 400. |
| `values`  | `powered`, `unpowered`                        | `powered` | Whether card descriptions and enemy intent labels arrive with the player's standing powers and relics baked into their numbers; unknown values return HTTP 400. |

JSON responses are compact by default; `pretty` opts into indentation for that one request. It works on
every JSON-returning endpoint, on POST as well as GET (e.g. `POST /api/v1/singleplayer?pretty=1`), and on
error responses. It has no effect on `format=markdown`.

`detail=full` is opt-in so ordinary multiplayer polling keeps the compact
response and its existing latency. Room-local `map.players`, `event.players`,
and `treasure.players` arrays remain summaries even in a full response; only
the top-level `players` array expands.

### Powered vs unpowered values

The game renders a card's description through its damage/block pipeline, so a
live card's printed numbers already carry the player's standing Strength,
Dexterity, Vigor, Focus and Weak plus every additive relic bonus. Enemy intent
`label` strings are baked the same way, and additionally carry target Vulnerable
and the Back Attack x1.5 multiplier. A consumer that then applies those
modifiers itself double-counts them.

`values=unpowered` renders the numbers at **base + enchantment** instead:

- Each card gains `"text_baked": false`. The key is absent in `powered` mode, so
  an existing consumer sees a byte-identical payload by default.
- Each enemy intent whose label was actually rebuilt gains `"unbaked": true`. If
  the label could not be rebuilt the game's own powered label is kept and the
  marker is **not** emitted, so the flag never over-promises.
- The response echoes `"values"` at the top level.

An enchantment counts as part of the card, exactly as the game treats it, so
enchanted values stay applied in both modes.

Unaffected by this parameter, because none of it comes from the description's
value pipeline:

- `keywords` / `keyword_ids`, including single-turn Retain and Sly grants and
  keywords a relic or upgrade adds or removes (a relic that strips Exhaust still
  strips it).
- The enchantment and affliction text lines, and the replay-count line.
- `cost` / `star_cost`, which stay hook-modified: a relic that makes a card free
  still reports `0`.
- Orb `passive_val` / `evoke_val`, which use the orb model's current Focus and
  relic hooks. Glass also exports its stored `base_passive_val` before those hooks.
- Intent `title` / `description` hover text, which still carries the powered
  damage number in its wording.

`values=unpowered` also makes non-hand piles deterministic. A card's value set is
shared UI state that only the card node writes, so in `powered` mode a draw-pile,
shop or reward card reports whatever the last on-screen render happened to leave
there. The unpowered path sets the values explicitly before rendering.

Unsupported `format` values return HTTP 400 with `error_code: "invalid_format"` instead of falling back to JSON.

### Common Top-Level Fields

Every response — `menu` and error-ish states included — carries the build stamp and the game's own
release version:

```jsonc
{
  "state_type": "...",      // Screen identifier (see sections below)
  "mod_version": "0.4.0",   // Mod version
  "mod_commit": "2de67cd",  // Git commit built from ("<sha>-dirty" from an edited tree); null if unknown
  "schema_version": 3,      // Contract version — see below
  "game_version": "0.108.2" // From the game's release_info.json; omitted when the
                            // build ships without one
}
```

`schema_version` is bumped **only** when an existing field or action changes meaning or is removed.
Purely additive changes (new fields, actions, parameters, `state_type`s) do not bump it, so
feature-detect additions by presence and use `schema_version` only to notice a breaking change.

- **3** — `map.next_encounters` was removed because it exposed future rooms before the player
  could see them. `map.encounters_seen` reports only travelled encounters.
- **2** — `battle.enemies` is no longer "the living enemies": a corpse the game keeps in the
  fight (a Decimillipede segment waiting to reattach) is listed with `alive: false`. Filter on
  the new `alive` field instead of assuming every entry is a valid target.
- **1** — initial contract.

Every state JSON response also includes `status: "ok"` and `kind` (`singleplayer_state` or
`multiplayer_state`), so a client can tell a successful read from an error body without
inspecting the HTTP status. Active-run states include run, current-run, and local-player context.

Every response except `menu` additionally includes these top-level fields alongside the
state-specific data:

```jsonc
{
  "status": "ok",
  "kind": "singleplayer_state",
  "state_type": "...",      // Screen identifier (see sections below)
  "run": {
    "act": 1,               // Current act (1-indexed)
    "floor": 3,             // Total floors visited
    "player_count": 1,
    "card_multiplayer_constraint": "SingleplayerOnly",
    "ascension": 0,         // Ascension level
    "visited_event_ids": ["WELLSPRING"]  // Events already seen this run;
                            // RoomSet.EnsureNextEventIsValid skips past these
  },
  "act": {                  // The act's pre-rolled room queues (ActModel._rooms)
    "index": 0,             // 0-based act index
    "id": "OVERGROWTH",
    "rooms_total": 15,
    "floors_total": 17,
    "encounters": {
      // Consumed in order: PullNextEncounter is list[visited % count], so these
      // name the exact fight behind every Monster and Elite node on the map.
      "normal": ["NIBBITS_WEAK", "SLIMES_WEAK", "MAWLER_NORMAL"],
      "normal_visited": 1,
      "elite": ["BYRDONIS_ELITE", "PHROG_PARASITE_ELITE"],
      "elite_visited": 0,
      // NOT already skip-adjusted: replay EnsureNextEventIsValid yourself using
      // run.visited_event_ids, since doing it here would mutate run state.
      "events": ["SAPPHIRE_SEED", "WELLSPRING"],
      "events_visited": 0,
      "boss": "THE_KIN_BOSS",
      "second_boss": null, // Present only when the act has a second boss
      "boss_visited": 0
    }
  },
  "profile": {
    // UnknownMapPointOdds.Roll forces the first three "?" rooms on a profile
    // that has never finished a run, consuming no RNG while it does.
    "number_of_runs": 42,
    // True if any party Ironclad has UnlockState.NumberOfRuns == 0.
    // Lasting Candy uses this per-player fact, not the shared party maximum.
    "first_run_ironclad_present": false
  },
  "current_run": {
    "is_in_progress": true,
    "profile_id": 1,
    "progress_path": "modded/profile1/saves/progress.save",
    "resolved_progress_path": "C:/Users/timot/AppData/Roaming/SlayTheSpire2/steam/<account>/modded/profile1/saves/progress.save",
    "profile_root": "modded/profile1",
    "save_scope": "modded",
    "id_format": "{save_scope}:profile{profile_id}:{start_time}",
    // Save-backed fields below are present when current_run.save exposes them.
    "run_id": "modded:profile1:1778295706",
    "start_time": 1778295706,
    "seed": "2450ZAR9EF"
  },
  "player": { ... },        // Full player state (see Player Object below)
  // ... state-specific fields
}
```

### 解放済みの戦闘生成 pool（追加、schema 3）

`player.generation_pool` は単独プレイとマルチのローカル `player` に入る。
`BuildPlayerState` と同じ snapshot で、その Player 自身の `UnlockState` を使う。
対象の照合用に `player.character_id` / `player.player_id`、`run.player_count` /
`run.card_multiplayer_constraint` も追加した。ID は表示名や配列添字ではない。

```jsonc
"generation_pool": {
  "character_id": "IRONCLAD",
  "player_id": "1", // Player.NetId.ToString()
  "players": 1,
  "multiplayer_constraint": "SingleplayerOnly",
  "known": true,
  "cards": [ { "id": "UPPERCUT", "type": "Attack" } ], // 例示。実際には全候補
  "game_assembly_sha256": "9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4",
  "filter_version": 1,
  "filter_source": "CardPoolModel.GetUnlockedCards(Player.UnlockState,IRunState.CardMultiplayerConstraint);CardFactory.FilterForPlayerCount;CardFactory.FilterForCombat;OrobicAcid.OnUse:Attack,Skill,Power"
}
```

`GetUnlockedCards` の結果に実人数の filter、`CardFactory.FilterForCombat`、
Attack/Skill/Power の型 filter を適用し、ID の昇順で出す。`FilterForPlayerCount` は private
なので、人数 > 1 では SingleplayerOnly、そうでなければ MultiplayerOnly を除く同じ条件を実装した。
抽選、戦闘カード作成、RNG の読み取りはしない。ID と型だけを載せ、本文・コストは複製しない。
`game_assembly_sha256` は実際にロードした `CardModel` の assembly ファイルから初回に計算して保持する。
`filter_version` はこの pool 契約の版であり、state の schema とは別。

取得中に例外が出た場合は `known: false, cards: []` と `reason`（例外の型とメッセージ）、
state の `warnings` を返す。取得前に失敗したメタデータは欠落しうる。部分一覧を known=true にしない。
旧 MOD にはこの欄が無い。取得不能と空一覧を同一視せず、欄の存在を検出すること。
Markdown は単独・マルチのローカル player について件数・人数、または unknown と理由の1行だけを出す。
追加項目なので schema は 3 のまま。

出典は sts2-9cb4f1ad の `OrobicAcid.OnUse`、`CardPoolModel.GetUnlockedCards`、
`CardFactory.FilterForCombat/FilterForPlayerCount`。これは discovered_cards とは別の情報。
`NCardLibraryGrid.GetCardVisibility` は profile の解放条件と発見条件を分け、解放済みでも未発見なら
NotSeen にする。図鑑がこのラン固有の候補ID一覧をそのまま見せるとは言えない。
解放対象札は各 Epoch の `QueueUnlocks` → `NTimelineScreen.QueueCardUnlock` →
`NUnlockCardsScreen.Open` で表示される。保存ランの `Player.UnlockState` は profile の現在値と
異なりうる（`UnlockState` の契約、`Player.FromSerializable`）。未来の抽選結果・山札順は含まない。

### Player Object

Always present at the top level (except `menu`). Contains everything about the local player.

```jsonc
{
  "character": "The Ironclad",
  "character_id": "IRONCLAD", // Stable model ID (not localized)
  "player_id": "1",          // Player.NetId as a string
  "turn_phase": "Play",   // None, Start, AutoPrePlay, Play, AutoPostPlay, End (per player).
  "block_gains_this_turn": [], // Owner Move Block history: CardPlay identity per entry, null for no CardPlay.
  "effect_depth": 0,       // Active card/potion bodies, including suspended and nested executions.
                            // Read is_resolving for the surrounding animation/settlement window as well.
  "hp": 72,
  "max_hp": 80,
  "block": 0,
  "gold": 99,

  // --- Combat-only fields (present only when CombatManager.IsInProgress) ---
  "energy": 3,
  "max_energy": 3,
  "turn_number": 4,         // This player's turn count, extra turns included (battle.round only counts normal side switches).
                            //   Use it as the "a new player turn began" edge: is_play_phase alone is unreliable, since with
                            //   Instant Mode the enemy phase can start and finish between two polls.
  "stars": 2,               // Regent only; omitted if 0 and character doesn't always show stars
  "hand": [ /* Card Objects */ ],
  "draw_pile_count": 15,
  "discard_pile_count": 3,
  "exhaust_pile_count": 1,
  "play_pile_count": 0,
  "attacks_played_this_turn": 2,   // Attack cards this player finished playing this turn (Finisher-style counters)
  "stars_gained_this_turn": 2,     // Stars GAINED this turn (Radiate). The positive StarsModifiedEntry sum - neither the
                                   //   current "stars" total nor the Stars spent, since a turn can gain and spend the same Star.
                                   //   Added in 0.5.4; omitted by older builds, which clients should read as 0.
  "unblocked_hits_taken_this_combat": 3,  // Times this player took unblocked damage THIS COMBAT (Tear Asunder). A count of
                                   //   events, not HP lost; a fully blocked hit does not count. Added in 0.5.4.
  "damage_not_fully_blocked_this_owner_turn": true,  // Whether this player received damage that was NOT fully blocked
                                   //   during their current owner turn, enemy side included (Emotion Chip's
                                   //   HappenedLastPlayerTurn query at the next turn start). Unlike lost_hp_this_turn it is
                                   //   true when Buffer, Tungsten Rod or Osty prevented the HP loss, and for unblockable
                                   //   damage. Also on teammate entries in "players". Omitted when the history is unavailable.
  "exhausted_this_turn": true,     // true if any of this player's cards exhausted this turn (Forgotten Ritual)
  "hand_will_flush": true,         // whether the end-of-turn flush will run at all. A hook can suppress it, in which
                                   //   case the WHOLE hand is kept and no card left in it is headed for the discard pile.
  "draw_pile": [ /* Pile Card Objects, in true order (index 0 = next draw; valid until the next shuffle event) */ ],
  "discard_pile": [ /* Pile Card Objects */ ],
  "exhaust_pile": [ /* Pile Card Objects */ ],
  "play_pile": [ /* Pile Card Objects, in Play-pile order: executing cards and reserved autoplays */ ],
  "orbs": [ /* Orb Objects */ ],   // Defect only; omitted if orb capacity is 0
  "orb_slots": 3,
  "orb_empty_slots": 1,
  "pets": [                        // Necrobinder only; omitted if no pets
    {
      "id": "OSTY",
      "name": "Otsy",
      "alive": true,
      "hp": 12,
      "max_hp": 12,
      "block": 0,
      "status": [ /* Power Objects */ ]
    }
  ],

  // --- Always present ---
  "status": [ /* Power Objects */ ],
  "deck_count": 10,
  "deck": [ /* Pile Card Objects */ ],
  "relics": [
    {
      "id": "BURNING_BLOOD",
      "name": "Burning Blood",
      "description": "At the end of combat, heal 6 HP.",
      "rarity": "Starter",
      "counter": null,       // Number if relic shows a counter, null otherwise
      "used_up": false,      // RelicModel.IsUsedUp: the relic has been used up
      "melted": false,       // RelicModel.IsMelted: a separate melted-wax state
      "keywords": [ /* Keyword Objects */ ]
    }
  ],
  "potions": [
    {
      "id": "SWIFT_POTION",
      "name": "Swift Potion",
      "description": "Draw 3 cards.",
      "rarity": "Common",
      "slot": 0,             // Potion slot index (use this for use_potion action)
      "can_use_in_combat": true,
      "can_use": true,       // true when use_potion can run now, assuming any required target is supplied
      "use_blocked_reason": null, // e.g. "CombatOnly", "EnemyTargetRequiresCombat", "Automatic", "AlreadyQueued", "PlayerDead", "CustomUsabilityCheckFailed", "NotInPlayPhase", "PlayerActionsDisabled", "NoValidTargets"
      "can_discard": true,
      "discard_blocked_reason": null, // e.g. "AlreadyQueued"
      "requires_target": false,
      "valid_targets": [],   // For AnyEnemy potions: [{ "entity_id": "JAW_WORM_0", "combat_id": 12, "name": "Jaw Worm" }]
      "target_type": "None", // None, Self, AnyEnemy, AnyAlly, AnyPlayer, etc.
      "usage": "Manual",
      "keywords": [ /* Keyword Objects */ ]
    }
  ],
  "max_potion_slots": 3      // Belt capacity. Default 3, grows with belt-expanding relics (e.g. Potion Belt: +2). Use to detect a full belt: len(potions) >= max_potion_slots.
}
```

Owned relics always include boolean `used_up` and `melted`, in singleplayer and the
multiplayer local `player` object (both use `BuildPlayerState`). Markdown marks true
values with `(used)` and `(melted)` in singleplayer outside combat, and during combat.
Multiplayer outside combat does not list relics in Markdown. These fields are additive:
feature-detect their presence; the schema stays at 3. Older builds omit them.
`melted` is separate from consumption: `RelicModel.HoverTip` exposes the melted
notice, and `UpdateTexture` gives it a dark-red tint. The grey inventory tint comes
from `RelicStatus.Disabled` in `NRelicInventoryHolder.RefreshStatus`; for example,
`LizardTail.WasUsed` sets that status when consumed. These booleans do not claim to
encode every reason a relic can be disabled.

Pael's Tooth exposes `relics[].runtime.return_cards` in saved-list order. Each
entry is a full card record rendered after its pending upgrade, with
`stored_upgrade_level` preserving the original saved upgrade level. The list
replaces the opaque `_serializableCards` field; reading it does not add cards,
run global upgrade hooks, or consume RNG. `values=unpowered` applies to these
records too. A hidden counter and an empty list mean there are no cards left.

### Card Object (in hand)

```jsonc
{
  "index": 0,
  "id": "STRIKE_R",
  "name": "Strike",
  "type": "Attack",          // Attack, Skill, Power, Status, Curse
  "cost": "1",               // Energy cost as string ("X" for X-cost)
  "star_cost": null,         // Regent star cost as string, null if N/A
  "description": "Deal 6 damage.",
  "target_type": "AnyEnemy", // None, Self, AnyEnemy, AllEnemies, etc.
  "requires_target": true,
  "valid_targets": [
    { "entity_id": "JAW_WORM_0", "combat_id": 12, "name": "Jaw Worm" }
  ],
  "can_play": true,
  "unplayable_reason": null, // e.g. "NotEnoughEnergy", "NotInPlayPhase", "PlayerActionsDisabled", null if playable
  "is_upgraded": false,
  "is_upgradable": true,
  "current_upgrade_level": 0,
  "max_upgrade_level": 1,
  "upgrade_preview_type": "None",
  "upgrade_preview_cost": "1",
  "upgrade_preview_star_cost": null,
  "upgrade_preview_description": "Deal 9 damage.",
  "keywords": [ /* Keyword Objects */ ],   // localized hover tips — DISPLAY TEXT, don't match on it
  "keyword_ids": ["Exhaust"],              // machine-readable CardKeyword names; use these
  "combat_card_id": 12,      // stable physical identity for the fight; tells two copies of Strike apart
  "has_retain_keyword": false,      // exact Keywords.Contains(Retain); survives turn cleanup
  "has_single_turn_retain": false,  // private single-turn grant; cleared after the hand flush
  "should_retain_this_turn": false, // OR of those two fields, for the current flush
  "has_sly_keyword": false,        // exact permanent/combat keyword membership
  "has_single_turn_sly": false,    // Hand Trick grant; cleared after the hand flush
  "is_sly_this_turn": false,       // OR of the two Sly sources
  "exhaust_on_next_play": false    // one-shot flag, separate from the Exhaust keyword
}
```

### Pile Card Object (draw/discard/exhaust piles)

```jsonc
{
  "index": 0,
  "id": "STRIKE_R",
  "name": "Strike",
  "type": "Attack",          // Attack, Skill, Power, Status, Curse
  "cost": "1",               // Energy cost as string ("X" for X-cost)
  "star_cost": null,         // Regent star cost as string, null if N/A
  "description": "Deal 6 damage.",
  "rarity": "Common",
  "is_upgraded": false,
  "is_upgradable": true,
  "current_upgrade_level": 0,
  "max_upgrade_level": 1,
  "upgrade_preview_type": "None",
  "upgrade_preview_cost": "1",
  "upgrade_preview_star_cost": null,
  "upgrade_preview_description": "Deal 9 damage.",
  "target_type": "AnyEnemy", // None, Self, AnyEnemy, AllEnemies, etc.
  "keywords": [ /* Keyword Objects */ ],   // localized hover tips — DISPLAY TEXT, don't match on it
  "keyword_ids": ["Exhaust"],              // machine-readable CardKeyword names; use these
  "combat_card_id": 12,      // stable physical identity for the fight
  "has_retain_keyword": false,
  "has_single_turn_retain": false,
  "should_retain_this_turn": false,
  "has_sly_keyword": false,
  "has_single_turn_sly": false,
  "is_sly_this_turn": false,
  "exhaust_on_next_play": false // common card fields, present in every pile
}
```

### Orb Object

`passive_val` and `evoke_val` include current Focus and relic modifiers. Glass
also includes `base_passive_val`, its stored passive amount before Focus. This
field preserves the value hidden when negative Focus clamps the preview to zero.

```jsonc
{
  "id": "LIGHTNING",
  "name": "Lightning",
  "description": "Passive: Deal 3 damage to a random enemy. Evoke: Deal 8 damage.",
  "passive_val": 3,
  "evoke_val": 8,
  "keywords": [ /* Keyword Objects */ ]
}
```

### Power Object (status effects)

Used for both player `status` and enemy `status`.

```jsonc
{
  "id": "STRENGTH",
  "name": "Strength",
  "amount": 3,
  "type": "Buff",            // "Buff" or "Debuff"
  "description": "Increases attack damage by 3.",
  "keywords": [ /* Keyword Objects */ ],
  // Exact instance state, present even for powers the UI hides:
  "display_amount": 3,               // what the game would render
  "amount_on_turn_start": 3,         // for powers that tick per turn
  "skip_next_duration_tick": false,
  "instance_type": "Normal",         // game's InstanceType for the power
  "applier_combat_id": 1,            // Creature.CombatId that applied it (null if none)
  "target_combat_id": 42,            // Creature.CombatId it is attached to
  "is_visible": true,                // power.IsVisible
  "hidden_in_ui": true,              // present only when is_visible is false
  "vars": {                          // DynamicVars, per var
    "Strength": { "base": 3, "enchanted": 3, "preview": 3, "rendered": "3" }
  },
  "runtime": { /* BuildPowerRuntimeState: counters/recharge the display omits */ }
}
```

Powers are listed even when `is_visible` is false, because hidden tracker powers
still change damage. `name`, `description` and `keywords` are omitted (rather
than blanked) when localization or hover tips throw mid-transition.

### Keyword Object

```jsonc
{
  "name": "Vulnerable",
  "description": "Takes 50% more damage from attacks."
}
```

---

## State Types

### `menu`

No run in progress.

```jsonc
{
  "state_type": "menu",
  "message": "No run in progress. Player is in the main menu.",
  "menu_screen": "main",
  "options": ["continue", "abandon_run", "singleplayer", "multiplayer", "compendium", "timeline", "settings", "quit"]
}
```

Use `menu_select` with one of the advertised options. Options are accepted case-insensitively.
If a visible option is intentionally withheld from automation, the state may also include `blocked_options` with a reason. For example, Timeline can be blocked while obtained epochs still need manual reveal because opening that game state through automation can trigger invalid unlock-state errors.
`abandon_run` opens a confirmation popup; the next state exposes the popup's `confirm` / `cancel` options.

Menu sub-screens expose their own options:

- `singleplayer`: `standard`, `daily`, `custom`, `back`
- `multiplayer`: `host`, `join`, `load`, `abandon`, `back`
- `multiplayer_host`: `standard`, `daily`, `custom`, `back`
- `multiplayer_join`: `refresh`, `back`, `join_<index>`, `join_<player_id>`
- `multiplayer_load_lobby`: `confirm` / `embark`, `unready`, `back`
- `profile_select`: `profile_1`, `profile_2`, `profile_3`, `back`
- `character_select`: character IDs/names, `back`, `confirm` / `embark`, `unready` (MP, after readying)
- `custom_run`: character IDs/names, `back`, `confirm` / `embark` (accepts `seed`), `unready` (MP, after readying)
- `tutorial_prompt`: `no`, `yes`
- `popup`: advertised popup button labels, normalized to lowercase words such as `ignore` or `back`
- `timeline`: `advance`, `back`

The `multiplayer` submenu is gated on whether a multiplayer save exists: with no save, `host` is shown and `load` / `abandon` are hidden; with a save, those are shown and `host` is hidden. `abandon` opens a vertical popup — the next state turn surfaces the popup's `confirm` / `cancel` options, not a transition.

#### `multiplayer_join` — Friend list / FastMP

```jsonc
{
  "state_type": "menu",
  "menu_screen": "multiplayer_join",
  "message": "Pick a friend to join, or 'refresh' to update the list.",
  "fast_mp": false,             // true when --fastmp / Steam not initialized; auto-joins 127.0.0.1:33771
  "loading": false,             // true while refreshing or while a join handshake is in flight
  "no_friends": false,          // true when refresh returned an empty list
  "friends": [
    { "index": 0, "name": "Bob", "player_id": "76561198000000000", "enabled": true }
  ],
  "options": [
    { "name": "join_0",  "enabled": true },
    { "name": "refresh", "enabled": true },
    { "name": "back",    "enabled": true }
  ]
}
```

`menu_select` accepts either `join_<index>` (e.g. `join_0`) or `join_<player_id>` (e.g. `join_76561198000000000`) — both click the same button. With `fast_mp: true`, the screen auto-joins localhost on open; you typically won't need to issue a join action in that mode.

#### `multiplayer_load_lobby` — Resume saved MP run

Reached via the `multiplayer` submenu's `load` option (host) or by joining a host that already loaded a save (client). Mirrors the standard MP character-select ready/unready flow but on a fixed (saved) party.

```jsonc
{
  "state_type": "menu",
  "menu_screen": "multiplayer_load_lobby",
  "message": "Multiplayer load lobby. Confirm to ready up; ...",
  "lobby": {
    "type": "host",                // or "client"
    "game_mode": "standard",       // or "daily" / "custom"
    "ascension": 0,
    "act": 2,
    "floor": 13,
    "character_id": "REGENT",      // local player's saved character
    "current_hp": 64,
    "max_hp": 80,
    "gold": 240,
    "expected_player_count": 3,
    "connected_player_count": 2,
    "players": [
      {
        "id": "76561198000000000",
        "is_local": true,
        "character_id": "REGENT",
        "is_connected": true,
        "is_ready": false,
        "platform_name": "Alice"
      }
    ]
  },
  "options": [
    { "name": "confirm", "enabled": true },
    { "name": "embark",  "enabled": true },   // alias of confirm
    { "name": "back",    "enabled": true },
    { "name": "unready", "enabled": false }   // becomes enabled after confirm/embark
  ]
}
```

#### `custom_run` — Custom run setup (seeded runs)

Reached from the `singleplayer` (or `multiplayer_host`) submenu via `custom`. This is
a different screen from `character_select`: it owns the seed field, the ascension
panel and the modifier tickboxes, and it carries its own character buttons.

It is the **only** way to start a run on a chosen seed. Standard singleplayer
character select has no lobby behind it, so a seeded `embark` there is rejected.

```jsonc
{
  "state_type": "menu",
  "menu_screen": "custom_run",
  "message": "Custom run setup. Select a character, then 'embark' (pass 'seed' to fix the run).",
  "characters": [ /* same shape as character_select */ ],
  "selected_character": "IRONCLAD",  // the screen auto-selects the first button on open
  "selection_busy": false,
  "seed": "0",                       // the lobby's seed (what the run will actually use)
  "seed_input": "0",                 // the on-screen text field
  "ascension": 0,
  "max_ascension": 0,
  "modifiers": [],                   // ticked custom modifiers, empty for a clean run
  "lobby": { /* present only in MP host/client */ },
  "options": [
    { "name": "IRONCLAD", "enabled": true },
    { "name": "confirm",  "enabled": true },
    { "name": "embark",   "enabled": true },   // alias of confirm
    { "name": "back",     "enabled": true },
    { "name": "unready",  "enabled": false }   // MP only; enabled after confirm/embark
  ]
}
```

`menu_select` with `confirm`/`embark` and a `seed` applies the seed to the lobby
before clicking, so the run starts on it. The seed is canonicalized by the game
(upper-cased, `O` -> `0`, `I` -> `1`).

#### `character_select` — extended for MP

The same screen drives SP, MP host, and MP client. In MP, an additional `lobby` block appears, and the `unready` option becomes available after the local player has hit `confirm`/`embark`:

```jsonc
{
  "state_type": "menu",
  "menu_screen": "character_select",
  "message": "Select a character.",
  "characters": [ /* same shape as SP */ ],
  "lobby": {                       // present only in MP
    "type": "host",                // "host" | "client" | "singleplayer"
    "game_mode": "standard",       // "standard" | "daily" | "custom"
    "max_players": 4,              // private on beta v0.111+, still read; absent only if renamed
    "ascension": 0,
    "max_ascension": 5,            // min across all lobby members' MaxMultiplayerAscension
    "all_ready": false,
    "is_about_to_begin": false,    // mirrors StartRunLobby.IsAboutToBeginGame()
    "is_local_ready": false,
    "local_player_id": "76561198000000000",
    "player_count": 2,
    "seed": "...",                 // optional, present only for daily/custom
    "players": [
      {
        "id": "76561198000000000",
        "slot_id": 0,
        "is_local": true,
        "is_host": true,           // only positively true for self when hosting; otherwise false
        "character": "The Regent",
        "character_id": "REGENT",
        "is_ready": false,
        "platform_name": "Alice"
      }
    ]
  },
  "options": [
    { "name": "REGENT",  "enabled": true },
    { "name": "IRONCLAD","enabled": true },
    /* ... other characters and lockable RANDOM ... */
    { "name": "confirm", "enabled": true },
    { "name": "embark",  "enabled": true },
    { "name": "back",    "enabled": true },
    { "name": "unready", "enabled": false }
  ]
}
```

In SP the `lobby` field is omitted, and `unready` does not appear (the unready button is only enabled after MP ready).

### `unknown`

Run state or room type not recognized.

```jsonc
{
  "state_type": "unknown",
  "room_type": "SomeUnknownRoom"  // May be present
}
```

### `monster` / `elite` / `boss` — Combat

A combat room reports its room type as `state_type` for its whole lifetime, so
the payload says which phase it is in. `battle` is only present once the fight
has actually started:

| Payload | Meaning | What to do |
|---------|---------|------------|
| `battle` present | Combat is live | Act normally (respect `is_play_phase`) |
| `combat_starting: true`, no `battle` | The room is entered but the fight has not started (asset load, deck shuffle, spawn-ins, combat-start banner). Covers combats reached from the map *and* from an event's "Fight" option | Poll again; do not send actions. Lasts about a second |
| `message: "Combat ended. Waiting for rewards..."`, no `battle` | The fight is over and rewards are on the way | Poll again until the rewards overlay or the map appears |

```jsonc
{
  "state_type": "monster",  // or "elite", "boss"
  "battle": {
    "round": 1,
    "turn": "player",       // "player" or "enemy"
    "is_play_phase": true,
    "player_actions_disabled": false,
    "can_end_turn": true,
    "end_turn_blocked_reason": null, // e.g. "NotInPlayPhase", "PlayerActionsDisabled", "CardInPlay", "HandSelectionMode"
    "enemies": [
      {
        "entity_id": "JAW_WORM_0",    // Synthesized ID for targeting
        "combat_id": 42,               // Internal combat ID
        "name": "Jaw Worm",
        "alive": true,                 // Always present. false = dead but still in the fight
        "hp": 44,
        "max_hp": 44,
        "block": 0,
        "is_alive": true,
        "status": [ /* Power Objects */ ],
        "intents": [
          {
            "type": "Attack",          // Attack, Defend, Buff, Debuff, Sleep, etc.
            "intent_class": "AttackIntent", // Concrete intent class name (never throws)
            "is_stun": false,          // true when this intent is a StunIntent
            "label": "11",             // Damage number or short label
            "damage": 11,              // Attack/DeathBlow only: exact per-hit damage
            "hits": 1,                 //   (from the intent's DamageCalc/Repeats)
            "card_count": 2,           // StatusCard only: cards shuffled in
            "title": "Attack",         // Hover tip title
            "description": "Deals 11 damage."  // Hover tip description
          }
        ],
        "stunned": false,              // true when the current move is a stun turn
        "move_id": "SEA_KICK",         // Stable id of the current move state
        // What the current move ACTUALLY does, statically read from the move
        // lambda's IL (PowerCmd.Apply<XxxPower> / CreatureCmd.GainBlock /
        // CardPileCmd.AddToCombatAndPreview<Card> callsites). "target" is
        // "self" (the monster) or "player". "amount" may be null when the
        // value couldn't be resolved. Omitted when the move has no such
        // effects or the scan failed.
        "move_effects": {
          "applies": [ { "power": "StrengthPower", "target": "self", "amount": 2 } ],
          "block": 5,                  // block the move grants its owner
          "heal": 4,                   // HP the move restores
          "status_card": "Slimed"      // card class shuffled into player piles
        },                             //   (count = the StatusCard intent's card_count)
        // Numeric design stats declared on the concrete monster class.
        // Names are "<MoveIdCamelCase><Effect>" (BUBBLE -> BubbleBlock,
        // BubbleStr), so a Buff/Defend/Heal move's real amounts are joinable
        // from its move_id when move_effects is absent.
        "model_stats": { "SeaKickDamage": 8, "BubbleBlock": 9, "BubbleStr": 3 },
        // Complete monster intent state machine. Move nodes include the same
        // intent/effect shapes as the current move. Random branches retain the
        // game's weight, repeat, max-repeat, and cooldown rules. Conditional
        // branches are ordered; condition is a re-evaluable expression over
        // simulated HP, powers, living allies, monster deaths, and move
        // history. enabled preserves the live closure result for diagnostics.
        // Unknown modded closures use {op:"snapshot"} and set the top-level
        // snapshot flag. rng_seed/rng_counter (stable) or
        // rng_state/rng_counter (beta) identify the shared MonsterAi
        // xoshiro256** stream used by all enemies in slot order. A beta state
        // is already at the reported counter and must not be advanced again.
        // Omitted when reflection fails or no machine exists.
        "intent_machine": {
          "initial_state": "BUBBLE",
          "current_state": "SEA_KICK",
          "state_log": ["BUBBLE", "SEA_KICK"],
          "rng_seed": 12345,
          "rng_counter": 7,
          "conditional_values_are_snapshots": true,
          "random_weights_are_snapshots": false,
          "states": [
            { "id": "SEA_KICK", "kind": "move", "follow_up": "CHOICE",
              "intents": [ { "type": "Attack", "label": "8" } ] },
            { "id": "CHOICE", "kind": "random", "branches": [
              { "state": "BUBBLE", "weight": 1.0, "repeat": "CannotRepeat",
                "max_repeats": 0, "cooldown": 0 }
            ] },
            { "id": "PHASE", "kind": "conditional", "branches": [
              { "state": "BUBBLE", "enabled": true,
                "condition": { "op": "owner_hp_fraction", "cmp": "ge",
                  "numerator": 1, "denominator": 2 } },
              { "state": "SPINNING_KICK", "enabled": false,
                "condition": { "op": "owner_hp_fraction", "cmp": "lt",
                  "numerator": 1, "denominator": 2 } }
            ] }
          ]
        }
      },
      {
        // A dead enemy the game keeps in the fight (a Decimillipede segment waiting to
        // reattach). Listed so the revive is visible; it cannot be targeted while dead.
        "entity_id": "DECIMILLIPEDE_SEGMENT_1",
        "combat_id": 44,
        "name": "Decimillipede Segment",
        "alive": false,
        "hp": 0,
        "max_hp": 46,
        "block": 0,
        "status": [ /* still carries REATTACH_POWER */ ],
        "revive": {                    // Present only when it is actually coming back
          "in_turns": 2,               // Enemy turns until it heals back up; null if unknown
          "power_id": "REATTACH_POWER",
          "power_name": "Reattach",
          "hp": 25                     // HP it returns with; null when not readable
        },
        "intents": [],                 // Becomes a Heal intent on the turn before the revive
        "is_visible": true,            // Node visible this frame
        "can_target": true,            // Valid target for a targeted card/potion right now
        "can_select": true             // Selectable in this phase (target + play phase)
      }
    ],
    // The player's side of the board: living, non-player creatures (Osty and
    // other pets). Each entry has exactly the same shape as an `enemies[]`
    // entry, so a target consumed from either list works the same way. The
    // player's own creature is reported under `player`, not here.
    "allies": [ { "entity_id": "OSTY_0", "combat_id": 50, "alive": true, "hp": 12 } ],
    // The combat id the game will hand to the next creature it creates.
    // A summon that lands this turn shows up with this id, so a client can
    // tell a freshly spawned pet from one it has already seen.
    "next_creature_id": 51,
    // Run RNG stream coordinates (singleplayer and multiplayer battle states).
    // Each stream is an independent xoshiro256** generator seeded once per run
    // (seed = run seed + hash(snake_case(stream name))); counter = values
    // consumed so far. Reconstructing the stream at counter and rolling it
    // forward predicts future random results exactly: "shuffle" drives
    // reshuffle order (StableShuffle: sort by (card id, upgrade level) then
    // Fisher-Yates downward, count-1 rolls) plus random-position inserts and
    // Beat Down/Catastrophe/Uproar-style pile picks; "combat_targets" drives
    // random-target attacks and orb targeting (one NextInt(0, n) roll per
    // pick over hittable enemies in slot order). In MP the host-synced
    // RunRngSet is shared by all players in lockstep. Streams are read via
    // reflection; a renamed stream is omitted. Absent if reflection fails.
    // Identical to `run.rng` — see the "Run RNG" section below for the full
    // stream list, the run seed and the odds accumulators.
    "rng_streams": { "...": "same block as run.rng" }
  },
  "run": { ... },   // Carries "rng" on every screen, in and out of combat
  "player": { ... } // Includes hand, energy, piles, orbs during combat, plus
                    // that player's own "rng" (rewards/shops/transformations)
}
```

**Dead enemies.** `enemies[]` normally holds only living creatures, because the game drops a
killed enemy from the fight. A few powers refuse that — `ReattachPower` on a Decimillipede
segment is the one that matters — and their owner stays in the fight as a corpse. Those
corpses are listed with `alive: false` and `hp: 0`, keeping the `entity_id` they already had,
so a caller that never meets one sees no change. Filter on `alive` (not on `hp > 0`) to get
the targetable enemies; `play_card` and `use_potion` already refuse a dead target.

`revive` is present only when the corpse is actually scheduled to come back: `in_turns` is
read off the monster's move chain (2 the turn it dies, 1 the turn before it stands up, and
its `intents` then show `Heal`), and `hp` is the amount it heals for. A corpse held in the
fight by something that never revives it (`DieForYouPower`, `IllusionPower`) has `alive: false`
and no `revive`, and neither does the last segment of a Decimillipede — with no living peer
left to reattach to, the fight is over rather than about to restart.

**Allies.** `battle.allies` lists the player's own side of the board — living, non-player
creatures such as Osty. Only `enemies[]` used to be reported, so a pet was invisible to
anything reading combat state and the Necrobinder capture mechanic could not be verified.
Entries share the `enemies[]` shape exactly (including `status`, `intents` and `revive`), so
one target lookup handles both lists. The player's own creature stays under `player`.

`battle.next_creature_id` is the combat id the game will assign to the next creature it
creates (`CombatState._nextCreatureId`). An id equal to it in the same payload names a
creature summoned during this turn.

### Run RNG — `run.rng` / `battle.rng_streams` / `player.rng` / enemy `rng`

Every deterministic generator the game holds is exported. Each is an independent
xoshiro256** stream with `counter` = values consumed so far. Stable builds expose
the constructor `seed`; reconstruct it and advance by `counter`. Beta v0.110+
instead exposes the four-word current `state`, which is already advanced to the
reported counter and must be used directly. Nothing here ever advances a live
stream.

Streams are read via reflection: a renamed stream is omitted rather than
crashing, and a stream added by a game update is picked up automatically (under
its snake-cased type name if it has no property accessor).

**`run.rng`** — the run-wide `RunRngSet`, present on every screen (not just in
combat, since map layout, `?`-node contents and the encounter order are all
rolled outside a battle). `battle.rng_streams` carries the same block during
combat, for backwards compatibility.

```jsonc
"rng": {
  "run_seed": 3054346721,       // uint hash of the seed string; the map LAYOUT is
                                // a fresh `new Rng(run_seed, "act_N_map")`, so it
                                // is reconstructible from this alone
  "run_string_seed": "ABC123",  // the seed as typed/displayed

  "up_front":                { "seed": 771209812, "counter": 12 },  // monster/elite order, event pool, relic offers
  "shuffle":                 { "seed": 3054346721, "counter": 42 }, // reshuffles (StableShuffle: sort by (card id,
                                                                    // upgrade level) then Fisher-Yates downward,
                                                                    // count-1 rolls), random-position inserts,
                                                                    // Beat Down/Catastrophe/Uproar pile picks
  "unknown_map_point":       { "seed": 118273645, "counter": 4 },   // the room type a "?" node rolls into
  "combat_card_generation":  { "seed": 77120981,  "counter": 0 },
  "combat_potion_generation":{ "seed": 5523187,   "counter": 0 },
  "combat_card_selection":   { "seed": 445910227, "counter": 1 },   // True Grit-style random card picks
  "combat_energy_costs":     { "seed": 8912345,   "counter": 0 },   // Confusion / Snecko Eye
  "combat_targets":          { "seed": 1938475601,"counter": 3 },   // random-target attacks and orb targeting
                                                                    // (one NextInt(0, n) per pick, hittable
                                                                    // enemies in slot order)
  "monster_ai":              { "seed": 12345,     "counter": 7 },
  "niche":                   { "seed": 992134,    "counter": 9 },   // CursedRun, and SPAWNED MONSTER HP:
                                                                    // CombatState.CreateCreature calls
                                                                    // Creature.SetUniqueMonsterHpValue(niche),
                                                                    // so mid-combat summons (Infested wrigglers,
                                                                    // Surprise gremlins) are predictable
  "combat_orb_generation":   { "seed": 190283345, "counter": 0 },   // Chaos
  "treasure_room_relics":    { "seed": 44120983,  "counter": 0 },   // MP tie-break on duplicate relic picks

  // Stateful odds the rolls are compared against. A counter alone cannot
  // predict a "?" node without the running odds value.
  "odds": {
    "unknown_map_point": {
      "current_value": 0.0,
      "monster_odds": 0.1, "elite_odds": -1.0,   // negative = never rolled
      "treasure_odds": 0.02, "shop_odds": 0.03
    }
  }
}
```

On beta v0.110+, each stream entry uses the following shape instead. All four
values are unsigned 64-bit integers:

```jsonc
"shuffle": {
  "state": [13720838825685603483, 2398916695208396998,
            17770384849984869256, 891717726879801395],
  "counter": 42
}
```

**`player.rng`** (and `players[i].rng` in multiplayer) — the per-player
`PlayerRngSet`, seeded from the run seed plus the player's **slot** index.

```jsonc
"rng": {
  "seed": 3054346799,
  "slot_index": 0,              // also needed to rebuild ad-hoc content RNGs,
                                // which seed from run seed + slot + content id
  "rewards":         { "seed": 1122334, "counter": 6 },  // card + potion rewards, and
                                                         // whether a potion drops at all
  "shops":           { "seed": 5566778, "counter": 2 },
  "transformations": { "seed": 9900112, "counter": 0 },
  "odds": {
    "card_rarity":   { "current_value": 0.0 },
    "potion_reward": { "current_value": 0.4 }
  }
}
```

**Enemy `rng`** — each enemy in `battle.enemies[]` carries its own monster-local
stream, assigned in `CombatState.CreateCreature` from the run seed and the
current map coordinate. Omitted for canonical (non-mutable) models, whose getter
returns the non-deterministic `Rng.Chaotic`.

```jsonc
"rng": { "seed": 3054346722, "counter": 3 } // stable
// beta: { "state": [s0, s1, s2, s3], "counter": 3 }
```

### `hand_select` — In-Combat Card Selection

Appears when a card effect prompts "Select a card to exhaust/discard/upgrade". **Not** an overlay — happens within the combat hand.

```jsonc
{
  "state_type": "hand_select",
  "hand_select": {
    "mode": "simple_select",     // "simple_select" or "upgrade_select"
    "prompt": "Select a card to Exhaust.",
    "cards": [
      {
        "index": 0,
        "id": "STRIKE_R",
        "name": "Strike",
        "type": "Attack",
        "cost": "1",
        "star_cost": null,       // Regent star cost as string, null if N/A
        "description": "Deal 6 damage.",
        "is_upgraded": false,
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "selected_cards": [          // Only present if cards have been selected
      {
        "index": 0,
        "hand_index": 2,         // Physical index in player.hand, disambiguates identical copies
        "id": "DEFEND_R",
        "name": "Defend",
        "type": "Skill",
        "cost": "1",
        "star_cost": null,
        "description": "Gain 5 Block.",
        "is_upgraded": false,
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "can_confirm": false
  },
  "battle": { ... },  // Full battle state included for context
  "run": { ... },
  "player": { ... }
}
```

### `rewards` — Rewards Screen

Appears after combat ends or when triggered by events (e.g. TheFutureOfPotions, Draft).

```jsonc
{
  "state_type": "rewards",
  "rewards": {
    "items": [
      {
        "index": 0,
        "type": "gold",              // gold, potion, relic, card, special_card, card_removal
        "description": "Obtain 25 gold.",
        "is_visible": true,
        "can_claim": true,
        "gold_amount": 25            // Only for gold rewards
      },
      {
        "index": 1,
        "type": "potion",
        "description": "Obtain a potion.",
        "potion_id": "SWIFT_POTION", // Only for potion rewards
        "potion_name": "Swift Potion",
        "potion_description": "Draw 3 cards.",
        "potion_rarity": "Common",
        "potion_target_type": "None",
        "potion_usage": "Manual",
        "keywords": [ /* Keyword Objects */ ]
      },
      {
        "index": 2,
        "type": "relic",
        "description": "Obtain a relic.",
        "relic_id": "VAJRA",
        "relic_name": "Vajra",
        "relic_description": "At the start of each combat, gain 1 Strength.",
        "relic_rarity": "Common",
        "keywords": [ /* Keyword Objects */ ]
      },
      {
        "index": 3,
        "type": "card",
        "description": "Add a card to your deck."
        // Card rewards open the card_reward screen when claimed
      }
    ],
    "can_proceed": true
  },
  "run": { ... },
  "player": { ... }
}
```

### 報酬 ID と選択状態（加算契約、schema 3）

`rewards.items` の既存キーと有効ボタンだけを列挙する挙動は維持する。
各 item に `reward_id` / `selection_state` / `claimable` / `skipped` /
`reward_consumed` / `last_selection` / `last_option_id` を追加する。
`rewards.screen_revision` は現在の一覧と選択状態を表す opaque な文字列。
`rewards.selection_history` は同じセッションで観測した報酬の ID と上記状態を保持し、
取得後に `items` から消えた報酬も `claimed` として確認できる。履歴に報酬の中身は含めない。
`claimable` は現在の有効ボタンの有無であり、ポーション枠など取得条件の保証ではない。

`reward_id` はプロセスごとのランダム epoch と単調番号から生成する。
シード・抽選・メモリアドレスとは無関係で、同じ画面の同じ Reward オブジェクトに対して固定。
他の報酬取得で index が詰まっても残存 ID は変わらない。
報酬画面の終了、マップの開封、別の報酬画面、ランの変更で登録を破棄し、再起動でも別 epoch となる。
一時的なカード三択・デッキ選択・一時停止の表示だけでは親セッションを破棄しない。

| selection_state | 意味 |
|---|---|
| `unopened` | 画面生成から追跡できた未開封の報酬 |
| `opened` | 開封・選択中。操作送信だけで取得済みにはしない |
| `skipped` | Skip の選択結果と親の RewardSkipped 通知を観測。親は未消費で再開封可能 |
| `claimed` | 親の RewardClaimed 通知を観測。カード取得と alternative による完了は last_selection で区別 |
| `unknown` | 観測欠落、中断、選択失敗等。未閲覧／完了へ決め打ちしない |

`skipped` は selection_state が `skipped` のとき true。
`reward_consumed` は `claimed` で true、`skipped` で false、それ以外は null（または欠落）。
`last_selection` は `card` / `skip` / `alternative` / `direct` / `interrupted` / `unknown` 等。
`last_option_id` に `Skip` / `SACRIFICE` / `REROLL` 等の表示中の option ID が入る。
REROLL は同じ親のまま `opened` に戻り、SACRIFICE は親の完了通知が来るまでは完了にしない。
手動の再開封は許可し、再開封すると `opened` になる。GET は Skip 状態を消去せず、報酬を選択／完了しない。

`card_reward.parent_reward_id` は対応する親 ID（親を特定できない直接表示では null／欠落）。
`card_reward.selection_state` も追加する。対応が不明なら `unknown`。
未開封カードの中身、次の報酬、生成候補・抽選結果の先読みは追加しない。
Markdown にも ID・revision・選択状態・親 ID を表示する。新項目のない入力も表示できる。

### `card_reward` — Card Reward Selection

Pick one card to add to your deck. Appears after claiming a card reward, or directly during events (e.g. Draft modifier at Neow).

```jsonc
{
  "state_type": "card_reward",
  "card_reward": {
    "cards": [
      {
        "index": 0,
        "id": "UPPERCUT",
        "name": "Uppercut",
        "type": "Attack",
        "cost": "2",
        "star_cost": null,
        "description": "Deal 13 damage. Apply 1 Weak. Apply 1 Vulnerable.",
        "rarity": "Uncommon",
        "is_upgraded": false,
        "is_selected": false,
        "is_visible": true,
        "can_select": true,
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "can_skip": true       // true only when the skip/alternate button is enabled and visible
  },
  "run": { ... },
  "player": { ... }
}
```

### `map` — Map Navigation

```jsonc
{
  "state_type": "map",
  "map": {
    "current_position": {
      "col": 3, "row": 2, "type": "Monster"
    },
    "visited": [
      { "col": 3, "row": 0, "type": "Start" },
      { "col": 3, "row": 1, "type": "Monster" },
      { "col": 3, "row": 2, "type": "Monster" }
    ],
    // Current act only. `visited` above carries the map-point TYPE; this is the
    // only place a "?" reports the room it actually became, and which encounter
    // or event was served there. Also the input to
    // RunManager.BuildRoomTypeBlacklist (no Shop straight after a Shop).
    "point_history": [
      {
        "map_point_type": "Unknown",
        "rooms": [
          { "room_type": "Monster", "model_id": "MAWLER_NORMAL",
            "monster_ids": ["MAWLER"] }
        ]
      }
    ],
    "next_options": [
      {
        "index": 0,
        "col": 2, "row": 3,
        "type": "RestSite",
        "is_visible": true,
        "can_travel": true,
        "leads_to": [            // 1-level lookahead (children)
          { "col": 1, "row": 4, "type": "Elite" },
          { "col": 3, "row": 4, "type": "Shop" }
        ]
      }
    ],
    "travel_enabled": true,      // false while the current room is unfinished
    "travel_in_flight": false,   // true between picking a node and the room loading
    "transition_in_flight": false, // true while an act change is still resolving behind this map
    "screen_open": false,          // NMapScreen.IsOpen — the map was deliberately opened
    "screen_visible": true,        // the map node is on screen, which includes Close()'s animate-out
    "drawing_coordinate_space": {
      "name": "map_normalized",
      "x_min": -3, "x_max": 3,
      "y_min": -2, "y_max": 2
    },
    "nodes": [                   // Full map DAG
      {
        "col": 3, "row": 0,
        "type": "Start",
        // Live visual anchor after the map's per-run random jitter. Accepted
        // directly as an x/y point by map_draw.
        "draw_position": { "x": -0.08, "y": 0.83 },
        "children": [ [2, 1], [3, 1], [4, 1] ]  // [col, row] pairs
      }
    ],
    "boss": {
      "col": 3,
      "row": 15,
      "id": "ENCOUNTER.VANTOM_BOSS",
      "name": "Vantom"
    },
    "bosses": [
      { "col": 3, "row": 15, "id": "ENCOUNTER.VANTOM_BOSS", "name": "Vantom" }
    ],
    // What this act has ALREADY served. There is no "next encounter" field: the game does
    // not show that before the room is entered. Omitted entirely before the first room.
    "encounters_seen": {
      "act": 1,
      "monster": [
        { "floor": 2, "id": "NIBBITS_WEAK", "name": "Nibbits", "monsters": ["Nibbit", "Nibbit"] }
      ],
      "elite": [
        { "floor": 8, "id": "BYRDONIS_ELITE", "name": "Byrdonis", "monsters": ["Byrdonis"] }
      ],
      "event": [
        { "floor": 1, "id": "NEOW", "name": "Neow" }
      ]
      // "elite_cycle": 1   // only once the act's elite pool has wrapped at least once
    }
  },
  "run": { ... },
  "player": { ... }
}
```

Existing drawing strokes are intentionally not included in game state. Read
them from the dedicated `GET /api/v1/mapdrawings` endpoint.

### `event` — Event / Ancient Encounter

```jsonc
{
  "state_type": "event",
  "event": {
    "event_id": "NEOW",
    "event_name": "Neow",
    "is_ancient": true,
    "in_dialogue": false,        // true = click to advance dialogue first
    "body": "Event description text...",
    // A room transition is resolving (entering this room, or an option body
    // still running). choose_event_option refuses every option while it is
    // true, and every option reports is_enabled: false to match — wait, don't
    // retry. Notably non-zero with Instant Mode, which draws the interactive
    // event well before the game's own room fade finishes.
    "transition_in_flight": false,
    "options": [
      {
        "index": 0,
        // EventOption.TextKey: a stable, language-independent option id. Match
        // on this rather than on the localized title/description, which change
        // with rewording and translation.
        "option_key": "NEOW.pages.INITIAL.options.DRAFT",
        "title": "Draft",
        "description": "Choose 10 card rewards to replace your starting deck.",
        "is_locked": false,
        "is_enabled": true,
        "is_visible": true,
        "can_choose": true,
        "is_proceed": false,
        "was_chosen": false,
        "is_enabled": true,                  // false while a choice is executing or a transition is resolving
        "relic_id": "RELIC_ID",             // Only if option has a relic
        "relic_name": "Relic Name",         // Only if option has a relic
        "relic_description": "Relic desc.",  // Only if option has a relic
        "relic_rarity": "Common",           // Only if option has a relic
        "relic_keywords": [ /* Keyword Objects */ ],
        "keywords": [ /* Keyword Objects */ ]
      }
    ]
  },
  "run": { ... },
  "player": { ... }
}
```

### `rest_site` — Rest Site

```jsonc
{
  "state_type": "rest_site",
  "rest_site": {
    "room_open": true,          // false while the room UI is still loading
    "options": [
      {
        "index": 0,
        "id": "HEAL",
        "name": "Rest",
        "description": "Heal 30% of max HP.",
        "is_enabled": true,
        "is_visible": true,
        "can_choose": true,
        "has_button": true   // false = the option exists in the run model but no
                             // button is drawn; choose_rest_option refuses it
      }
    ],
    "can_proceed": false
  },
  "run": { ... },
  "player": { ... }
}
```

### `shop` — Shop

State queries do not open the inventory. Use `shop_open` when ready to buy;
this permits out-of-combat actions such as throwing Foul Potion first.

Card entries include `is_upgraded`, `enchantment`, and `enchantment_state` from
the offered card, plus `card_upgrade_preview_*` fields describing what the card
would become if upgraded. These use the same fields as deck and reward cards,
including relic effects applied when the inventory is created or updated after a
purchase.

`can_proceed` mirrors the `proceed` action: it is true if the proceed button is
already enabled, or if the inventory can be closed (then proceeded through) in
one action. `can_close_inventory` reports the close step on its own.

```jsonc
{
  "state_type": "shop",
  "shop": {
    "inventory_open": false,
    "items": [
      // Card entry
      {
        "index": 0,
        "category": "card",
        "price": 75,               // Gold price in the shop
        "is_stocked": true,
        "can_afford": true,
        "can_purchase": true,
        "on_sale": false,
        "card_id": "OFFERING",
        "card_name": "Offering",
        "card_type": "Skill",
        "card_cost": "1",          // Energy cost as string ("X" for X-cost)
        "card_star_cost": null,    // Regent star cost as string, null if N/A
        "card_rarity": "Rare",
        "card_description": "Lose 6 HP. Gain 2 Energy. Draw 3 cards.",
        "card_upgrade_preview_description": "Lose 6 HP. Gain 2 Energy. Draw 4 cards.",
        "is_upgraded": false,
        "enchantment": null,
        "enchantment_state": null,
        "keywords": [ /* Keyword Objects */ ]
      },
      // Relic entry
      {
        "index": 5,
        "category": "relic",
        "price": 150,
        "is_stocked": true,
        "can_afford": false,
        "can_purchase": false,
        "relic_id": "VAJRA",
        "relic_name": "Vajra",
        "relic_description": "At the start of each combat, gain 1 Strength.",
        "relic_rarity": "Common",
        "keywords": [ /* Keyword Objects */ ]
      },
      // Potion entry
      {
        "index": 8,
        "category": "potion",
        "price": 50,
        "is_stocked": true,
        "can_afford": true,
        "can_purchase": true,
        "potion_id": "FIRE_POTION",
        "potion_name": "Fire Potion",
        "potion_description": "Deal 20 damage to target enemy.",
        "potion_rarity": "Common",
        "potion_target_type": "Enemy",
        "potion_usage": "Manual",
        "keywords": [ /* Keyword Objects */ ]
      },
      // Card removal entry
      {
        "index": 10,
        "category": "card_removal",
        "price": 75,
        "is_stocked": true,
        "can_afford": true,
        "can_purchase": true
      }
    ],
    "inventory_open": true,
    "can_close_inventory": true,
    "can_proceed": true,
    "error": "..."             // Only present if inventory isn't ready; retry in a moment
  },
  "run": { ... },
  "player": { ... }
}
```

### `fake_merchant` — Fake Merchant Event

A relic-only shop disguised as an event. Uses `shop_open`, `shop_purchase`, and
`proceed` actions (same as a regular shop). If the player triggers a fight, the
merchant disappears.

```jsonc
{
  "state_type": "fake_merchant",
  "fake_merchant": {
    "event_id": "FAKE_MERCHANT",
    "event_name": "Fake Merchant",
    "started_fight": false,         // true after triggering the foul potion fight
    "shop": {
      "inventory_open": false,
      "items": [
        {
          "index": 0,
          "category": "relic",
          "cost": 150,
          "is_stocked": true,
          "can_afford": true,
          "can_purchase": true,
          "relic_id": "VAJRA",
          "relic_name": "Vajra",
          "relic_description": "At the start of each combat, gain 1 Strength.",
          "relic_rarity": "Common",
          "keywords": [ /* Keyword Objects */ ]
        }
      ],
      "inventory_open": true,
      "can_close_inventory": true,
      "can_proceed": true
    },
    // After fight:
    // "started_fight": true,
    // "shop": { "items": [], "can_proceed": true },
    // "message": "The fake merchant has been defeated. Proceed to map."
  },
  "run": { ... },
  "player": { ... }
}
```

**Note:** The fake merchant only sells relics — no cards, potions, or card removal. Its shop actions work the same as for a regular shop.

### `treasure` — Treasure Room

Chest is auto-opened on first state query.

```jsonc
{
  "state_type": "treasure",
  "treasure": {
    "message": "Opening chest...",  // Transitional state; query again
    // OR, once opened:
    "relics": [
      {
        "index": 0,
        "id": "LANTERN",
        "name": "Lantern",
        "description": "Gain 1 Energy on the first turn of each combat.",
        "rarity": "Uncommon",
        "is_visible": true,
        "can_claim": true,
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "can_proceed": true
  },
  "run": { ... },
  "player": { ... }
}
```

### `card_select` — Card Selection Overlay

Covers deck transforms, upgrades, enchants, removals, and choose-a-card effects. Appears on top of any room. During combat the
top-level payload also contains the full `battle` snapshot, so a client can evaluate the selection in its combat
context rather than applying a deck-only heuristic.

```jsonc
{
  "state_type": "card_select",
  "card_select": {
    "screen_type": "transform",  // transform, upgrade, enchant, select, simple_select, choose
    "prompt": "Choose 2 cards to Transform.",
    "selection_id": 17,          // monotonic identity of the command that opened the overlay
    "selection_entry_point": "FromCombatPile",
    "selection_surface": "combat_pile", // hand, combat_pile, generated_choice, generated_bundle, deck, simple_grid
    "selection_effect": "discard_to_draw_top", // canonical post-selection continuation; origin_specific if unknown
    "source_pile": "discard",   // present for combat-pile selectors
    "origin_id": "HEADBUTT",    // source model when the command receives one
    "origin_type": "Headbutt",  // decompiled caller/model type
    "origin_kind": "card",      // card, potion, power, relic, monster, event, or model
    "min_select": 1,
    "max_select": 1,
    "manual_confirmation": false,
    "cancelable": false,
    "cards": [
      {
        "index": 0,
        "combat_card_id": 23,   // stable physical-card identity; combat piles only
        "id": "STRIKE_R",
        "name": "Strike",
        "type": "Attack",
        "cost": "1",
        "star_cost": null,       // Regent star cost as string, null if N/A
        "description": "Deal 6 damage.",
        "rarity": "Common",
        "is_upgraded": false,
        "is_upgraded": false,
        "selected": false,       // in the screen's own selection set (grid screens only)
        "is_selected": false,    // same fact from the holder's own selected state
        "is_visible": true,
        "can_select": true,
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "selected_cards": [],        // selected cards as full card payloads
    "selected_count": 0,         // how many cards the screen currently holds selected
    "required_count": 2,         // the N of a "choose N" prompt; null when a range is accepted
    "min_select": 2,             // the two ends of that range (equal when required_count is set)
    "max_select": 2,
    "preview_showing": false,    // true when the preview panel is up (step two, see below)
    "preview_cards": [],         // cards shown by an upgrade/transform/select preview
    "can_confirm": false,        // true when confirm button is available
    "can_cancel": true           // true when close/cancel button is available

    // "selected_outside_grid": only present when the grid is scrolled and a selected card
    // falls outside the window listed in "cards".

    // For "choose" type: picking is immediate (no confirm needed).
    // can_skip indicates if a skip button exists.
  },
  "battle": { ... },            // present when this overlay is inside a live combat
  "run": { ... },
  "player": { ... }
}
```

**Grid screens have two steps.** Cards are picked first; then a preview panel opens — by itself once
`max_select` is reached, or when `confirm_selection` presses the screen's own confirm — and only the
preview's own confirm applies the selection. `selected` comes from the screen's selection set, not from the
grid highlight, so it stays `true` during the preview (the game drops the highlight there but keeps the card).

### `bundle_select` — Card Bundle Selection Overlay

Choose between bundles of cards (e.g. from Scroll Boxes relic).

```jsonc
{
  "state_type": "bundle_select",
  "bundle_select": {
    "screen_type": "bundle",
    "prompt": "Choose a bundle.",
    "bundles": [
      {
        "index": 0,
        "card_count": 3,
        "cards": [
          {
            "index": 0,
            "id": "UPPERCUT",
            "name": "Uppercut",
            "type": "Attack",
            "cost": "2",
            "star_cost": null,   // Regent star cost as string, null if N/A
            "description": "Deal 13 damage. Apply 1 Weak. Apply 1 Vulnerable.",
            "rarity": "Uncommon",
            "is_upgraded": false,
            "keywords": [ /* Keyword Objects */ ]
          }
        ]
      }
    ],
    "preview_showing": false,
    "preview_cards": [ /* Card Objects — shown when a bundle is selected */ ],
    "can_cancel": false,
    "can_confirm": false
  },
  "run": { ... },
  "player": { ... }
}
```

### `relic_select` — Relic Choice Overlay

Boss relic selection. Pick is immediate.

```jsonc
{
  "state_type": "relic_select",
  "relic_select": {
    "prompt": "Choose a relic.",
    "relics": [
      {
        "index": 0,
        "id": "BLACK_STAR",
        "name": "Black Star",
        "description": "Elites drop an additional relic.",
        "rarity": "Rare",
        "is_visible": true,
        "can_select": true,
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "can_skip": true
  },
  "run": { ... },
  "player": { ... }
}
```

### `crystal_sphere` — Crystal Sphere Minigame

```jsonc
{
  "state_type": "crystal_sphere",
  "crystal_sphere": {
    "instructions_title": "Crystal Sphere",
    "instructions_description": "Use divination tools to reveal cells...",
    "grid_width": 11,
    "grid_height": 11,
    "cells": [
      {
        "x": 0, "y": 0,
        "is_hidden": true,       // true = unrevealed
        "is_clickable": true,    // true = can be clicked to reveal
        "is_highlighted": false,
        "is_hovered": false,
        "item_type": "CrystalSphereGold",  // Only on revealed cells
        "is_good": true                     // Only on revealed cells
      }
    ],
    "clickable_cells": [
      { "x": 4, "y": 7 }       // Convenience list of clickable cells
    ],
    "revealed_items": [
      {
        "item_type": "CrystalSphereGold",
        "x": 2, "y": 3,
        "width": 1, "height": 1,
        "is_good": true
      }
    ],
    "tool": "big",               // "big", "small", or "none"
    "can_use_big_tool": true,
    "can_use_small_tool": true,
    "divinations_left_text": "3 divinations remaining",
    "can_proceed": false,
    "minigame": {
      "rng_at_start": { "seed": 1234567, "counter": 1 },  // beta builds: { "state": [..4..], "counter": 1 }
      "divinations_left": 2,
      "clicks": [ { "tool": "big", "x": 5, "y": 5 } ],
      "click_in_flight": false,   // a click is still resolving (a curse reveal awaits a ~2s preview)
      "trace_complete": true
    }
  },
  "run": { ... },
  "player": { ... }
}
```

### `game_over`

Run has ended.

```jsonc
{
  "state_type": "game_over",
  "game_over": {
    "message": "Run ended.",
    "options": ["main_menu"]
  },
  "run": { ... },
  "player": { ... }
}
```

Use `menu_select` with `main_menu` to return to the main menu. `continue` is not advertised because it is not an actionable game-over option.

### `overlay` — Unhandled Overlay (catch-all)

Prevents soft-locks when an unrecognized overlay is active.

```jsonc
{
  "state_type": "overlay",
  "overlay": {
    "screen_type": "NSomeUnknownScreen",
    "message": "An overlay (NSomeUnknownScreen) is active. It may require manual interaction in-game."
  },
  "run": { ... },
  "player": { ... }
}
```

---

## Map Drawings

### `GET /api/v1/mapdrawings`

Returns saved drawing/eraser polylines through a dedicated endpoint. Drawing
data is never embedded in the normal singleplayer or multiplayer state payload.
The endpoint works for either run mode and also reports live node anchors.

```jsonc
{
  "status": "ok",
  "coordinate_space": {
    "name": "map_normalized",
    "x_min": -3, "x_max": 3,
    "y_min": -2, "y_max": 2,
    "description": "Resolution-independent coordinates used by the game's saved and multiplayer map drawings"
  },
  "nodes": [
    { "col": 2, "row": 3, "position": { "x": -0.18, "y": 0.63 } }
  ],
  "players": [
    {
      "player_id": "76561198000000000",
      "is_local": true,
      "lines": [
        {
          "mode": "draw",
          "points": [
            { "x": -0.18, "y": 0.63 },
            { "x": 0.02, "y": 0.51 }
          ]
        }
      ]
    }
  ]
}
```

The node positions reflect the current live map's random visual jitter and
canvas scroll transform. They use the game's native map-network coordinate
space accepted by `map_draw`.

---

## Profiles

Profile endpoints are independent of the singleplayer and multiplayer run endpoints.

### `GET /api/v1/settings`

Returns the game's saved settings and preferences. Values come from the save manager's settings and prefs objects; the API does not change them.

- `status`, `kind`: response envelope fields. `kind` is `settings`.
- `display`: `fullscreen` (bool), `resolution` (`WIDTHxHEIGHT` from the saved window size), `fps_limit` (int), `vsync` (string, the game's vsync mode name), `msaa` (int), `aspect_ratio` (string, the game's aspect-ratio setting name), `target_display` (int, monitor index), `limit_fps_background` (bool, whether the cap also applies while unfocused).
- `audio`: `master`, `bgm`, `sfx`, `ambience` — channel volumes as the game stores them.
- `gameplay`: `fast_mode` (string, `Normal` / `Fast` / `Instant`), `screen_shake` (int, the settings screen's option index), `show_run_timer` (bool), `show_card_indices` (bool), `text_effects` (bool), `long_press` (bool, whether long-press input is enabled).
- `mods`: `enabled` (bool, whether the player agreed to mod loading).
- `language`: the selected language, omitted when the save has none.
- `skip_intro`: whether the intro logo is skipped.

```jsonc
{
  "status": "ok",
  "kind": "settings",
  "display": {
    "fullscreen": true,
    "resolution": "2560x1440",
    "fps_limit": 60,
    "vsync": "Enabled",
    "msaa": 4,
    "aspect_ratio": "Auto",
    "target_display": 0,
    "limit_fps_background": true
  },
  "audio": { "master": 0.8, "bgm": 0.7, "sfx": 0.9, "ambience": 0.5 },
  "gameplay": {
    "fast_mode": "Instant",
    "screen_shake": 1,
    "show_run_timer": true,
    "show_card_indices": false,
    "text_effects": true,
    "long_press": false
  },
  "mods": { "enabled": true },
  "language": "en",
  "skip_intro": false
}
```

When the save manager or either save object is missing, the endpoint answers `503` with `error_code` `save_manager_unavailable` or `settings_data_unavailable`; an unexpected read failure answers `500` with `settings_read_failed`.

### `GET /api/v1/profile`

Returns the active profile's persistent progress summary, including `status: "ok"`, `kind: "profile"`, `profile_id`, save scope/path context, `resolved_progress_path`, `current_run` when a run is active, character stats, card stats, encounter stats, discovered content, achievements, epochs, and global totals.

### `GET /api/v1/compendium`

Returns the active profile's progress grouped by the in-game Compendium cards:

- `status`, `kind`: response envelope fields. `kind` is `compendium`.
- `profile_id`, `progress_path`, `resolved_progress_path`, `profile_root`, `save_scope`: active profile and save-location context, matching `/api/v1/profile`.
- `current_run`: present while a run is active. Includes profile/save context (`is_in_progress`, `profile_id`, `progress_path`, `resolved_progress_path`, `profile_root`, `save_scope`) and `id_format`; when `current_run.save` can be read, it also includes save-backed fields such as `start_time`, a derived `run_id` in `{save_scope}:profile{profile_id}:{start_time}` format, `seed`, `save_time`, `run_time`, and other run metadata.
- `card_library`: discovered card IDs and card pick/skip/win/loss stats. Detailed card metadata lives at `/api/v1/glossary/cards`, which currently requires a run context.
- `relic_collection`: discovered relic IDs. Detailed relic metadata lives at `/api/v1/glossary/relics`, which currently requires a run context.
- `potion_lab`: discovered potion IDs. Detailed potion metadata lives at `/api/v1/glossary/potions`, which currently requires a run context.
- `bestiary`: profile encounter/enemy fight stats plus a pointer to `/api/v1/bestiary` for reflected model metadata. The in-game Bestiary card is currently marked future/locked.
- `character_stats`: per-character and global profile totals.
- `run_history`: summaries of the active profile's saved `saves/history/*.run` files. If more than 20 files exist, the response includes the 20 most recent entries and the local `history_path`.

```jsonc
{
  "status": "ok",
  "kind": "compendium",
  "profile_id": 1,
  "progress_path": "modded/profile1/saves/progress.save",
  "resolved_progress_path": "C:/Users/timot/AppData/Roaming/SlayTheSpire2/steam/76561197985806660/modded/profile1/saves/progress.save",
  "profile_root": "modded/profile1",
  "save_scope": "modded",
  "current_run": {
    "is_in_progress": true,
    "profile_id": 1,
    "progress_path": "modded/profile1/saves/progress.save",
    "resolved_progress_path": "C:/Users/timot/AppData/Roaming/SlayTheSpire2/steam/76561197985806660/modded/profile1/saves/progress.save",
    "profile_root": "modded/profile1",
    "save_scope": "modded",
    "id_format": "{save_scope}:profile{profile_id}:{start_time}",
    "run_id": "modded:profile1:1778295706",
    "start_time": 1778295706,
    "seed": "2450ZAR9EF",
    "run_time": 137
  },
  "sections": {
    "card_library": { "status": "exposed", "discovered_ids": ["BASH"], "stats": [] },
    "relic_collection": { "status": "partially_exposed", "discovered_ids": ["BURNING_BLOOD"] },
    "potion_lab": { "status": "partially_exposed", "discovered_ids": [] },
    "bestiary": { "status": "locked_in_ui", "detail_endpoint": "/api/v1/bestiary" },
    "character_stats": { "status": "exposed", "characters": [], "global": {} },
    "run_history": {
      "status": "exposed",
      "history_path": "C:/.../SlayTheSpire2/steam/<account>/modded/profile1/saves/history",
      "entry_count": 10,
      "entries": [
        {
          "id": "1774869148",
          "run_id": "modded:profile1:1774869148",
          "players": [{ "id": 1, "character": "CHARACTER.IRONCLAD" }],
          "ascension": 0,
          "win": false,
          "run_time": 6541
        }
      ]
    }
  }
}
```

The response is built from `SaveManager.Progress`, the active profile's `progress.save` path, and local run-history files under the same active Steam account. It does not scan every Steam account under the save root. Run-history parsing is bounded to the 20 most recent `.run` files in the response so profile pages with many saved runs do not return unbounded payloads.

`current_run.run_id` is derived from save scope, profile id, and run `start_time`. Use it to identify one concrete run attempt. Use `seed` separately when grouping runs by generated content, because multiple attempts can share the same seed. `card_library`, `relic_collection`, and `potion_lab` report profile-level discovery only; rules text for those items is available from game state or from the run-scoped glossary endpoints below.

### `GET /api/v1/bestiary`

Returns reflected monster and encounter metadata as a deterministic structured object with `status`, `kind`, `monster_count`, `encounter_count`, `monsters`, and `encounters`. Profile-specific encounter and enemy fight stats are also summarized under `/api/v1/compendium`.

### `GET /api/v1/glossary/*`

The glossary endpoints expose active-run pool metadata. They require a run in progress and are scoped to the current run/character context plus shared run pools such as Colorless cards, shared relics, and shared potions, not profile-wide discovered content. Successful responses are structured objects with `status`, `kind`, `scope`, `count`, profile/save context (`profile_id`, `progress_path`, `resolved_progress_path`, `profile_root`, `save_scope`), `current_run` save context, `players`, and `items`; `current_run.run_id` and `current_run.seed` are included when `current_run.save` exposes them.

- `GET /api/v1/glossary/cards`: active-run card pool metadata, including energy/star costs, upgrade availability, plus upgraded-preview cost and description.
- `GET /api/v1/glossary/relics`: active-run relic pool metadata.
- `GET /api/v1/glossary/potions`: active-run potion pool metadata.
- `GET /api/v1/glossary/keywords`: keyword metadata collected from active-run cards, relics, and potions.

If no run is active, glossary endpoints return HTTP 409 with `error_code: "run_not_in_progress"` plus the same profile/save context fields so callers can identify the active profile even without a current run. If a run is marked in progress but the run state cannot be read, they return HTTP 503 with `error_code: "run_state_unavailable"` plus the same profile/save context fields.

Example success shape:

```json
{
  "status": "ok",
  "kind": "cards",
  "scope": "active_run",
  "count": 87,
  "profile_id": 1,
  "progress_path": "modded/profile1/saves/progress.save",
  "resolved_progress_path": "C:/Users/timot/AppData/Roaming/SlayTheSpire2/steam/76561197985806660/modded/profile1/saves/progress.save",
  "profile_root": "modded/profile1",
  "save_scope": "modded",
  "current_run": {
    "is_in_progress": true,
    "profile_id": 1,
    "progress_path": "modded/profile1/saves/progress.save",
    "resolved_progress_path": "C:/Users/timot/AppData/Roaming/SlayTheSpire2/steam/76561197985806660/modded/profile1/saves/progress.save",
    "profile_root": "modded/profile1",
    "save_scope": "modded",
    "id_format": "{save_scope}:profile{profile_id}:{start_time}",
    "run_id": "modded:profile1:1778295706",
    "seed": "VQY2JBY38L"
  },
  "items": [
    {
      "id": "STRIKE_RED",
      "name": "Strike",
      "type": "Attack",
      "cost": "1",
      "star_cost": null,
      "rarity": "Basic",
      "is_upgraded": false,
      "is_upgradable": true,
      "current_upgrade_level": 0,
      "max_upgrade_level": 1,
      "upgrade_preview_type": "None",
      "upgrade_preview_cost": "1",
      "upgrade_preview_star_cost": null,
      "upgrade_preview_description": "Deal 9 damage.",
      "keywords": []
    }
  ]
}
```

### `GET /api/v1/wiki`

Searches or lists wiki-style card and relic entries. With a query the endpoint fuzzy-ranks the matches and returns a bounded result set; **with no query it lists everything in scope**, in name order, which is what a tool building a static catalog wants. By default both modes are filtered to the active profile's discovered card and relic IDs.

Query parameters:

| Parameter | Values | Default | Description |
|---|---|---|---|
| `query` or `q` | text | *(none)* | Fuzzy search text, such as `ironclad perfect strike` or `silver spoon`. Omit it to list rather than search. |
| `item_type` or `type` | `all`, `card`, `relic` | `all` | Restricts the search to one wiki kind. |
| `limit` | integer | `10` searching, whole list otherwise | Maximum returned entries. The mod clamps values below 1 and above its internal maximum, which is higher for a listing than for a search. |
| `scope` | `discovered`, `all` | `discovered` | `all` ignores the profile's discovery progress and covers every card and relic the game defines. Tooling only — it spoils content the profile has not met. |

The response carries `mode` (`search` or `list`), `scope`, `truncated` (the limit cut the result short), and a `counts` block.

**`scope=all` is what a card catalog needs.** The discovery gate is a spoiler gate, not a data one: an undiscovered card still has a fully resolved description, and a tool that cannot see it writes a catalog entry with no text at all. An older DLL ignores the parameter and silently returns the discovered subset.

```http
GET /api/v1/wiki?scope=all&item_type=card
```

Card results include both `base` and `upgraded` variants when the card can be upgraded. The upgraded variant is built from a cloned preview, so the catalog model is not mutated.

```http
GET /api/v1/wiki?query=ironclad%20perfect%20strike&item_type=card&limit=3
```

```jsonc
{
  "status": "ok",
  "profile_id": 1,
  "query": "ironclad perfect strike",
  "item_type": "card",
  "limit": 3,
  "scope": "active_profile_discovered_cards_and_relics",
  "selection_policy": "Searches only cards and relics discovered by the active profile, then returns the best fuzzy matches instead of exposing the full catalog.",
  "counts": {
    "discovered_cards": 42,
    "discovered_relics": 18,
    "searched": 42,
    "returned": 1
  },
  "results": [
    {
      "item_type": "card",
      "id": "PERFECTED_STRIKE",
      "name": "Perfected Strike",
      "score": 775.123,
      "rarity": "Common",
      "type": "Attack",
      "is_upgradable": true,
      "base": {
        "variant": "base",
        "cost": "2",
        "description": "Deal ... damage.",
        "is_upgraded": false,
        "keywords": []
      },
      "upgraded": {
        "variant": "upgraded",
        "cost": "2",
        "description": "Deal ... damage.",
        "is_upgraded": true,
        "keywords": []
      }
    }
  ]
}
```

Relic searches use the same profile filter and ranking:

```http
GET /api/v1/wiki?query=silver%20spoon&item_type=relic
```

Relic results include `id`, `name`, `rarity`, `description`, and `keywords`.

### `GET /api/v1/cardpools`

Every unlocked card pool, in the order the game rolls them. Added in 0.5.0,
replacing the five per-state `combat_*_pool` fields.

The payload is **static for a run** but not across runs: in multiplayer the
unlock state is the union over all players, and the multiplayer constraint
follows the player count. Fetch it once when a run starts, not per tick.

```json
{
  "source": "run",                     // "run", "profile" (no run loaded), or "none"
  "multiplayer_constraint": "SingleplayerOnly",
  "characters": { "Ironclad": "ironclad" },   // character display name -> pool title
  "character_pool_order": ["ironclad", "silent", "defect", "necrobinder", "regent"],
  "pools": {
    "ironclad": {
      "is_colorless": false,
      "cards": [
        {
          "id": "BASH",
          "name": "Bash",
          "type": "Attack",
          "rarity": "Common",
          "pool": "ironclad",               // the card's OWN pool, which can differ
          "can_be_generated_in_combat": true
        }
      ]
    },
    "colorless": { "...": "..." }, "status": { "...": "..." }, "curse": { "...": "..." }
  }
}
```

Pools cover all five characters plus the shared Colorless, Curse, Deprecated,
Event, Quest, Status and Token pools.

**Card order is load-bearing.** The game's `Rng.NextItem` indexes straight into
these lists, so consumers must preserve the order exactly — sorting them changes
which card a roll produces. Two derived domains follow from this payload:

- *combat generation* (Call of the Void, Creative AI, Hello World, Spectrum
  Shift) — keep `can_be_generated_in_combat`, drop `Basic`/`Ancient`/`Event`;
- *transformation* (Entropy) — draw from the original card's own `pool`, or the
  colorless pool when the original is `Quest` type or `Event`/`Ancient`/`Token`
  rarity; keep only `Common`/`Uncommon`/`Rare` unless the original is `Status`
  or `Curse` (those draw from their whole pool); always exclude the original.

### `GET /api/v1/profiles`

Lists the three profile slots and identifies the active slot.

```json
{
  "status": "ok",
  "kind": "profiles",
  "current_profile_id": 1,
  "count": 3,
  "profiles": [
    {
      "id": 1,
      "profile_id": 1,
      "is_current": true,
      "has_data": true,
      "progress_path": "modded/profile1/saves/progress.save",
      "resolved_progress_path": "C:/.../SlayTheSpire2/steam/<account>/modded/profile1/saves/progress.save",
      "profile_root": "modded/profile1",
      "save_scope": "modded"
    },
    {
      "id": 2,
      "profile_id": 2,
      "is_current": false,
      "has_data": false,
      "progress_path": "modded/profile2/saves/progress.save",
      "resolved_progress_path": "modded/profile2/saves/progress.save",
      "profile_root": "modded/profile2",
      "save_scope": "modded"
    },
    {
      "id": 3,
      "profile_id": 3,
      "is_current": false,
      "has_data": true,
      "progress_path": "modded/profile3/saves/progress.save",
      "resolved_progress_path": "C:/.../SlayTheSpire2/steam/<account>/modded/profile3/saves/progress.save",
      "profile_root": "modded/profile3",
      "save_scope": "modded"
    }
  ]
}
```

### `POST /api/v1/profiles`

Switch to a profile slot through the game's profile UI:

```json
{ "action": "switch", "profile_id": 2 }
```

Delete an inactive profile slot:

```json
{ "action": "delete", "profile_id": 2 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `switch` or `delete` |
| `profile_id` | int | Yes | Profile slot, from 1 to 3 |

Switching is rejected during a run. Deleting the active profile is rejected; switch away first if you need to remove a slot.
Profile action validation returns structured HTTP errors: invalid profile IDs and unknown actions return HTTP 400, deleting the active profile returns HTTP 409 with `error_code: "active_profile_delete"`, and switching profiles during a run returns HTTP 409 with `error_code: "run_in_progress"`.

---

## POST — Perform Actions

All POST requests use a JSON body with an `"action"` field and action-specific parameters.
Action dispatch failures include structured `error_code` values. Missing or unknown main-menu `menu_select` options and unknown non-menu actions return HTTP 400; gameplay actions posted with no active run return HTTP 409 with `error_code: "run_not_in_progress"`. Older generic action failures use the stable fallback `error_code: "action_error"`. Other failed action attempts return non-2xx structured error JSON instead of HTTP 200.
If a tutorial or blocking popup is visible during a run, gameplay actions return HTTP 409 with `error_code: "blocking_popup_active"`; use the advertised `menu_select` option to dismiss or answer it first.

### Success Response

```jsonc
{ "status": "ok", "message": "Playing 'Strike' targeting Jaw Worm" }
```

### Error Response

Action failures — the action was understood but could not be performed — are returned with
**HTTP 200**:

```jsonc
{
  "status": "error",
  "error": "Card requires a target. Provide 'target' with an entity_id.",
  "error_code": "action_error"
}
```

Transport failures — invalid JSON, a missing `action` field, an unknown route, a wrong HTTP method,
the wrong run mode for the endpoint, or an unhandled exception — use an **HTTP 4xx/5xx** code but the
same body shape:

```jsonc
// HTTP 400
{ "status": "error", "error": "Missing 'action' field" }
```

So every POST response body carries `status` (`"ok"` or `"error"`), and `error` is present if and
only if `status` is `"error"`. Branching on `status` alone is enough; the HTTP code only distinguishes
*where* the failure happened.

### Unavailable Response (HTTP 503)

```jsonc
{ "error": "timed out after 10000 ms waiting for the game's main thread", "status": "error", "retry": true }
```

Every request — GET included — is executed on the game's main thread, which is pumped once per
rendered frame. A `503` with `"retry": true` means the request **was not executed**: the main thread
did not answer within 10 s, or more than 32 requests are already queued, which only happens when the
game is not processing frames (scene load, hang, minimized or paused app). Nothing was applied to the
run, so the same request may be sent again once the game is responsive. Contrast with `500`, where
the action ran and threw.

### Waiting for the Game to Settle

A POST returns as soon as the action has been dispatched on the main thread. Whether the animation,
card resolution and redraw it started have finished is *not* in the response, so a client that acts
on the reply is reading pre-resolution numbers — enemy HP before a multi-hit landed, a hand that has
not been redrawn. The usual workaround is a GET poll loop on `is_resolving`; `"wait": true` moves
that loop into the server, so one round trip both performs the action and returns the settled state.

```json
{ "action": "play_card", "card_uid": "STRIKE_IRONCLAD#1", "target": "NIBBIT_0", "wait": true }
```

| Parameter | Type | Default | Meaning |
|---|---|---|---|
| `wait` | bool | `false` | Wait for the game to settle before answering. Truthy forms: `true`, `1`, `"1"`, `"true"`, `"yes"`, `"on"`. Also readable from the query string (`?wait=1`, or a bare `?wait`). |
| `wait_timeout_ms` | int | `10000` | Upper bound on the wait, clamped to `500`–`30000`. |
| `include_state` | bool | `true` when `wait` is set | Whether to embed the settled state. |

Response, with the wait's own fields appended after the action's:

```jsonc
{
  "status": "ok",
  "message": "Playing 'Strike' targeting Nibbit",
  "waited_ms": 431,
  "polls": 6,
  "settled": true,
  "changed": true,
  "state": { "state_type": "monster", "...": "..." }
}
```

| Field | Meaning |
|---|---|
| `waited_ms` | Wall-clock duration of the wait. |
| `polls` | Number of state reads performed. |
| `settled` | `true` if the game came to rest; `false` means the timeout was reached and the state may still be mid-resolution. |
| `changed` | Whether anything observable moved compared with the state read just before the action. `false` means the action was a no-op. |
| `state` | The final state, identical in shape to `GET /api/v1/singleplayer`. Omitted when `include_state` is `false`, and when the wait could not read a state at all. |
| `wait_error` | Present only when the wait itself failed (the main thread stopped answering, or the state could not be built). The action already ran, so this is not a `503`. |

**The settle rule.** Every ~60 ms the server re-reads the state on the main thread and calls it
settled when `is_resolving` is false and, for `monster` / `elite` / `boss`, `battle.is_play_phase`
is true. It must look settled for **3 consecutive polls** (~180 ms) before the wait returns: a click
only takes effect on the following frame, and between two queued actions the game looks idle for a
single frame — one poll would routinely return a mid-resolution state. This mirrors the
empirically tuned loop every operator client ends up writing.

**The no-change rule.** A cheap fingerprint (state type, menu screen, floor, round, player HP /
block / energy, the uids in hand, each enemy's HP and block, the number of options on screen) is
taken just before the action and compared on every poll. If it has not moved within the first
**3000 ms**, the wait stops and returns `settled: true, changed: false` rather than burning the
whole timeout on an action the game ignored.

The wait never runs when the action returned `"status": "error"`, and a state that cannot be built
(it throws while a scene is swapping) counts as "not settled" and is simply polled again.

This is a **singleplayer-only** feature: `POST /api/v1/multiplayer` ignores `wait`,
`wait_timeout_ms` and `include_state`.

---

### `menu_select`

Select an option from the main menu, a menu submenu, profile select, character select, tutorial prompt, blocking popup, timeline screen, or game-over screen.

```json
{ "action": "menu_select", "option": "singleplayer" }
```

```json
{ "action": "menu_select", "option": "main_menu" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `option` | string | Yes | One of the current state's advertised menu options. Matching is case-insensitive. |
| `seed` | string | No | Only supported in menu contexts that expose a real seeded flow. Standard singleplayer character select currently returns an error without starting a run when `seed` is supplied. |

`game_over` advertises only `main_menu`. `continue` is not actionable on that screen and returns an error.
If `timeline` is blocked by pending obtained epochs, `menu_select` returns HTTP 409 with `error_code: "timeline_manual_action_required"`, `manual_action_required: true`, and `pending_epoch_ids` instead of opening Timeline.
When the game is already on `menu_screen: "timeline"`, `menu_select` with `option: "advance"` progresses safe Timeline UI steps:
tutorial/confirm/proceed buttons, queued unlock screens, epoch inspect close buttons, and visible obtained epoch slots.
If the Timeline is still animating, or the obtained epoch slot has not spawned yet, the response includes `retry: true` with `pending_epoch_ids`.
Pending `ObtainedNoSlot` epochs still return `manual_action_required: true` and `done: true` if no queued Timeline expansion screen or visible slot is available.
The main-menu `timeline` guard remains in place because entering Timeline from that state can still trigger invalid game unlock-state errors.

---

### `play_card`

Play a card from hand during combat.

```json
{ "action": "play_card", "card_index": 0, "target": "JAW_WORM_0" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `card_index` | int | Yes | 0-based index in hand |
| `target` | string | When hand card `requires_target` is true (or for `AnyAlly` cards) | `entity_id` from the card's `valid_targets` list, or the target combat_id as a string. For `AnyAlly` cards (multiplayer-only) it names a teammate — `players[].entity_id` (e.g. `"player_1"`) or its numeric `combat_id`; self is rejected, and if omitted/unresolvable the lowest-HP living teammate is targeted. `AnyPlayer` cards ignore `target` (the engine only accepts a null target for them). |

**Warning:** Playing a card removes it from hand — all higher indices shift left. Play from highest index first, or re-query state between plays.

### `use_potion`

Use a potion from the potion belt.

```json
{ "action": "use_potion", "slot": 0, "target": "JAW_WORM_0" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `slot` | int | Yes | Potion slot index |
| `target` | string | For `AnyEnemy` potions | `entity_id` of the target enemy (one of the potion's `valid_targets`), or the target combat_id as a string. For `AnyPlayer` potions it is optional and may name any living player — `players[].entity_id` (e.g. `"player_1"`) or numeric `combat_id`; defaults to self (this is how you throw a potion to a teammate). `AnyAlly` potions require a teammate (never self); default is the lowest-HP living teammate. |

**Errors:** Empty slot, combat-only potion used outside combat, automatic potion, already queued, player dead, custom usability blocked, missing/invalid target, enemy-targeted potion outside combat, unresolvable player target.

### `discard_potion`

Discard a potion to free up the slot. Use when potion slots are full and you need room for incoming potions (rewards, Cauldron, etc.).

```json
{ "action": "discard_potion", "slot": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `slot` | int | Yes | Potion slot index |

**Errors:** Empty slot, out-of-range slot, potion already queued.

### `end_turn`

End the player's combat turn.

```json
{ "action": "end_turn" }
```

**Errors:** Not in combat, not play phase, actions disabled, card mid-play, hand in selection mode.

### `combat_select_card`

Select a card during in-combat hand selection (exhaust, discard, upgrade prompts).

```json
{ "action": "combat_select_card", "card_index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `card_index` | int | Yes | Index in the hand-selection cards; the card must be visible and report `can_select: true` |

### `combat_confirm_selection`

Confirm the in-combat hand card selection when the visible confirm button is enabled.

```json
{ "action": "combat_confirm_selection" }
```

**Errors:** No selection active, confirm button not enabled (select more cards first).

### `claim_reward`

現在の報酬画面で ID を解決して操作を送る。推奨は ID 指定。

```json
{ "action": "claim_reward", "reward_id": "<rewards.items[].reward_id>", "wait": true }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `reward_id` | string | ID または index | 現在の画面の有効報酬 ID。推奨 |
| `index` | int | ID または index | 互換の現在一覧の 0 始まり位置。別名 `reward_index` |
| `screen_revision` | string | No | state の値をそのまま送る。古い値は ID 指定でも拒否 |

存在しない／別画面／取得済み／現在無効な ID はエラー。index へのフォールバックはしない。
ID と index を併記して別の報酬を指す場合もエラー。revision + index で古い一覧を検出できる。
**revision なしの旧 index 要求は古いか識別できず、現在位置として実行する。完全な安全保証はない。**
ポーション満杯のエラーは維持する。金・ポーション・レリック等は直接取得を開始し、card は三択を開く。
`special_card` は特定カードの直接取得であり、card の三択とは別の報酬。

成功応答の `status:ok` と `action_stage:submitted` は操作送信を表し、取得完了の保証ではない。
claim 応答に `reward_id` / `screen_revision`、カード選択／alternative 応答に `parent_reward_id` を追加する。
`wait:true` では UI の解決待ち後に `outcome_observed` / `selection_state` / `reward_consumed` を付ける。
`claimed` または `skipped` を確認できた場合のみ `action_stage:resolved` と
`claimable` / `selection_closed` を出す。タイムアウト・観測欠落・セッション終了時は成功を取得完了へ読み替えず、
`outcome_observed:false` とし、消費状態は不明にする。`settled:true` だけも取得完了を保証しない。
開封された三択・デッキ選択は入力待ちであり、その入力を待つためにサーバの settle 待ちを継続しない。

出典: `NRewardButton.GetReward`、`NRewardsScreen.RewardCollectedFrom / RewardSkippedFrom`、
`CardReward.OnSelect / Populate`、`NCardRewardSelectionScreen.OptionSelected`、
`CardRewardAlternative.Generate`、`SpecialCardReward.OnSelect`。

### `select_card_reward`

Pick a card from the card reward selection screen.

```json
{ "action": "select_card_reward", "card_index": 1 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `card_index` | int | Yes | 0-based card index |

### `skip_card_reward`

表示中の `Skip` option を選ぶ。カード三択を閉じ、親報酬は未消費で残り得る。
SACRIFICE や他の alternative の代用にはしない。
応答の `closes_selection_without_consuming_reward:true` は UI option の動作契約を表す。
実際の完了確認は `wait:true` 後の `outcome_observed:true` / `selection_closed:true` /
`reward_consumed:false` と同じ `parent_reward_id` で行う。待機なしの `submitted` は送信段階。
自動進行側は同じ親 ID の `skipped` を自動再開封対象から外し、他の報酬は個別に処理する。
明示的な再開封は可能。新項目のない旧 state は unknown と扱う。

```json
{ "action": "skip_card_reward" }
```

### `proceed`

Leave the current screen and open the map when a visible proceed button is enabled. Works from: rewards, rest site, shop, fake merchant, and treasure room; shop/fake merchant can close an open inventory first when the visible back button is enabled.

```json
{ "action": "proceed" }
```

**Note:** Does NOT work for events — use `choose_event_option` with the Proceed option's index instead.

### `choose_event_option`

Choose an event option.

```json
{ "action": "choose_event_option", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based index matching the option's `index` from state (locked options return an error) |

Works for both regular events and Ancients (after dialogue ends).

An option is rejected once it has been chosen (`was_chosen`), while its button
is disabled (`is_enabled: false`), or while a room transition is resolving.
Options whose body starts a fight run across several frames, so without those
guards a client polling fast enough re-picks the same option and fires it twice,
which wedges the run. Poll until the state changes instead of retrying.

### `advance_dialogue`

Click through Ancient dialogue.

```json
{ "action": "advance_dialogue" }
```

Call repeatedly until `in_dialogue` becomes `false` and event options appear.

### `choose_rest_option`

Choose a rest site option (rest, smith, etc.).

```json
{ "action": "choose_rest_option", "option_id": "SMITH" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `option_id` | string | One of the two | Matches the option's `id` from state, case-insensitively (`HEAL`, `SMITH`, `DIG`, …). Aliases: `id`, `option`. **Preferred** |
| `index` | int | One of the two | 0-based index matching the option's `index` from state. Alias: `option_index` |

The option is resolved in `RestSiteRoom.Options` (the same list the state numbers) and the
button is then looked up from that option, so the on-screen order of the buttons cannot make
this press a different option than the one asked for. Disabled options, unknown ids, and an
`option_id` that disagrees with a supplied `index` all return an error listing every option as
`[index] id (enabled/disabled)`.

Success adds the `option_id` and `index` of what was clicked:

```json
{ "status": "ok", "message": "Selecting rest site option: Smith", "option_id": "SMITH", "index": 1 }
```

### `shop_open`

Open a regular or fake merchant inventory. Game-state queries do not open it
automatically.

```json
{ "action": "shop_open" }
```

**Errors:** Not in shop, inventory UI not ready, inventory cannot be opened.

### `shop_purchase`

Purchase a shop item. The inventory must already be open via `shop_open`.

```json
{ "action": "shop_purchase", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based index in the flat items list |

**Errors:** Not in shop, inventory closed, item sold out, not enough gold, inventory not ready.

### `choose_map_node`

Travel to a map node.

```json
{ "action": "choose_map_node", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based index from `next_options` |

`next_options` is empty (and this action errors) whenever the current room is
not finished, a travel is already resolving, or an act change is still resolving
— `map.travel_enabled`, `map.travel_in_flight`, `map.transition_in_flight`, and the two halves of the
map-screen test (`map.screen_open` vs `map.screen_visible`; `Close()` clears `IsOpen` and only then
animates out, so open=false with visible=true is the just-arrived-in-a-room window)
say which. Sending a travel during either window wedges the run: a second map
vote lands on top of the one still resolving, and travelling during an act
change kills the tween `RunManager.EnterAct` is awaiting, which strands its
loading handles and disables every later event choice for the session. Both are
rejected rather than forwarded — wait for the flags to clear.

### `map_draw`

Add one or more drawing or eraser strokes to the open map. This calls the
game's native map-drawing methods, so strokes are saved with the run and are
broadcast to multiplayer peers.

```json
{
  "action": "map_draw",
  "strokes": [
    {
      "mode": "draw",
      "points": [
        { "col": 2, "row": 3 },
        { "col": 3, "row": 4 },
        { "x": 0.12, "y": 0.41 }
      ]
    }
  ]
}
```

Each point is either a normalized `{x,y}` coordinate or a live map-node
reference `{col,row}`. Node references resolve through the jittered live UI,
not an approximation from grid coordinates. `mode` is `draw` or `erase` and
defaults to `draw`. A request is limited to 64 strokes and 2,048 total points.

### `map_clear_drawings`

Clear only the local player's map drawings. In multiplayer, the clear is
broadcast using the game's reliable clear message.

```json
{ "action": "map_clear_drawings" }
```

### `select_card`

Select a card in a card selection overlay.

```json
{ "action": "select_card", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based card index in the grid; the card must be visible and report `can_select: true` |

**Behavior varies by screen type:**
- Grid screens (transform, upgrade, enchant, select): toggles selection through the game's own
  `OnCardClicked`, so a card that is already selected comes back out. Once `max_select` cards are selected,
  the screen opens its preview by itself.
- Choose-a-card screens (potions, effects): picks the card immediately.

On grid screens the result carries the outcome read back from the screen: `selected` (true when the card is
now in the selection), `selected_count`, `min_select` / `max_select`, and `preview_showing` when the press
opened the preview.

Returns `status: error` when the press would not land: while a preview is open (use `confirm_selection` or
`cancel_selection` — pressing through it corrupts the preview row), and when the screen refuses the press
because its selection is already full.

### `confirm_selection`

Confirm card selection (grid screens only).

```json
{ "action": "confirm_selection" }
```

With a preview open, presses the preview's own confirm, which applies the selection. Otherwise presses the
screen's confirm, which on most of these screens **opens the preview** rather than applying anything — the
message says which of the two happened, so a two-step screen needs two calls. Not needed for choose-a-card
screens.

Returns `status: error` rather than an `ok` that does nothing when fewer than `min_select` or more than
`max_select` cards are selected, when no confirm button is enabled, or when the press was swallowed (the
screen is still open and no preview appeared).

### `cancel_selection`

Cancel or close the card selection overlay.

```json
{ "action": "cancel_selection" }
```

**Behavior:**
- If a preview is showing: cancels back to the selection grid. This also **clears the selection** — the game's
  own cancel empties the set, so the grid comes back with nothing selected.
- For choose-a-card screens: clicks the skip button (if available).
- Otherwise: closes the selection screen (if cancellation is allowed).

### `select_bundle`

Open a bundle preview in the bundle selection screen.

```json
{ "action": "select_bundle", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based bundle index; the bundle must be visible and report `can_select: true` |

### `confirm_bundle_selection`

Confirm the currently previewed bundle when the visible confirm button is enabled.

```json
{ "action": "confirm_bundle_selection" }
```

### `cancel_bundle_selection`

Cancel the bundle preview when the visible cancel button is enabled.

```json
{ "action": "cancel_bundle_selection" }
```

### `select_relic`

Pick a relic from the relic choice screen. Selection is immediate.

```json
{ "action": "select_relic", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based relic index |

### `skip_relic_selection`

Skip the relic choice.

```json
{ "action": "skip_relic_selection" }
```

### `claim_treasure_relic`

Claim a relic from an opened treasure chest.

```json
{ "action": "claim_treasure_relic", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based relic index |

### `crystal_sphere_set_tool`

Switch divination tool in the Crystal Sphere minigame.

```json
{ "action": "crystal_sphere_set_tool", "tool": "big" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tool` | string | Yes | `"big"` or `"small"` |

### `crystal_sphere_click_cell`

Reveal a cell in the Crystal Sphere.

```json
{ "action": "crystal_sphere_click_cell", "x": 4, "y": 7 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `x` | int | Yes | Cell x-coordinate |
| `y` | int | Yes | Cell y-coordinate |

### `crystal_sphere_proceed`

Finish the Crystal Sphere minigame.

```json
{ "action": "crystal_sphere_proceed" }
```

---

## Actions added by the absorbed forks

These actions are dispatched by the same `POST /api/v1/singleplayer` route as the rest, and
each one returns the standard `{"status": "ok" | "error"}` body.

| Action | Parameters | Meaning |
|---|---|---|
| `return_to_main_menu` | none | Save and quit the current run to the main menu |
| `open_pause_menu` | none | Open the in-game pause menu |
| `resume` | none | Close the pause menu and resume the run |
| `give_up` | none | Abandon the run from the pause menu |
| `sell_potion` | `slot` (int) | Sell a potion at the shop |
| `select_card_reward_alternative` | reward screen indices | Pick the alternative card offered by a reward |
| `abandon_run` | none | Abandon the run from the main-menu continue screen |
| `timeline_reveal_epochs` | none | Reveal every obtained Timeline epoch |
| `set_recording` | `enabled` (bool) | Turn the combat recorder on or off for this session |
| `set_time_scale` | `scale` (number, 0.25–10.0) | Set the Godot engine time scale (animation speed) |
| `set_ascension` | `level` (int) | Set the run ascension level on the open character-select screen |

### Debug actions

Debug actions mutate the run directly. They exist for harness work and are not part of a
normal play flow.

| Action | Parameters | Meaning |
|---|---|---|
| `debug_start_encounter` | `encounter` (string, required) | Start the named encounter immediately |
| `debug_start_event` | `event` (string, required) | Enter the named event |
| `debug_start_shop` | none | Enter a shop room |
| `debug_force_play_phase` | none | Push the combat into the player's play phase |
| `debug_add_card` | `card` (string, required), `pile` (optional) | Add a card to a pile |
| `debug_add_power` | `power` (string, required), `amount` (number), `target` (optional) | Apply a power to a creature |
| `debug_enchant_card` | `enchantment` (string, required), `pile` (optional) | Enchant a card in the given pile |
| `debug_set_energy` | `amount` (int) | Set the player's energy |
| `debug_set_stars` | `amount` (int) | Set the player's stars (Regent) |
| `debug_gain_max_hp` | `amount` (int) | Raise max HP by the given amount |
| `debug_upgrade_deck` | none | Upgrade every card in the deck |
| `debug_enter_next_act` | none | Advance to the next act |

## Multiplayer Additions

### GET — Additional Top-Level Fields

```jsonc
{
  "game_mode": "multiplayer",
  "detail": "summary",        // `full` when requested
  "net_type": "SteamMultiplayer",
  "player_count": 2,
  "local_player_slot": 0,      // Index of local player in players array
  "players": [                  // Summary of all players
    {
      "entity_id": "player_0",  // Target id for AnyAlly/AnyPlayer cards & potions
      "character": "The Ironclad",
      "hp": 72, "max_hp": 80,
      "gold": 99,
      "is_alive": true,
      "is_local": true
      // In combat also: "combat_id" (numeric, also accepted as a target),
      // "block", "is_ready_to_end_turn", teammate "pets"
      //
      // For TEAMMATES in combat (not the local player) the entry also carries
      // that player's fully revealed combat state — MP combat is lockstep, so
      // every player's combat state is replicated client-side even though the
      // game UI hides teammate hands (cf. STS2-ShowPlayerHandCards). This is
      // enough to forward-model a teammate's turn:
      //   "energy", "max_energy"
      //   "hand": [ { card info: id/name/type/cost/description/... plus
      //              "index" and "target_type" } ]
      //   "draw_pile" / "discard_pile" / "exhaust_pile" / "play_pile": full card lists.
      //      draw_pile is in true order (index 0 = next draw), valid until
      //      that player's next shuffle — same guarantee as the local player.
      //   "draw_pile_count", "discard_pile_count", "exhaust_pile_count", "play_pile_count"
      //   "status": powers/debuffs (same shape as the local player's "status")
      //   "orbs" / "orb_slots" / "orb_empty_slots": Defect orb queue, if any
      //   "relics": same shape as the local player's "relics"
      //   "potions" / "max_potion_slots": same shape as the local player's
      // (omitted if the teammate's combat state is mid-transition)
    }
  ]
}
```

With `detail=full`, every top-level player additionally carries their stable
`slot_index`, string `net_id`, locale-independent `character_id`, complete
master `deck`, `relics` (including counters/runtime state), `potions`,
`max_potion_slots`, persistent `status`, and player-scoped `rng`. The response
also carries `relic_bag_generation`, a process-monotonic integer read in
constant time. Relic deque contents are deliberately not included here.

Fetch those separately from `GET /api/v1/relicbag`. That response carries the
same `relic_bag_generation`; join players by `slot_index` and retry when the two
generation values differ. The counter advances whenever any player's bag is
populated, loaded, pruned, refreshed, pulled from, explicitly removed from, or
moved into the multiplayer fallback deque. It detects bag drift between the two
requests without hashing or traversing bags on the main multiplayer endpoint.

### Relic Grab Bags

`GET /api/v1/relicbag` remains the sole exporter of each player's ordered relic
deques:

```jsonc
{
  "relic_bag_generation": 42,
  "players": [
    {
      "net_id": 123456789,
      "slot_index": 0,
      "is_me": true,
      "deques": {
        "common": ["AKABEKO", "ANCHOR"],
        "uncommon": [],
        "rare": [],
        "shop": [],
        "mp_fallback": []
      }
    }
  ]
}
```

`mp_fallback` is always exported, including an empty list. If its private source queue cannot be read, the bag export is unavailable rather than claiming exhaustion. After the normal rarity escalation is exhausted, front rewards and filtered merchant back pulls draw from this queue.

Reward sources pull from the front, merchants pull from the back, and declined
relics have already left the lists. Never sort or cache these deques across
rooms.

### Battle State Additions

```jsonc
{
  "battle": {
    "all_players_ready": false,
    "local_player_ready_to_end_turn": false,
    "can_end_turn": true,
    "end_turn_blocked_reason": null,
    "can_undo_end_turn": false,
    "undo_end_turn_blocked_reason": "NotReady"
    // Same shape as singleplayer (round, turn, is_play_phase, enemies).
    // Player state lives in top-level "player" (local) and "players" (all).

    // Every card play this combat, ALL players (MP combat is lockstep, so
    // the local client's history includes remote players' plays).  Appended
    // in play order; "index" is a stable per-combat sequence number, so
    // pollers can dedup with "index > last seen".  Cleared at combat start.
    // NOTE: emitted in SINGLEPLAYER TOO, despite living in this section.
    "card_plays": [
      {
        "index": 0,
        "round": 1,
        "player_turn": 1,             // the OWNER's turn number. Prefer this
                                       // over "round" for grouping plays into
                                       // turns: an extra turn carries the same
                                       // round as the turn before it.
        "player": "The Ironclad",
        "is_local": true,
        "card_id": "STRIKE",
        "card_name": "Strike",
        "combat_card_id": 12,         // physical instance, joins to the pile
                                       // snapshots; null if unavailable
        "result_pile": "Discard",     // where the card went after resolving —
                                       // "Discard" vs "Exhaust" vs "Hand"
        "play_index": 0,              // position in a Replay series
        "play_count": 1,              // total plays in that series
        "is_auto_play": false,        // true = Mayhem-style, not played by hand
        "target": "KIN_PRIEST"        // monster model id, player character
                                       // title for ally-targeted cards, or
                                       // null for untargeted cards
      }
    ],

    // Where every card WENT during the current and previous player turn.
    // "card_plays" answers "what was played"; this answers everything else.
    // Together they reconstruct a whole turn's card churn without diffing pile
    // snapshots across polls — which cannot work on its own, because a card
    // drawn and discarded between two polls is never observed in any pile, and
    // a card mid-resolution sits in a "Play" pile that is not exported.
    //
    // IMPORTANT: the end-of-turn hand flush writes NOTHING here.  The game
    // moves leftover cards with CardPileCmd.Add rather than CardCmd.Discard,
    // and only the latter reaches the combat history.  So every "discarded"
    // row is a MID-TURN discard, and "was this card simply left in hand?" must
    // be answered from the hand itself (see "should_retain_this_turn").
    //
    // Bounded to this turn plus each owner's previous turn, so a long fight
    // does not re-serialize hundreds of rows every poll.  Reading the previous
    // turn is what lets a client wait until a turn is complete — end-of-turn
    // cleanup resolves after the turn ends — before acting on it.
    // Emitted in singleplayer too.
    "card_history": [
      {
        "index": 0,
        "kind": "drawn",              // played | drawn | discarded |
                                       // exhausted | generated
        "round": 1,
        "player_turn": 1,             // the OWNER's turn number; see above
        "is_local": true,
        "card_id": "STRIKE",
        "card_name": "Strike",
        "is_upgraded": false,
        "combat_card_id": 12,         // physical instance; null if unavailable
        "keyword_ids": ["Sly"]        // machine-readable CardKeyword names
      }
    ],

    // Unblocked (HP) damage dealt TO ENEMIES, per player per round.
    // by_round[i] = damage dealt in round i+1; array length = current round.
    // Pet damage credits the pet's owner.  Dealer-less damage (e.g. some
    // DoTs) is not attributed and therefore not counted.
    "player_damage": [
      {
        "player": "The Ironclad",
        "is_local": true,
        "by_round": [18, 25],
        "total": 43
      }
    ]
  },
  "players": [
    {
      "character": "The Ironclad",
      "hp": 72, "max_hp": 80,
      "gold": 99,
      "is_alive": true,
      "is_local": true,
      "is_ready_to_end_turn": false   // Only present during combat
    },
    {
      "character": "The Necrobinder",
      "hp": 60, "max_hp": 66,
      "gold": 80,
      "is_alive": true,
      "is_local": false,
      "is_ready_to_end_turn": false,
      "pets": [                       // Omitted for local player (pets are under top-level "player")
        {
          "id": "OSTY",
          "name": "Otsy",
          "alive": true,
          "hp": 12, "max_hp": 12,
          "block": 0,
          "status": [ /* Power Objects */ ]
        }
      ]
    }
  ]
}
```

### Map State Additions

```jsonc
{
  "map": {
    "votes": [
      {
        "player": "The Ironclad",
        "is_local": true,
        "voted": true,
        "vote_col": 2, "vote_row": 3
      }
    ],
    "all_voted": false
  }
}
```

### Event State Additions

```jsonc
{
  "event": {
    "is_shared": true,
    "votes": [
      {
        "player": "The Ironclad",
        "is_local": true,
        "voted": true,
        "vote_option": 0
      }
    ],
    "all_voted": false
  }
}
```

### Treasure State Additions

```jsonc
{
  "treasure": {
    "is_bidding_phase": true,
    "bids": [
      {
        "player": "The Ironclad",
        "is_local": true,
        "voted": true,
        "vote_relic_index": 0
      }
    ],
    "all_bid": false
  }
}
```

### Multiplayer-Only Actions

**End turn (vote):**
```json
{ "action": "end_turn" }
```
In multiplayer, this is a vote. The turn ends only when all players submit. Use `battle.can_end_turn` / `battle.end_turn_blocked_reason` to check local readiness.

**Undo end turn:**
```json
{ "action": "undo_end_turn" }
```
Retract the end-turn vote before all players have committed. Use `battle.can_undo_end_turn` / `battle.undo_end_turn_blocked_reason` to check local readiness.

All other actions work identically to singleplayer.

## Exact card damage variables

Cards with canonical `Damage`, `OstyDamage`, or `CalculatedDamage` variables include a `damage_var` object. Numeric values are invariant decimal strings, independent of localized or rounded descriptions:

```json
{"base":"8.5","enchantment_additive":"0","enchantment_multiplier":"2"}
```

`base` is the canonical value before enchantments and combat hooks. Calculated damage also includes `extra`; calculate `base + extra * current_count` using the card's current state and target. Apply the additive enchantment value, then its multiplier, before global combat modifiers such as Strength and Weak. These enchantment values reflect the current observation.

The field is exported in every shared card context, including Hand, other piles, selections, decks, rewards, and shops. It is present regardless of the description's `values` mode. Cards without these canonical variables omit it.

For Momentum and active Vigorous enchantments, `damage_var.after_play` describes the mutable callback. Momentum exports `{"kind":"Momentum","amount":"3"}` (with its actual amount): increase the additive bonus after the card's own commands resolve. Active Vigorous exports `{"kind":"Vigorous"}`: disable the bonus during its normal `AfterCardPlayed` listener, after owner relic listeners. Disabled Vigorous omits this field and exports zero additive damage. These updates apply between executions of a repeated card.

Corrupted exports `damage_var.after_play` as `{"kind":"Corrupted"}`. After the card's own commands, it deals 2 unblockable, unpowered damage to its owner. Await damage callbacks before recording the completed play or running its post-card listeners; owner death cancels completion. The intrinsic 1.5 damage multiplier remains in `enchantment_multiplier`.

Corrupted cards also export `hp_loss_var`: the card's own canonical `HpLoss` base as an exact decimal string, or null when that variable is absent. This separates intrinsic self-damage (for example Hemokinesis's first hit) from Corrupted's appended `Lose 2 HP.` description line and its later enchantment hit. Native snapshots preserve the parsed intrinsic amount in `native_self_damage`.

## Master-deck card links

Card objects include `deck_card_index`: the current zero-based master-deck index of the card itself (in the Deck pile) or its `DeckVersion` (in combat). It is `null` when no current deck entry exists. This position can change after deck edits; it is not a permanent ID. `has_deck_version` separately reports whether a parent pointer exists, including a parent removed from the deck.

## Saved card counters

Guilty card objects include `combats_seen`, its saved completed-fight counter (normally 0 through 4). `AfterCombatEnd` increments this field only for the master card in the Deck pile, removing it at 5. Combat copies retain their own saved value. The description shows `5 - combats_seen`, with singular `combat` at one remaining fight.


### Fixed card returns and card object identity

Mutable cards include `card_object_id`, an opaque positive integer stable for that physical object during the mod session. This is separate from the shifting `deck_card_index` and is not an action target. Card references in power `runtime` also include `deck_version`: the complete saved master card or `null`. For Swipe, the removed combat face and its master can have different upgrades or saved properties.

`battle.special_card_rewards` lists pending local-player fixed-card rewards, including cards returned when Hopper dies. Each entry has `type: "special_card"`, `card_reference` (the master's `card_object_id`), `card` (the full master snapshot), and `encounter_source`. Reward-screen items of type `special_card` expose the same fields. A returned card remains outside the deck until `claim_reward` succeeds; repeated entries can refer to the same physical card. This list survives the dead enemy's power cleanup. An empty list means no pending fixed-card rewards.
