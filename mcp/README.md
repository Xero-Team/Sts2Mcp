# MCP Tools

## Layout

`server.py` at this directory's root is the executable entry point. The
implementation is the `sts2mcp` package:

| Module | Contents |
|---|---|
| `sts2mcp/app.py` | shared FastMCP instance |
| `sts2mcp/client.py` | HTTP transport for `localhost:15526` and its route helpers |
| `sts2mcp/errors.py` | structured endpoint-error rendering |
| `sts2mcp/tools/general.py` | startup, menu, potion, and run-wide actions |
| `sts2mcp/tools/profiles.py` | profile, compendium, wiki, glossary, settings, telemetry |
| `sts2mcp/tools/combat.py` | combat and in-combat card selection |
| `sts2mcp/tools/rewards.py` | post-combat rewards and card rewards |
| `sts2mcp/tools/map.py` | map travel and map drawings |
| `sts2mcp/tools/rooms.py` | rest site, shop, and event screens |
| `sts2mcp/tools/selection.py` | card, bundle, relic, treasure, Crystal Sphere |
| `sts2mcp/tools/debug.py` | session recorder and debug sandbox |
| `sts2mcp/tools/multiplayer.py` | every `mp_*` tool |
| `sts2mcp/telemetry.py` | playtest-ledger aggregation |

Add a tool to the module that matches its screen; `sts2mcp/tools/__init__.py`
imports every module, so a decorated function is registered automatically.

Lint and format the bridge with ruff (`make lint-mcp`, `make format-mcp`, or
`uv run --project mcp ruff check mcp`). The configuration lives in
`pyproject.toml`; `make verify-mcp` runs the lint, format check, and bridge
tests together.

## Singleplayer

| Tool | Scope | Description |
|---|---|---|
| `get_api_index()` | General | Get mod status, bound prefixes, and HTTP endpoint index |
| `get_game_state(format?)` | General | Get current game state (`markdown` or `json`) |
| `menu_select(option, seed?)` | General | Select a visible menu/game-over option, including `abandon_run` when advertised |
| `open_pause_menu()` | General | Open the in-run pause menu (resume / settings / compendium / give_up / save_and_quit) |
| `timeline_reveal_epochs()` | General | Reveal obtained-but-unrevealed timeline epochs from the main menu (repeat until `done`) |
| `return_to_main_menu(wait?)` | General | Save the run and return to the main menu (the run stays resumable) |
| `set_time_scale(scale)` | General | Set the Godot time scale (0.25–10.0) to speed up or slow down animations |
| `set_ascension(level)` | General | Set the run ascension level on the open character-select screen |
| `set_recording(enabled)` | Recorder | Turn the combat recorder on or off for this session |
| `debug_start_encounter(encounter)` | Debug | Start the named encounter immediately |
| `debug_start_event(event)` | Debug | Enter the named event |
| `debug_start_shop()` | Debug | Enter a shop room immediately |
| `debug_force_play_phase()` | Debug | Force the current combat into the play phase |
| `debug_add_card(card, pile?)` | Debug | Add a card to a pile |
| `debug_add_power(power, amount, target?)` | Debug | Apply a power to a creature |
| `debug_enchant_card(enchantment, pile?)` | Debug | Enchant a card in the given pile |
| `debug_set_energy(amount)` | Debug | Set the player's energy |
| `debug_set_stars(amount)` | Debug | Set the player's stars |
| `debug_gain_max_hp(amount)` | Debug | Raise max HP |
| `debug_upgrade_deck()` | Debug | Upgrade every card in the deck |
| `debug_enter_next_act()` | Debug | Advance the run to the next act |
| `get_settings()` | General | Get current settings and preferences |
| `get_profile()` | Profiles | Get active profile progress plus save/run context |
| `get_compendium()` | Profiles | Get Compendium-shaped profile progress plus save/run context |
| `get_playtest_metrics(...)` | Profiles | Summarize the local playtest ledger (filters: character, build, ascension, time window) |
| `search_wiki(query, item_type?, limit?)` | Profiles | Fuzzy-search discovered card and relic wiki entries |
| `get_bestiary()` | Profiles | Get deterministic monster and encounter metadata with counts |
| `get_glossary_cards()` | Active Pool | Get active-run card pool metadata with current-run save context |
| `get_glossary_relics()` | Active Pool | Get active-run relic pool metadata with current-run save context |
| `get_glossary_potions()` | Active Pool | Get active-run potion pool metadata with current-run save context |
| `get_glossary_keywords()` | Active Pool | Get active-run keyword metadata with current-run save context |
| `list_profiles()` | Profiles | List profile slots and active slot |
| `switch_profile(profile_id)` | Profiles | Switch to a profile slot through the game UI |
| `delete_profile(profile_id)` | Profiles | Delete an inactive profile slot |
| `use_potion(slot, target?, wait?)` | General | Use a potion (works in and out of combat) |
| `sell_potion(slot)` | General | Sell a merchant-interaction potion (Foul Potion's 100-gold mode) |
| `discard_potion(slot)` | General | Discard a potion to free up the slot |
| `proceed_to_map(wait?)` | General | Proceed from rewards/rest site/shop/treasure to the map |
| `combat_play_card(card_index, target?, card_uid?, wait?)` | Combat | Play a card from hand |
| `combat_end_turn(wait?)` | Combat | End the current turn |
| `combat_select_card(card_index)` | Combat Selection | Select a card from hand during exhaust/discard prompts |
| `combat_confirm_selection()` | Combat Selection | Confirm the in-combat card selection |
| `rewards_claim(reward_index)` | Rewards | Claim a reward from the post-combat screen |
| `rewards_pick_card(card_index)` | Rewards | Select a card from the card reward screen |
| `rewards_skip_card()` | Rewards | Skip the card reward |
| `rewards_card_alternative(option_id)` | Rewards | Take an alternative option from `card_reward.alternatives` (Skip, SACRIFICE, …) |
| `get_map_drawings()` | Map | Read strokes and live node drawing positions from the dedicated endpoint |
| `map_choose_node(node_index)` | Map | Choose a map node to travel to |
| `map_draw(strokes)` | Map | Draw/erase normalized polylines or paths through map-node references |
| `map_clear_drawings()` | Map | Clear the local player's map drawings |
| `rest_choose_option(option_index, option_id)` | Rest Site | Choose a rest site option (rest, smith, dig, …). Prefer `option_id` from `rest_site.options[].id` |
| `shop_open_inventory()` | Shop | Open a regular or fake merchant's inventory |
| `shop_purchase(item_index)` | Shop | Purchase an item from the shop |
| `event_choose_option(option_index)` | Event | Choose an event option (including Proceed) |
| `event_advance_dialogue()` | Event | Advance ancient event dialogue |
| `deck_select_card(card_index)` | Card Select | Pick/toggle a card in the selection screen |
| `deck_confirm_selection()` | Card Select | Confirm the current card selection |
| `deck_cancel_selection()` | Card Select | Cancel/skip card selection |
| `bundle_select(bundle_index)` | Bundle Select | Open a bundle preview |
| `bundle_confirm_selection()` | Bundle Select | Confirm the current bundle preview |
| `bundle_cancel_selection()` | Bundle Select | Cancel the current bundle preview |
| `relic_select(relic_index)` | Relic Select | Choose a relic from the selection screen |
| `relic_skip()` | Relic Select | Skip relic selection |
| `treasure_claim_relic(relic_index)` | Treasure | Claim a relic from the treasure chest |
| `crystal_sphere_set_tool(tool)` | Crystal Sphere | Switch the active divination tool |
| `crystal_sphere_click_cell(x, y)` | Crystal Sphere | Click a hidden cell in the grid |
| `crystal_sphere_proceed()` | Crystal Sphere | Continue after the minigame finishes |

### Waiting for the Game to Settle (`wait?`)

`combat_play_card`, `combat_end_turn`, `use_potion` and `proceed_to_map` accept `wait=True`. The mod
then holds the HTTP response until the game has settled — animations, card resolution and the redraw
finished — and returns the settled state with it, replacing the usual "act, then poll
`get_game_state` until `is_resolving` is false" loop with a single call.

The reply gains `settled`, `changed`, `waited_ms`, `polls` and `state` (the same payload
`get_game_state(format="json")` would return). `changed: false` means the action moved nothing
observable; `settled: false` means the wait timed out and the state may still be mid-resolution.

These are the hot paths, so only they expose it as a tool parameter; every other singleplayer action
still accepts `"wait": true` (plus `wait_timeout_ms` and `include_state`) over raw HTTP. Multiplayer
ignores it.

### Profile Tools

`get_profile()` returns the raw active-profile progress summary: character totals, global totals, discoveries, achievements, epochs, and aggregate stats.

`get_compendium()` presents the same profile data in the shape agents usually need when reasoning about long-term progress. It groups the response into `card_library`, `relic_collection`, `potion_lab`, `bestiary`, `character_stats`, and `run_history`, matching the high-level Compendium cards in the game UI. It also includes `current_run` while a run is active, with a derived `run_id` in `{save_scope}:profile{profile_id}:{start_time}` format.

The Compendium response is profile-scoped, not run-state-scoped. It works from the main menu, does not require navigating the in-game Compendium UI, and summarizes only the 20 most recent saved run-history files to keep the MCP response bounded.

`search_wiki(query, item_type="all", limit=10)` is the selective wiki lookup. It fuzzy-matches names and IDs such as `ironclad perfect strike` or `silver spoon`, but it searches only cards and relics discovered by the active profile. The default response is capped at 10 entries; callers may pass a higher or lower `limit`, and the mod clamps oversized requests. Card matches include `base` and `upgraded` blocks so agents can compare pre-upgrade and post-upgrade text without asking for the full card library.

### Response Envelope and Errors

`get_api_index()` returns `status`, `kind: api_index`, `version`, `endpoint_count`, bound listener prefixes, and the advertised HTTP routes.

`get_game_state()` and `mp_get_game_state()` JSON responses include `status: ok`, `kind` (`singleplayer_state` or `multiplayer_state`), `state_type`, and the active-run context fields when a run is in progress.

Profile, profile-list, and Compendium tools include `status`, `kind`, `profile_id`, `progress_path`, `resolved_progress_path`, `profile_root`, and `save_scope`; profile and Compendium responses also include `current_run` when a run is active, so callers can distinguish the active profile, save scope, local save location, and current run attempt. Save/path context fields are normalized with forward slashes, including Windows absolute paths.

Profile switch/delete failures are surfaced as structured endpoint errors with HTTP 400 for invalid input and HTTP 409 for state conflicts such as deleting the active profile or switching during a run. POST validation failures preserve endpoint `error_code` values such as `invalid_json`, `missing_action`, `invalid_action_type`, `missing_profile_id`, `invalid_profile_id_type`, and `invalid_action_payload`.

Route-level failures preserve endpoint `error_code` values such as `method_not_allowed`, `not_found`, and `internal_error`. Read endpoint startup/data-availability failures preserve `save_manager_unavailable`, `settings_data_unavailable`, and `profile_data_unavailable`. Read endpoint exceptions preserve endpoint-specific codes such as `settings_read_failed`, `bestiary_build_failed`, `glossary_build_failed`, `profile_build_failed`, `profiles_read_failed`, `compendium_build_failed`, `singleplayer_state_read_failed`, and `multiplayer_state_read_failed`.

Menu/action dispatch failures also use structured endpoint errors: missing or unknown main-menu selections and unknown actions return HTTP 400, gameplay actions sent without an active run return HTTP 409, Timeline manual-reveal blocks return HTTP 409 with `error_code: timeline_manual_action_required`, and blocking tutorial/popup overlays return HTTP 409 with `error_code: blocking_popup_active`. Older generic action failures carry the stable fallback `error_code: action_error` instead of omitting an error code. MCP wrappers preserve those structured JSON error bodies and add `http_status` so callers can handle endpoint failures without parsing a flat text prefix.

Glossary tools require an active run. They include the current character context plus shared run pools such as Colorless cards, shared relics, and shared potions. Card glossary items include energy/star costs, upgrade availability, plus upgraded-preview cost and description. Successful responses include `profile_id`, `progress_path`, `resolved_progress_path`, `profile_root`, `save_scope`, `current_run` save context, `kind`, `count`, and `items`; `current_run.run_id` and `current_run.seed` are included when `current_run.save` exposes them. The HTTP endpoints return `run_not_in_progress` with HTTP 409 when called from the main menu, or `run_state_unavailable` with HTTP 503 if the active run state cannot be read, while still including the active profile/save context fields.

## Multiplayer

All multiplayer tools are prefixed with `mp_`. They route through `/api/v1/multiplayer` and are only available during multiplayer (co-op) runs. The endpoints automatically guard against cross-mode calls.

| Tool | Scope | Description |
|---|---|---|
| `mp_get_game_state(format?)` | General | Get multiplayer game state (all players, votes, bids) |
| `mp_combat_play_card(card_index, target?)` | Combat | Play a card from the local player's hand |
| `mp_combat_end_turn()` | Combat | Submit end-turn vote (turn ends when all players submit) |
| `mp_combat_undo_end_turn()` | Combat | Retract end-turn vote |
| `mp_use_potion(slot, target?)` | General | Use a potion from the local player's slots |
| `mp_discard_potion(slot)` | General | Discard a potion from the local player's slots |
| `mp_proceed_to_map()` | General | Proceed from rewards/rest site/shop/fake merchant/treasure to the map |
| `mp_map_vote(node_index)` | Map | Vote for a map node (travel when all agree) |
| `mp_map_draw(strokes)` | Map | Draw/erase strokes and broadcast them to peers |
| `mp_map_clear_drawings()` | Map | Clear local drawings and broadcast the clear |
| `mp_event_choose_option(option_index)` | Event | Vote for / choose an event option |
| `mp_event_advance_dialogue()` | Event | Advance ancient event dialogue |
| `mp_rest_choose_option(option_index)` | Rest Site | Choose a rest site option (per-player, no vote) |
| `mp_shop_open_inventory()` | Shop | Open the local player's shop inventory |
| `mp_shop_purchase(item_index)` | Shop | Purchase an item (per-player inventory) |
| `mp_rewards_claim(reward_index)` | Rewards | Claim a post-combat reward |
| `mp_rewards_pick_card(card_index)` | Rewards | Select a card from the card reward screen |
| `mp_rewards_skip_card()` | Rewards | Skip the card reward |
| `mp_deck_select_card(card_index)` | Card Select | Pick/toggle a card in the selection screen |
| `mp_deck_confirm_selection()` | Card Select | Confirm the current card selection |
| `mp_deck_cancel_selection()` | Card Select | Cancel/skip card selection |
| `mp_bundle_select(bundle_index)` | Bundle Select | Open a bundle preview |
| `mp_bundle_confirm_selection()` | Bundle Select | Confirm the current bundle preview |
| `mp_bundle_cancel_selection()` | Bundle Select | Cancel the current bundle preview |
| `mp_combat_select_card(card_index)` | Combat Selection | Select a card during in-combat selection prompts |
| `mp_combat_confirm_selection()` | Combat Selection | Confirm in-combat card selection |
| `mp_relic_select(relic_index)` | Relic Select | Choose a relic from the selection screen |
| `mp_relic_skip()` | Relic Select | Skip relic selection |
| `mp_treasure_claim_relic(relic_index)` | Treasure | Bid on a relic (relic fight if contested) |
| `mp_crystal_sphere_set_tool(tool)` | Crystal Sphere | Switch the active divination tool |
| `mp_crystal_sphere_click_cell(x, y)` | Crystal Sphere | Click a hidden cell in the grid |
| `mp_crystal_sphere_proceed()` | Crystal Sphere | Continue after the minigame finishes |
