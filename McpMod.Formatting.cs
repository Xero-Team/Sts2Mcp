using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace STS2_MCP;

public static partial class McpMod
{
    private static string FormatAsMarkdown(Dictionary<string, object?> state)
    {
        var sb = new StringBuilder();
        string stateType = state.TryGetValue("state_type", out var st) ? st?.ToString() ?? "unknown" : "unknown";
        bool isMultiplayer = state.TryGetValue("game_mode", out var gm) && string.Equals(gm?.ToString(), "multiplayer", StringComparison.Ordinal);

        if (isMultiplayer)
            sb.AppendLine($"# Multiplayer Game State: {stateType}");
        else
            sb.AppendLine($"# Game State: {stateType}");
        sb.AppendLine();
        // So a pasted markdown state says which build produced it, without a second request.
        string modVersion = state.TryGetValue("mod_version", out var mv) ? mv?.ToString() ?? Version : Version;
        string modCommit = state.TryGetValue("mod_commit", out var mc) ? mc?.ToString() ?? "" : "";
        string schema = state.TryGetValue("schema_version", out var sv) ? sv?.ToString() ?? "" : "";
        sb.AppendLine($"mod {modVersion}{(modCommit.Length > 0 ? "+" + modCommit : "")}{(schema.Length > 0 ? " · schema " + schema : "")}");
        sb.AppendLine();

        if (state.TryGetValue("run", out var runObj) && runObj is Dictionary<string, object?> run)
        {
            sb.AppendLine($"**Act {run["act"]}** | Floor {run["floor"]} | Ascension {run["ascension"]}");
            sb.AppendLine();
        }

        // Loud on purpose: every number below is read live, so a state taken mid-animation
        // reports pre-resolution values.
        if (state.TryGetValue("is_resolving", out var resolving) && resolving is true)
        {
            string why = state.TryGetValue("resolving_reasons", out var rr) && rr is List<string> rl && rl.Count > 0
                ? $" ({string.Join(", ", rl)})" : "";
            sb.AppendLine($"> ⚠ **STILL RESOLVING{why}** — values below may be pre-resolution. Re-read until is_resolving is false.");
            sb.AppendLine();
        }

        // A pasted markdown state is often all a reader gets, so it has to say when parts
        // of it could not be built - otherwise a field that failed reads as a field that
        // is not there.
        if (state.TryGetValue("warnings", out var warningsObj) && warningsObj is List<string> warnings && warnings.Count > 0)
        {
            var head = string.Join(" | ", warnings.GetRange(0, Math.Min(3, warnings.Count)));
            sb.AppendLine(CultureInfo.InvariantCulture, $"> ⚠ **{warnings.Count} state warning(s)** — {head}");
            sb.AppendLine();
        }

        if (string.Equals(stateType, "menu", StringComparison.Ordinal))
        {
            FormatMenuMarkdown(sb, state);
            return sb.ToString();
        }

        if (string.Equals(stateType, "game_over", StringComparison.Ordinal))
        {
            FormatGameOverMarkdown(sb, state);
            return sb.ToString();
        }

        // A "message" is a note about the screen, not a replacement for it. Returning
        // here dropped every following section, which is why some screens (treasure
        // among them) rendered as a single line of prose with no contents at all.
        if (state.TryGetValue("message", out var msg) && msg != null)
        {
            sb.AppendLine(msg.ToString());
            sb.AppendLine();
        }

        if (state.GetValueOrDefault("player") is Dictionary<string, object?> poolPlayer
            && poolPlayer.GetValueOrDefault("generation_pool") is Dictionary<string, object?> pool)
        {
            string summary = pool.GetValueOrDefault("known") is true
                && pool.GetValueOrDefault("cards") is List<Dictionary<string, object?>> cards
                ? $"{cards.Count} cards (Attack/Skill/Power), {pool.GetValueOrDefault("players")} player(s)"
                : $"unknown: {pool.GetValueOrDefault("reason")}";
            sb.AppendLine($"Generation pool: {summary}");
            sb.AppendLine();
        }

        // Multiplayer players summary (top-level)
        if (isMultiplayer && state.TryGetValue("players", out var playersListObj)
            && playersListObj is List<Dictionary<string, object?>> playersList && playersList.Count > 0)
        {
            sb.AppendLine("## Party");
            foreach (var p in playersList)
            {
                string youTag = p["is_local"] is true ? " **(YOU)**" : "";
                string aliveTag = p["is_alive"] is false ? " [DEAD]" : "";
                string readyTag = p.TryGetValue("is_ready_to_end_turn", out var rdy) && rdy is true ? " [READY]" : "";
                sb.AppendLine($"- **{p["character"]}**{youTag}{aliveTag}{readyTag} - HP: {p["hp"]}/{p["max_hp"]} | Gold: {p["gold"]}");

                // Show teammate pets inline
                if (p.TryGetValue("pets", out var tPetsObj) && tPetsObj is List<Dictionary<string, object?>> tPets)
                {
                    foreach (var pet in tPets)
                    {
                        bool petAlive = pet.TryGetValue("alive", out var pa) && pa is true;
                        string petStatus = petAlive
                            ? $"HP: {pet["hp"]}/{pet["max_hp"]} | Block: {pet["block"]}"
                            : "DEAD";
                        sb.AppendLine($"  - Pet: **{pet["name"]}** - {petStatus}");
                    }
                }
            }
            sb.AppendLine();
        }

        // Top-level player summary (non-combat singleplayer only; combat renders its own detailed view)
        bool hasBattle = state.ContainsKey("battle");
        if (!isMultiplayer && !hasBattle
            && state.TryGetValue("player", out var topPlayerObj) && topPlayerObj is Dictionary<string, object?> topPlayer)
        {
            sb.AppendLine("## Player (You)");
            string stars = topPlayer.TryGetValue("stars", out var s) && s != null ? $" | Stars: {s}" : "";
            sb.AppendLine($"**{topPlayer["character"]}** - HP: {topPlayer["hp"]}/{topPlayer["max_hp"]} | Gold: {topPlayer["gold"]}{stars}");
            sb.AppendLine();

            FormatListSection(sb, "Relics", topPlayer, "relics", r =>
            {
                string counter = r.TryGetValue("counter", out var c) && c != null ? $" [{c}]" : "";
                string used = r.GetValueOrDefault("used_up") is true ? " (used)" : "";
                string melted = r.GetValueOrDefault("melted") is true ? " (melted)" : "";
                return $"- **{r["name"]}**{counter}{used}{melted}: {r["description"]}";
            });
            FormatPotionsSection(sb, topPlayer);
            FormatMasterDeckMarkdown(sb, topPlayer);
        }

        if (state.TryGetValue("battle", out var battleObj) && battleObj is Dictionary<string, object?> battle)
        {
            var battlePlayer = state.TryGetValue("player", out var bp) && bp is Dictionary<string, object?> bpd ? bpd : null;
            FormatBattleMarkdown(sb, battle, battlePlayer);
        }

        if (state.TryGetValue("event", out var eventObj) && eventObj is Dictionary<string, object?> eventData)
        {
            FormatEventMarkdown(sb, eventData);
            if (isMultiplayer)
                FormatEventVotesMarkdown(sb, eventData);
        }

        if (state.TryGetValue("rest_site", out var restObj) && restObj is Dictionary<string, object?> restData)
        {
            FormatRestSiteMarkdown(sb, restData);
        }

        if (state.TryGetValue("shop", out var shopObj) && shopObj is Dictionary<string, object?> shopData)
        {
            FormatShopMarkdown(sb, shopData);
        }

        if (state.TryGetValue("fake_merchant", out var fmObj) && fmObj is Dictionary<string, object?> fmData)
        {
            FormatFakeMerchantMarkdown(sb, fmData);
        }

        if (state.TryGetValue("map", out var mapObj) && mapObj is Dictionary<string, object?> mapData)
        {
            FormatMapMarkdown(sb, mapData);
            if (isMultiplayer)
                FormatMapVotesMarkdown(sb, mapData);
        }

        if (state.TryGetValue("rewards", out var rewardsObj) && rewardsObj is Dictionary<string, object?> rewards)
        {
            FormatRewardsMarkdown(sb, rewards);
        }

        if (state.TryGetValue("card_reward", out var cardRewardObj) && cardRewardObj is Dictionary<string, object?> cardReward)
        {
            FormatCardRewardMarkdown(sb, cardReward);
        }

        if (state.TryGetValue("hand_select", out var handSelectObj) && handSelectObj is Dictionary<string, object?> handSelect)
        {
            FormatHandSelectMarkdown(sb, handSelect);
        }

        if (state.TryGetValue("card_select", out var cardSelectObj) && cardSelectObj is Dictionary<string, object?> cardSelect)
        {
            FormatCardSelectMarkdown(sb, cardSelect);
        }

        if (state.TryGetValue("bundle_select", out var bundleSelectObj) && bundleSelectObj is Dictionary<string, object?> bundleSelect)
        {
            FormatBundleSelectMarkdown(sb, bundleSelect);
        }

        if (state.TryGetValue("relic_select", out var relicSelectObj) && relicSelectObj is Dictionary<string, object?> relicSelect)
        {
            FormatRelicSelectMarkdown(sb, relicSelect);
        }

        if (state.TryGetValue("crystal_sphere", out var crystalSphereObj) && crystalSphereObj is Dictionary<string, object?> crystalSphere)
        {
            FormatCrystalSphereMarkdown(sb, crystalSphere);
        }

        if (state.TryGetValue("treasure", out var treasureObj) && treasureObj is Dictionary<string, object?> treasureData)
        {
            FormatTreasureMarkdown(sb, treasureData);
            if (isMultiplayer)
                FormatTreasureBidsMarkdown(sb, treasureData);
        }

        if (state.TryGetValue("overlay", out var overlayObj) && overlayObj is Dictionary<string, object?> overlayData)
        {
            sb.AppendLine($"## Overlay: {overlayData.GetValueOrDefault("screen_type")}");
            sb.AppendLine(overlayData.GetValueOrDefault("message")?.ToString());
            sb.AppendLine();
        }

        // Keyword glossary - collect all unique keyword definitions
        var glossary = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectKeywordsFromState(state, glossary);
        if (glossary.Count > 0)
        {
            sb.AppendLine("## Keyword Glossary");
            foreach (var (name, description) in glossary.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.AppendLine($"- **{name}**: {description}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void FormatMenuMarkdown(StringBuilder sb, Dictionary<string, object?> state)
    {
        var screen = state.TryGetValue("menu_screen", out var ms) ? ms?.ToString() ?? "main" : "main";
        sb.AppendLine($"## Menu: {screen}");

        if (state.TryGetValue("message", out var msg) && msg != null)
            sb.AppendLine(msg.ToString());
        sb.AppendLine();

        // MP lobby block — ready/ascension/roster — same shape on character_select and load_lobby
        if (state.TryGetValue("lobby", out var lobbyObj) &&
            lobbyObj is Dictionary<string, object?> lobby)
        {
            FormatLobbyMarkdown(sb, lobby);
        }

        // Multiplayer Join — friends list
        if (state.TryGetValue("friends", out var friendsObj) &&
            friendsObj is List<Dictionary<string, object?>> friends)
        {
            bool fastMp = state.TryGetValue("fast_mp", out var fastObj) && fastObj is true;
            bool loading = state.TryGetValue("loading", out var loadingObj) && loadingObj is true;
            bool noFriends = state.TryGetValue("no_friends", out var noObj) && noObj is true;

            sb.AppendLine($"### Friends ({friends.Count})"
                          + (fastMp ? "  _[FastMP — auto-joins localhost:33771]_" : "")
                          + (loading ? "  _[loading...]_" : ""));
            if (friends.Count == 0)
            {
                sb.AppendLine(noFriends
                    ? "_No friends with open lobbies. Use `refresh` to retry._"
                    : "_(empty)_");
            }
            else
            {
                foreach (var friend in friends)
                {
                    var idx = friend.GetValueOrDefault("index")?.ToString() ?? "?";
                    var name = friend.GetValueOrDefault("name")?.ToString() ?? "(unknown)";
                    var pid = friend.GetValueOrDefault("player_id")?.ToString() ?? "?";
                    var enabled = !friend.TryGetValue("enabled", out var en) || en is not false;
                    sb.AppendLine($"- `join_{idx}` **{name}** ({pid}){(enabled ? "" : " (disabled)")}");
                }
            }
            sb.AppendLine();
        }

        // Ascension / seed / run modifiers — the start-a-run screens (character_select
        // and custom_run). In MP the lobby block above already carries the synced
        // ascension, but the panel is the local source of truth on both screens.
        if (state.TryGetValue("ascension", out var ascObj) && ascObj is int ascensionLevel)
        {
            var max = state.GetValueOrDefault("max_ascension")?.ToString();
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Ascension:** {ascensionLevel}{(max != null ? $" / {max}" : "")}");
            sb.AppendLine();
        }

        if (state.TryGetValue("seed", out var seedObj) && seedObj != null)
        {
            sb.AppendLine($"**Seed:** `{seedObj}`");
            sb.AppendLine();
        }

        if (state.TryGetValue("modifiers", out var modifiersObj) &&
            modifiersObj is List<Dictionary<string, object?>> modifiers &&
            modifiers.Count > 0)
        {
            sb.AppendLine("### Run Modifiers");
            foreach (var modifier in modifiers)
            {
                // `key` is the selectable name — it differs from `id` only where several
                // tickboxes share one modifier id (the per-character card modifiers).
                var id = modifier.GetValueOrDefault("id")?.ToString() ?? "?";
                var key = modifier.GetValueOrDefault("key")?.ToString() ?? id;
                var title = modifier.GetValueOrDefault("title")?.ToString() ?? key;
                var description = modifier.GetValueOrDefault("description")?.ToString();
                var ticked = modifier.TryGetValue("ticked", out var tickedObj) && tickedObj is true;
                sb.AppendLine($"- [{(ticked ? "x" : " ")}] `{key}` **{title}**"
                              + (string.IsNullOrWhiteSpace(description) ? "" : $" - {description}"));
            }
            sb.AppendLine();
            sb.AppendLine("Toggle with `menu_select` using `modifier_<key>`. Some modifiers are mutually "
                          + "exclusive, so enabling one may disable another.");
            sb.AppendLine();
        }

        if (state.TryGetValue("options", out var optionsObj) && optionsObj != null)
            FormatMenuOptionsMarkdown(sb, optionsObj);

        if (state.TryGetValue("characters", out var charactersObj) &&
            charactersObj is List<Dictionary<string, object?>> characters &&
            characters.Count > 0)
        {
            sb.AppendLine("### Characters");
            foreach (var character in characters)
            {
                var id = character.GetValueOrDefault("id")?.ToString() ?? "?";
                var name = character.GetValueOrDefault("name")?.ToString() ?? id;
                var locked = character.TryGetValue("locked", out var lockedObj) && lockedObj is true ? " (LOCKED)" : "";
                var hp = character.GetValueOrDefault("hp")?.ToString() ?? "?";
                var gold = character.GetValueOrDefault("gold")?.ToString() ?? "?";
                var energy = character.GetValueOrDefault("energy")?.ToString() ?? "?";
                sb.AppendLine($"- `{id}` **{name}**{locked} - HP: {hp} | Gold: {gold} | Energy: {energy}");
            }
            sb.AppendLine();
            sb.AppendLine("Use `menu_select` with an unlocked character ID or name, then `confirm`/`embark`.");
            sb.AppendLine();
        }

        // What the screen currently has chosen — character_select / custom_run.
        if (state.TryGetValue("selected", out var selectedObj) &&
            selectedObj is Dictionary<string, object?> selected)
        {
            var character = selected.GetValueOrDefault("character")?.ToString();
            sb.AppendLine("**Selected character:** " + (string.IsNullOrEmpty(character) ? "_(none yet)_" : character));
            if (selected.GetValueOrDefault("modifiers") is List<string> selectedModifiers)
            {
                sb.AppendLine("**Enabled modifiers:** "
                              + (selectedModifiers.Count > 0 ? string.Join(", ", selectedModifiers) : "_(none)_"));
            }
            sb.AppendLine();
        }
    }

    private static void FormatLobbyMarkdown(StringBuilder sb, Dictionary<string, object?> lobby)
    {
        var type = lobby.GetValueOrDefault("type")?.ToString() ?? "?";
        var mode = lobby.GetValueOrDefault("game_mode")?.ToString() ?? "standard";
        var asc = lobby.GetValueOrDefault("ascension")?.ToString() ?? "0";
        var maxAsc = lobby.GetValueOrDefault("max_ascension")?.ToString();
        var allReady = lobby.TryGetValue("all_ready", out var ar) && ar is true;
        var aboutToBegin = lobby.TryGetValue("is_about_to_begin", out var ab) && ab is true;

        sb.Append($"### Lobby ({type}, {mode}) — Ascension: {asc}");
        if (!string.IsNullOrEmpty(maxAsc) && !string.Equals(maxAsc, "0", StringComparison.Ordinal))
            sb.Append($" / max {maxAsc}");
        sb.AppendLine();
        sb.AppendLine($"- All ready: **{allReady}**" + (aboutToBegin ? "  — about to begin" : ""));

        // MP load lobby uses act/floor/connected_player_count/expected_player_count
        if (lobby.TryGetValue("act", out var actObj))
        {
            var act = actObj?.ToString();
            var floor = lobby.GetValueOrDefault("floor")?.ToString();
            sb.AppendLine($"- Saved progress: act {act}, floor {floor}");
        }
        if (lobby.TryGetValue("connected_player_count", out var connObj))
        {
            var conn = connObj?.ToString();
            var expected = lobby.GetValueOrDefault("expected_player_count")?.ToString();
            sb.AppendLine($"- Connected: {conn}/{expected}");
        }

        if (lobby.TryGetValue("players", out var pObj) &&
            pObj is List<Dictionary<string, object?>> pl &&
            pl.Count > 0)
        {
            sb.AppendLine("- Players:");
            foreach (var p in pl)
            {
                var local = p.TryGetValue("is_local", out var l) && l is true ? " (you)" : "";
                var host = p.TryGetValue("is_host", out var h) && h is true ? " [host]" : "";
                var ready = p.TryGetValue("is_ready", out var r) && r is true ? " ✓" : "";
                var connected = p.TryGetValue("is_connected", out var c) && c is not true ? " (disconnected)" : "";
                var name = p.GetValueOrDefault("platform_name")?.ToString();
                var charName = p.GetValueOrDefault("character")?.ToString()
                               ?? p.GetValueOrDefault("character_id")?.ToString() ?? "?";
                var label = string.IsNullOrEmpty(name) ? p.GetValueOrDefault("id")?.ToString() ?? "?" : name;
                sb.AppendLine($"  - {label}{local}{host}: {charName}{ready}{connected}");
            }
        }
        sb.AppendLine();
    }

    private static void FormatGameOverMarkdown(StringBuilder sb, Dictionary<string, object?> state)
    {
        if (state.TryGetValue("game_over", out var gameOverObj) &&
            gameOverObj is Dictionary<string, object?> gameOver)
        {
            if (gameOver.TryGetValue("message", out var msg) && msg != null)
                sb.AppendLine(msg.ToString());
            if (gameOver.TryGetValue("options", out var optionsObj) && optionsObj != null)
                FormatMenuOptionsMarkdown(sb, optionsObj);
        }
    }

    private static void FormatMenuOptionsMarkdown(StringBuilder sb, object optionsObj)
    {
        if (optionsObj is List<string> names && names.Count > 0)
        {
            sb.AppendLine("### Options");
            foreach (var name in names)
                sb.AppendLine($"- `{name}`");
            sb.AppendLine();
            return;
        }

        if (optionsObj is List<Dictionary<string, object?>> options && options.Count > 0)
        {
            sb.AppendLine("### Options");
            foreach (var opt in options)
            {
                var name = opt.GetValueOrDefault("name")?.ToString() ?? "?";
                var enabled = !opt.TryGetValue("enabled", out var enabledObj) || enabledObj is not false;
                sb.AppendLine($"- `{name}`{(enabled ? "" : " (disabled)")}");
            }
            sb.AppendLine();
        }
    }

    private static void FormatBattleMarkdown(StringBuilder sb, Dictionary<string, object?> battle, Dictionary<string, object?>? player)
    {
        string allReady = battle.TryGetValue("all_players_ready", out var ar) ? $" | All Ready: {ar}" : "";
        sb.AppendLine($"**Round {battle["round"]}** | Turn: {battle["turn"]} | Play Phase: {battle["is_play_phase"]}{allReady}");
        sb.AppendLine();

        if (player != null)
            AppendPlayerBattleMarkdown(sb, player);

        AppendEnemiesMarkdown(sb, battle);
    }

    private static void AppendPlayerBattleMarkdown(StringBuilder sb, Dictionary<string, object?> player)
    {
        sb.AppendLine("## Player (You)");
        string stars = player.TryGetValue("stars", out var s) && s != null ? $" | Stars: {s}" : "";
        sb.AppendLine($"**{player["character"]}** - HP: {player["hp"]}/{player["max_hp"]} | Block: {player["block"]} | Energy: {player["energy"]}/{player["max_energy"]}{stars} | Gold: {player["gold"]}");
        sb.AppendLine();

        FormatListSection(sb, "Status", player, "status", p => $"- **{p["name"]}** ({FormatStatusAmountWithDisplay(p)}): {p["description"]}");
        FormatListSection(sb, "Relics", player, "relics", r =>
        {
            string counter = r.TryGetValue("counter", out var c) && c != null ? $" [{c}]" : "";
            string used = r.GetValueOrDefault("used_up") is true ? " (used)" : "";
            string melted = r.GetValueOrDefault("melted") is true ? " (melted)" : "";
            return $"- **{r["name"]}**{counter}{used}{melted}: {r["description"]}";
        });
        FormatPotionsSection(sb, player);

        AppendBattleHandMarkdown(sb, player);
        FormatDeckPilesMarkdown(sb, player);
        AppendBattleOrbsMarkdown(sb, player);
        FormatPetsMarkdown(sb, player);
    }

    private static void AppendBattleHandMarkdown(StringBuilder sb, Dictionary<string, object?> player)
    {
        if (!player.TryGetValue("hand", out var handObj)
            || handObj is not List<Dictionary<string, object?>> hand
            || hand.Count == 0)
            return;

        sb.AppendLine("### Hand");
        foreach (var card in hand)
        {
            string playable = card["can_play"] is true ? "✓" : "✗";
            string keywords = card.TryGetValue("keywords", out var kw) && kw is List<Dictionary<string, object?>> kwList && kwList.Count > 0
                ? $" [{string.Join(", ", kwList.Select(k => k["name"]?.ToString()))}]" : "";
            string starCost = card.TryGetValue("star_cost", out var sc) && sc != null ? $" + {sc} star" : "";
            string uid = card.TryGetValue("uid", out var u) && u != null ? $" `{u}`" : "";
            sb.AppendLine($"- [{card["index"]}]{uid} **{card["name"]}** ({card["cost"]} energy{starCost}) [{card["type"]}] {playable}{keywords} - {card["description"]} (target: {card["target_type"]})");
            FormatCardTargetPreviews(sb, card);
            AppendUpgradePreviewLine(sb, card);   // only present while the hand can be upgraded (D-13)

        }
        sb.AppendLine();
    }

    private static void AppendBattleOrbsMarkdown(StringBuilder sb, Dictionary<string, object?> player)
    {
        if (!player.TryGetValue("orbs", out var orbsObj)
            || orbsObj is not List<Dictionary<string, object?>> orbs
            || orbs.Count == 0)
            return;

        int slots = player.TryGetValue("orb_slots", out var osVal) && osVal is int sv ? sv : orbs.Count;
        int empty = player.TryGetValue("orb_empty_slots", out var esVal) && esVal is int ev ? ev : 0;
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Orbs ({orbs.Count}/{slots} slots)");
        foreach (var orb in orbs)
        {
            string desc = orb.TryGetValue("description", out var d) && d != null ? $" - {d}" : "";
            sb.AppendLine($"- **{orb["name"]}** (passive: {orb["passive_val"]}, evoke: {orb["evoke_val"]}){desc}");
        }
        if (empty > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- *{empty} empty slot(s)*");
        sb.AppendLine();
    }

    private static void AppendEnemiesMarkdown(StringBuilder sb, Dictionary<string, object?> battle)
    {
        if (!battle.TryGetValue("enemies", out var enemiesObj)
            || enemiesObj is not List<Dictionary<string, object?>> enemies
            || enemies.Count == 0)
            return;

        sb.AppendLine("## Enemies");
        foreach (var enemy in enemies)
        {
            string combatIdTag = enemy.TryGetValue("combat_id", out var cid) && cid != null
                ? $" / combat_id `{cid}`" : "";
            // A dead enemy is only listed at all when the game is keeping it in the
            // fight, so it has to be unmistakable in the heading: it cannot be
            // targeted, and it may be about to stand back up.
            bool alive = enemy.GetValueOrDefault("alive") is not bool a || a;
            string deadTag = alive ? "" : " — DEAD";
            sb.AppendLine($"### {enemy["name"]} (`{enemy["entity_id"]}`{combatIdTag}){deadTag}");
            sb.AppendLine($"HP: {enemy["hp"]}/{enemy["max_hp"]} | Block: {enemy["block"]}");
            if (!alive)
                sb.AppendLine(FormatReviveLine(enemy));

            if (enemy.TryGetValue("intents", out var intentsObj) && intentsObj is List<Dictionary<string, object?>> intents && intents.Count > 0)
            {
                sb.Append("**Intent:** ");
                sb.AppendLine(string.Join(", ", intents.Select(DescribeEnemyIntent)));
            }

            FormatListSection(sb, "Status", enemy, "status", p => $"  - **{p["name"]}** ({FormatStatusAmountWithDisplay(p)}): {p["description"]}");
            sb.AppendLine();
        }
    }

    private static string DescribeEnemyIntent(Dictionary<string, object?> intent)
    {
        string title = intent.TryGetValue("title", out var t) && t != null ? t.ToString()! : intent["type"]!.ToString()!;
        string typeTag = $" ({intent["type"]})";
        string label = intent.TryGetValue("label", out var l) && l is string ls && ls.Length > 0 ? $" {ls}" : "";
        string desc = intent.TryGetValue("description", out var d) && d is string ds && ds.Length > 0 ? $" - {ds}" : "";
        return $"{title}{typeTag}{label}{desc}";
    }

    /// <summary>
    /// The one line that says whether a corpse is staying down. "Waiting to revive" is
    /// the whole point of listing it: a Decimillipede segment is back two enemy turns
    /// after it falls, and a plan made without that spends those turns on the wrong
    /// segment.
    /// </summary>
    private static string FormatReviveLine(Dictionary<string, object?> enemy)
    {
        if (enemy.GetValueOrDefault("revive") is not Dictionary<string, object?> revive)
            return "**Down, and staying down** (no revive scheduled) - cannot be targeted.";

        object? turns = revive.GetValueOrDefault("in_turns");
        object? hp = revive.GetValueOrDefault("hp");
        string at = hp != null ? $" at {hp} HP" : "";
        string by = revive.GetValueOrDefault("power_name") is object p ? $" ({p})" : "";
        string when = turns != null ? $"in {turns} enemy turn(s)" : "on an unknown turn";
        return $"**⚠ WAITING TO REVIVE** {when}{at}{by} - cannot be targeted until it is back.";
    }

    /// <summary>
    /// Resolved damage per living enemy, as the game would apply it right now
    /// (Strength, Pen Nib, Vulnerable, Weak all folded in). The card's own text bakes
    /// in an inconsistent subset of those, so this is the line to compute lethal from.
    /// </summary>
    private static void FormatCardTargetPreviews(StringBuilder sb, Dictionary<string, object?> card)
    {
        if (!card.TryGetValue("vs_targets", out var vtObj)
            || vtObj is not List<Dictionary<string, object?>> targets
            || targets.Count == 0)
            return;

        var parts = targets.Select(t =>
        {
            string label = t.GetValueOrDefault("target")?.ToString() ?? "?";
            if (!t.TryGetValue("damage", out var dmg) || dmg == null)
                return $"{label}: -";
            string hits = t.TryGetValue("hits", out var h) && h != null
                ? $" x{h} = {t.GetValueOrDefault("total_damage")}" : "";
            // A damage cap turns a big hit into a wasted card; say so on the same line.
            string cap = "";
            if (t.TryGetValue("damage_cap", out var capObj) && capObj is Dictionary<string, object?> c)
            {
                cap = c.TryGetValue("effective_damage", out var eff) && eff != null
                    ? $" ⚠ actually {eff} ({c.GetValueOrDefault("capped_by")})"
                    : $" ⚠ capped by {c.GetValueOrDefault("capped_by")}";
            }
            return $"{label}: {dmg}{hits}{cap}";
        });
        sb.AppendLine($"  - resolved damage: {string.Join(", ", parts)}");
    }

    /// <summary>
    /// The master deck, out of combat only (in a fight the piles already list every card).
    /// Identical copies are collapsed into one line with a count - a 30-card deck is mostly
    /// Strikes and Defends, and printing each one separately buried the cards that matter.
    /// Keywords are left off here on purpose: the glossary section already explains every
    /// keyword that appears anywhere in the state, including these cards.
    /// </summary>
    private static void FormatMasterDeckMarkdown(StringBuilder sb, Dictionary<string, object?> player)
    {
        if (!player.TryGetValue("deck", out var deckObj)
            || deckObj is not List<Dictionary<string, object?>> deck)
            return;

        object? countObj = player.GetValueOrDefault("deck_count") ?? deck.Count;
        sb.AppendLine($"### Deck ({countObj} cards)");

        // Group key has to include the upgrade state: an upgraded Strike is a different card
        // to reason about, and merging it with its base version would misreport the deck.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<(Dictionary<string, object?> Card, string Key)>();
        foreach (var card in deck)
        {
            string key = $"{card.GetValueOrDefault("id")}|{card.GetValueOrDefault("is_upgraded")}";
            if (seen.TryGetValue(key, out int count))
            {
                seen[key] = count + 1;
                continue;
            }
            seen[key] = 1;
            order.Add((card, key));
        }

        foreach (var (card, key) in order)
        {
            string starCost = card.TryGetValue("star_cost", out var sc) && sc != null ? $" + {sc} star" : "";
            string copies = seen[key] > 1 ? $" ×{seen[key]}" : "";
            sb.AppendLine($"- [{card["index"]}] **{card["name"]}**{copies} ({card["cost"]} energy{starCost}) [{card["type"]}] - {card["description"]}");
        }
        sb.AppendLine();
    }

    private static void FormatDeckPilesMarkdown(StringBuilder sb, Dictionary<string, object?> player)
    {
        sb.AppendLine("### Deck Information");
        sb.AppendLine();

        FormatPileMarkdown(sb, player, "deck", "deck_count", "Run Deck");
        FormatPileMarkdown(sb, player, "draw_pile", "draw_pile_count", "Draw Pile", " top of pile first");
        FormatPileMarkdown(sb, player, "discard_pile", "discard_pile_count", "Discard Pile");
        FormatPileMarkdown(sb, player, "exhaust_pile", "exhaust_pile_count", "Exhaust Pile");
        FormatPileMarkdown(sb, player, "play_pile", "play_pile_count", "Play Pile");
    }

    private static void FormatPetsMarkdown(StringBuilder sb, Dictionary<string, object?> player)
    {
        if (!player.TryGetValue("pets", out var petsObj)
            || petsObj is not List<Dictionary<string, object?>> pets
            || pets.Count == 0)
            return;

        sb.AppendLine("### Pets");
        foreach (var pet in pets)
        {
            bool alive = pet.TryGetValue("alive", out var a) && a is true;
            string status = alive
                ? $"HP: {pet["hp"]}/{pet["max_hp"]} | Block: {pet["block"]}"
                : "DEAD";
            sb.AppendLine($"- **{pet["name"]}** (`{pet["id"]}`) - {status}");
            if (alive && pet.TryGetValue("status", out var statusObj)
                && statusObj is List<Dictionary<string, object?>> statusList && statusList.Count > 0)
            {
                sb.AppendLine("  **Status**");
                foreach (var p in statusList)
                    sb.AppendLine($"  - **{p["name"]}** ({FormatStatusAmountWithDisplay(p)}): {p["description"]}");
            }
        }
        sb.AppendLine();
    }

    private static void FormatPileMarkdown(StringBuilder sb, Dictionary<string, object?> player,
        string pileKey, string countKey, string label, string suffix = "")
    {
        sb.AppendLine($"#### {label} ({player[countKey]} cards{(suffix.Length > 0 ? "," : "")}{suffix})");
        if (player.TryGetValue(pileKey, out var pileObj) && pileObj is List<Dictionary<string, object?>> pile && pile.Count > 0)
        {
            foreach (var card in pile)
                sb.AppendLine($"- {FormatCardDetails(card, energyLabel: false)}");
        }
        else
            sb.AppendLine("- *(empty)*");
        sb.AppendLine();
    }

    private static string FormatUpgradeMarker(Dictionary<string, object?> card)
        => card.TryGetValue("is_upgraded", out var upgraded) && upgraded is true ? "+" : "";

    private static string FormatCardDetails(Dictionary<string, object?> card, bool includeTarget = false, bool energyLabel = true)
    {
        string upgraded = FormatUpgradeMarker(card);
        string cost = card.TryGetValue("cost", out var c) && c != null ? c.ToString()! : "?";
        string energy = energyLabel ? " energy" : "";
        string starCost = card.TryGetValue("star_cost", out var sc) && sc != null ? $" + {sc} star" : "";
        string type = card.TryGetValue("type", out var t) && t != null ? $" [{t}]" : "";
        string rarity = card.TryGetValue("rarity", out var r) && r != null ? $" {r}" : "";
        string keywords = FormatKeywordNames(card);
        string description = card.TryGetValue("description", out var d) && d != null ? $" - {d}" : "";
        string target = includeTarget && card.TryGetValue("target_type", out var targetObj) && targetObj != null
            ? $" (target: {targetObj})"
            : "";
        return $"**{card.GetValueOrDefault("name")}**{upgraded} ({cost}{energy}{starCost}){type}{rarity}{keywords}{description}{target}";
    }

    private static string FormatRelicDetails(Dictionary<string, object?> relic, string nameSuffix = "")
    {
        string rarity = relic.TryGetValue("rarity", out var r) && r != null ? $" {r}" : "";
        string keywords = FormatKeywordNames(relic);
        string description = relic.TryGetValue("description", out var d) && d != null ? $" - {d}" : "";
        return $"**{relic.GetValueOrDefault("name")}**{nameSuffix}{rarity}{keywords}{description}";
    }

    private static string FormatKeywordNames(Dictionary<string, object?> item)
    {
        if (!item.TryGetValue("keywords", out var kw) || kw is not List<Dictionary<string, object?>> keywords || keywords.Count == 0)
            return "";

        var names = keywords
            .Select(keyword => keyword.TryGetValue("name", out var name) ? name?.ToString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return names.Count > 0 ? $" [{string.Join(", ", names)}]" : "";
    }

    private static void FormatEventMarkdown(StringBuilder sb, Dictionary<string, object?> evt)
    {
        string name = evt.TryGetValue("event_name", out var n) && n != null ? n.ToString()! : "Unknown Event";
        bool isAncient = evt.TryGetValue("is_ancient", out var a) && a is true;
        sb.AppendLine($"## {(isAncient ? "Ancient" : "Event")}: {name}");
        sb.AppendLine();

        bool inDialogue = evt.TryGetValue("in_dialogue", out var d) && d is true;
        if (inDialogue)
        {
            sb.AppendLine("*Ancient dialogue in progress - use `advance_dialogue` to continue.*");
            sb.AppendLine();
            return;
        }

        if (evt.TryGetValue("options", out var optObj) && optObj is List<Dictionary<string, object?>> options && options.Count > 0)
        {
            sb.AppendLine("### Options");
            foreach (var opt in options)
            {
                bool locked = opt["is_locked"] is true;
                bool proceed = opt["is_proceed"] is true;
                bool chosen = opt["was_chosen"] is true;

                string tag = "";
                if (locked)
                    tag = " (LOCKED)";
                else if (chosen)
                    tag = " (CHOSEN)";
                else if (proceed)
                    tag = " (PROCEED)";
                string relic = opt.TryGetValue("relic_name", out var rn) && rn != null ? $" [Relic: {rn}]" : "";
                sb.AppendLine($"- [{opt["index"]}] **{opt["title"]}**{tag}{relic} - {opt["description"]}");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("No options available.");
            sb.AppendLine();
        }
    }

    private static void FormatRestSiteMarkdown(StringBuilder sb, Dictionary<string, object?> restSite)
    {
        if (restSite.TryGetValue("options", out var optObj) && optObj is List<Dictionary<string, object?>> options && options.Count > 0)
        {
            sb.AppendLine("## Rest Site Options");
            foreach (var opt in options)
            {
                string enabled = opt["is_enabled"] is true ? "" : " (DISABLED)";
                // Only worth printing when it is false - a model option with no button is
                // the mismatch that used to make choose_rest_option press the wrong thing.
                string noButton = opt.TryGetValue("has_button", out var hb) && hb is false
                    ? " (no button on screen)"
                    : "";
                sb.AppendLine($"- [{opt["index"]}] `{opt["id"]}` **{opt["name"]}**{enabled}{noButton} - {opt["description"]}");
            }
            sb.AppendLine();
        }

        bool canProceed = restSite.TryGetValue("can_proceed", out var cp) && cp is true;
        sb.AppendLine($"**Can proceed:** {(canProceed ? "Yes" : "No")}");
        sb.AppendLine();
    }

    private static void FormatShopMarkdown(StringBuilder sb, Dictionary<string, object?> shop)
    {
        AppendShopErrorNote(sb, shop);
        AppendSellablePotionsMarkdown(sb, shop);
        AppendShopItemsMarkdown(sb, shop);

        bool canProceed = shop.TryGetValue("can_proceed", out var cp) && cp is true;
        sb.AppendLine($"**Can proceed:** {(canProceed ? "Yes" : "No")}");
        sb.AppendLine();
    }

    private static void AppendShopErrorNote(StringBuilder sb, Dictionary<string, object?> shop)
    {
        if (!shop.TryGetValue("error", out var err) || err == null)
            return;

        sb.AppendLine("## Shop");
        sb.AppendLine($"**Note:** {err}");
        sb.AppendLine();
    }

    private static void AppendSellablePotionsMarkdown(StringBuilder sb, Dictionary<string, object?> shop)
    {
        if (!shop.TryGetValue("sellable_potions", out var spObj)
            || spObj is not List<Dictionary<string, object?>> sellable
            || sellable.Count == 0)
            return;

        sb.AppendLine("### Potions this merchant will buy");
        foreach (var p in sellable)
            sb.AppendLine($"- [slot {p["slot"]}] **{p["potion_name"]}** — `sell_potion slot {p["slot"]}`");
        sb.AppendLine();
    }

    private static void AppendShopItemsMarkdown(StringBuilder sb, Dictionary<string, object?> shop)
    {
        if (!shop.TryGetValue("items", out var itemsObj) || itemsObj is not List<Dictionary<string, object?>> items)
            return;

        sb.AppendLine("## Shop Inventory");
        string? lastCategory = null;
        foreach (var item in items)
        {
            string category = item["category"]?.ToString() ?? "";
            if (!string.Equals(category, lastCategory, StringComparison.Ordinal))
            {
                string header = category switch { "card" => "Cards", "relic" => "Relics", "potion" => "Potions", "card_removal" => "Services", _ => category };
                sb.AppendLine($"### {header}");
                lastCategory = category;
            }
            sb.AppendLine(DescribeShopItem(item, category));
        }
        sb.AppendLine();
    }

    /// <summary>One shop row: the item description, then its price and stock flags.</summary>
    private static string DescribeShopItem(Dictionary<string, object?> item, string category)
    {
        bool stocked = item["is_stocked"] is true;
        bool afford = item["can_afford"] is true;
        string priceTag = stocked ? $"{item["price"]}g" : "SOLD";
        string affordTag = stocked && !afford ? " (can't afford)" : "";
        string saleTag = item.TryGetValue("on_sale", out var os) && os is true ? " **SALE**" : "";
        string desc = DescribeShopItemBody(item, category);
        return $"- [{item["index"]}] {desc} - {priceTag}{saleTag}{affordTag}";
    }

    private static string DescribeShopItemBody(Dictionary<string, object?> item, string category)
    {
        string cardCost = item.TryGetValue("card_cost", out var cc) && cc != null ? cc.ToString()! : "";
        string cardStarCost = item.TryGetValue("card_star_cost", out var csc) && csc != null ? $" + {csc} star" : "";
        string cardEnergy = !string.IsNullOrEmpty(cardCost) ? $" ({cardCost} energy{cardStarCost})" : "";
        string cardUpgrade = DescribeShopCardUpgrade(item, category);
        string itemKeywords = FormatKeywordNames(item);
        return category switch
        {
            "card" => $"**{item.GetValueOrDefault("card_name")}**{(item.GetValueOrDefault("card_is_upgraded") is true ? "+" : "")}{cardEnergy} [{item.GetValueOrDefault("card_type")}] {item.GetValueOrDefault("card_rarity")}{itemKeywords} - {item.GetValueOrDefault("card_description")}{cardUpgrade}",
            "relic" => $"**{item.GetValueOrDefault("relic_name")}** {item.GetValueOrDefault("relic_rarity")}{itemKeywords} - {item.GetValueOrDefault("relic_description")}",
            "potion" => $"**{item.GetValueOrDefault("potion_name")}** {item.GetValueOrDefault("potion_rarity")} ({item.GetValueOrDefault("potion_usage")}, target: {item.GetValueOrDefault("potion_target_type")}){itemKeywords} - {item.GetValueOrDefault("potion_description")}",
            "card_removal" => "**Remove a card** from your deck",
            _ => "Unknown item"
        };
    }

    /// <summary>
    /// The upgraded preview for a shop card, when it differs from the card as it is. Empty
    /// for every other category.
    /// </summary>
    private static string DescribeShopCardUpgrade(Dictionary<string, object?> item, string category)
    {
        if (!string.Equals(category, "card", StringComparison.Ordinal)
            || !item.TryGetValue("card_upgrade_preview_description", out var upgradeDesc)
            || upgradeDesc == null
            || Equals(upgradeDesc, item.GetValueOrDefault("card_description")))
            return "";

        string upgradeCost = item.TryGetValue("card_upgrade_preview_cost", out var uc) && uc != null ? uc.ToString()! : "";
        string upgradeStar = item.TryGetValue("card_upgrade_preview_star_cost", out var usc) && usc != null ? $" + {usc} star" : "";
        string upgradeEnergy = !string.IsNullOrEmpty(upgradeCost) ? $"({upgradeCost} energy{upgradeStar})" : "";
        return !string.IsNullOrEmpty(upgradeEnergy)
            ? $" | Upgrade: {upgradeEnergy} {upgradeDesc}"
            : $" | Upgrade: {upgradeDesc}";
    }

    private static void FormatFakeMerchantMarkdown(StringBuilder sb, Dictionary<string, object?> fm)
    {
        string name = fm.TryGetValue("event_name", out var n) && n != null ? n.ToString()! : "Fake Merchant";
        bool startedFight = fm.TryGetValue("started_fight", out var sf) && sf is true;

        sb.AppendLine($"## Event: {name}");
        sb.AppendLine();

        if (startedFight)
        {
            sb.AppendLine("*The fake merchant has been defeated. Use `proceed` to open the map.*");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("*This is a fake merchant selling dubious relics. You can browse and buy, or throw a Foul Potion (use_potion) to start a fight.*");
        sb.AppendLine();

        // Reuse shop formatting for the nested shop object
        if (fm.TryGetValue("shop", out var shopObj) && shopObj is Dictionary<string, object?> shopData)
            FormatShopMarkdown(sb, shopData);
    }

    private static void FormatMapMarkdown(StringBuilder sb, Dictionary<string, object?> map)
    {
        // Act boss. Bosses are rolled per act and are known as soon as the map exists,
        // so this is the single most load-bearing line for planning card picks - it used
        // to be JSON-only, which made it invisible to the markdown workflow.
        FormatMapBossMarkdown(sb, map);

        AppendMapEncountersSeen(sb, map);
        AppendMapPathTaken(sb, map);
        AppendMapMarkedNodes(sb, map);

        // Build node lookup for path traversal
        var nodeLookup = BuildMapNodeLookup(map);
        AppendMapNextOptions(sb, map, nodeLookup);

        FormatMapGraphMarkdown(sb, map);
    }

    /// <summary>
    /// What this act has already served. NOT what comes next - the game does not show that
    /// before the room is entered, and neither do we (see BuildEncountersSeen).
    /// </summary>
    private static void AppendMapEncountersSeen(StringBuilder sb, Dictionary<string, object?> map)
    {
        if (!map.TryGetValue("encounters_seen", out var esObj)
            || esObj is not Dictionary<string, object?> seen
            || seen.Count == 0)
            return;

        var parts = new List<string>();
        foreach (var key in new[] { "monster", "elite", "event" })
        {
            if (seen.GetValueOrDefault(key) is not List<Dictionary<string, object?>> rooms || rooms.Count == 0)
                continue;
            parts.Add($"{key}: " + string.Join(", ", rooms.Select(r =>
            {
                string name = r.TryGetValue("name", out var n) && n != null ? n.ToString()! : r.GetValueOrDefault("id")?.ToString() ?? "?";
                string floor = r.TryGetValue("floor", out var f) && f != null ? $" (f{f})" : "";
                return $"{name}{floor}";
            })));
        }
        if (parts.Count == 0)
            return;

        string cycle = seen.GetValueOrDefault("elite_cycle") is int c && c > 0
            ? $" - elite pool cycled x{c} (a fought elite can return)" : "";
        sb.AppendLine("**Fought this act:** " + string.Join(" | ", parts) + cycle);
        sb.AppendLine();
    }

    private static void AppendMapPathTaken(StringBuilder sb, Dictionary<string, object?> map)
    {
        if (!map.TryGetValue("visited", out var visitedObj)
            || visitedObj is not List<Dictionary<string, object?>> visited
            || visited.Count == 0)
            return;

        sb.AppendLine("## Path Taken");
        var parts = visited.Select((v, i) => $"{i + 1}. {v["type"]} ({v["col"]},{v["row"]})");
        sb.AppendLine(string.Join(" → ", parts) + " ← current");
        sb.AppendLine();
    }

    private static void AppendMapMarkedNodes(StringBuilder sb, Dictionary<string, object?> map)
    {
        if (!map.TryGetValue("marked_nodes", out var markedObj)
            || markedObj is not List<Dictionary<string, object?>> markedNodes
            || markedNodes.Count == 0)
            return;

        var labels = markedNodes.Select(n =>
        {
            var by = n.TryGetValue("marked_by", out var b) && b is List<string> bl ? string.Join("/", bl) : "?";
            return $"({n["col"]},{n["row"]}) by {by}";
        });
        sb.AppendLine($"**Marked rooms:** {string.Join(", ", labels)}");
        sb.AppendLine();
    }

    private static Dictionary<string, Dictionary<string, object?>> BuildMapNodeLookup(
        Dictionary<string, object?> map)
    {
        var nodeLookup = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        if (map.TryGetValue("nodes", out var nodesObj) && nodesObj is List<Dictionary<string, object?>> nodes)
        {
            foreach (var node in nodes)
                nodeLookup[$"{node["col"]},{node["row"]}"] = node;
        }
        return nodeLookup;
    }

    /// <summary>The travelable next nodes, each with the tree of paths it leads to.</summary>
    private static void AppendMapNextOptions(
        StringBuilder sb,
        Dictionary<string, object?> map,
        Dictionary<string, Dictionary<string, object?>> nodeLookup)
    {
        if (!map.TryGetValue("next_options", out var optObj)
            || optObj is not List<Dictionary<string, object?>> options
            || options.Count == 0)
        {
            sb.AppendLine("## Map");
            sb.AppendLine("No travelable nodes available.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("## Choose Next Node");
        foreach (var opt in options)
        {
            string marked = opt.TryGetValue("marked_by", out var mb) && mb is List<string> mbl && mbl.Count > 0
                ? $" [marked by {string.Join(", ", mbl)}]" : "";
            sb.AppendLine($"- [{opt["index"]}] **{opt["type"]}** ({opt["col"]},{opt["row"]}){marked}");
            string tree = BuildFuturePathTree(opt, nodeLookup);
            if (tree.Length > 0)
                sb.AppendLine($"  Future paths: {tree}");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// The act's map as an adjacency list, row by row.
    /// "Future paths" above flattens each level and loses which node leads where, so
    /// planning a route meant reading the save file's saved_map by hand. The same
    /// coordinates that choose_map_node accepts are printed here.
    /// </summary>
    private static void FormatMapGraphMarkdown(StringBuilder sb, Dictionary<string, object?> map)
    {
        if (!map.TryGetValue("nodes", out var nodesObj)
            || nodesObj is not List<Dictionary<string, object?>> nodes
            || nodes.Count == 0)
            return;

        sb.AppendLine("## Map Graph");
        sb.AppendLine("`(col,row) Type -> children`, one line per row of the act.");

        var byRow = new SortedDictionary<int, List<Dictionary<string, object?>>>();
        foreach (var node in nodes)
        {
            int row = ToInt(node.GetValueOrDefault("row"));
            if (!byRow.TryGetValue(row, out var list))
                byRow[row] = list = new List<Dictionary<string, object?>>();
            list.Add(node);
        }

        foreach (var (row, rowNodes) in byRow)
        {
            var parts = rowNodes
                .OrderBy(n => ToInt(n.GetValueOrDefault("col")))
                .Select(n =>
                {
                    var childKeys = GetChildKeys(n).OrderBy(k => k, StringComparer.Ordinal).ToList();
                    string children = childKeys.Count > 0 ? string.Join("/", childKeys) : "-";
                    string marked = n.TryGetValue("marked_by", out var mb) && mb is List<string> mbl && mbl.Count > 0
                        ? "*" : "";
                    return $"({n["col"]},{row}) {n["type"]}{marked} -> {children}";
                });
            sb.AppendLine(CultureInfo.InvariantCulture, $"- row {row}: {string.Join(" | ", parts)}");
        }
        sb.AppendLine();
    }

    private static int ToInt(object? value)
    {
        if (value is int i)
            return i;
        return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static void FormatMapBossMarkdown(StringBuilder sb, Dictionary<string, object?> map)
    {
        List<Dictionary<string, object?>>? bosses = null;
        if (map.TryGetValue("bosses", out var bs) && bs is List<Dictionary<string, object?>> bl && bl.Count > 0)
            bosses = bl;
        else if (map.TryGetValue("boss", out var b) && b is Dictionary<string, object?> bd)
            bosses = new List<Dictionary<string, object?>> { bd };
        if (bosses == null)
            return;

        var labels = new List<string>();
        foreach (var boss in bosses)
        {
            string name = "Unknown";
            if (boss.TryGetValue("name", out var n) && n != null)
                name = n.ToString()!;
            else if (boss.TryGetValue("id", out var i) && i != null)
                name = i.ToString()!;
            labels.Add($"{name} ({boss["col"]},{boss["row"]})");
        }
        if (labels.Count == 0)
            return;

        sb.AppendLine(labels.Count > 1
            ? $"**Boss candidates:** {string.Join(" / ", labels)}"
            : $"**Boss:** {labels[0]}");
        sb.AppendLine();
    }

    /// <summary>
    /// BFS from a node through its children to build a future path tree string.
    /// Format: -> Type (col,row) or Type (col,row) -> Type (col,row) or ...
    /// </summary>
    private static string BuildFuturePathTree(Dictionary<string, object?> startNode, Dictionary<string, Dictionary<string, object?>> nodeLookup)
    {
        // Look up the canonical node (with children) from the full node list
        string startKey = $"{startNode["col"]},{startNode["row"]}";
        var canonicalStart = nodeLookup.TryGetValue(startKey, out var cs) ? cs : startNode;
        var currentKeys = GetChildKeys(canonicalStart);
        var sb = new StringBuilder();

        while (currentKeys.Count > 0)
        {
            // Collect node info for this level
            var levelNodes = new List<(string type, int col, int row)>();
            var nextKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var key in currentKeys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (nodeLookup.TryGetValue(key, out var node))
                {
                    string type = node["type"]?.ToString() ?? "Unknown";
                    int col = node["col"] is int c ? c : Convert.ToInt32(node["col"], CultureInfo.InvariantCulture);
                    int row = node["row"] is int r ? r : Convert.ToInt32(node["row"], CultureInfo.InvariantCulture);
                    levelNodes.Add((type, col, row));

                    foreach (var childKey in GetChildKeys(node))
                        nextKeys.Add(childKey);
                }
            }

            if (levelNodes.Count == 0) break;

            sb.Append("-> ");
            sb.Append(string.Join(" or ", levelNodes.Select(n => $"{n.type} ({n.col},{n.row})")));
            sb.Append(' ');

            currentKeys = nextKeys;
        }

        return sb.ToString().TrimEnd();
    }

    private static HashSet<string> GetChildKeys(Dictionary<string, object?> node)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (node.TryGetValue("children", out var childrenObj) && childrenObj is System.Collections.IList childList)
        {
            foreach (var child in childList)
            {
                if (child is System.Collections.IList coords && coords.Count >= 2)
                    keys.Add($"{coords[0]},{coords[1]}");
            }
        }
        return keys;
    }

    private static void FormatRelicSelectMarkdown(StringBuilder sb, Dictionary<string, object?> relicSelect)
    {
        sb.AppendLine("## Relic Selection");
        if (relicSelect.TryGetValue("prompt", out var p) && p != null)
            sb.AppendLine($"*{p}*");
        sb.AppendLine();

        if (relicSelect.TryGetValue("relics", out var relicsObj) && relicsObj is List<Dictionary<string, object?>> relics)
        {
            foreach (var relic in relics)
                sb.AppendLine($"- [{relic["index"]}] {FormatRelicDetails(relic)}");
            sb.AppendLine();
        }

        bool canSkip = relicSelect.TryGetValue("can_skip", out var cs) && cs is true;
        sb.AppendLine($"Use `select_relic(index)` to choose. Can skip: {(canSkip ? "Yes" : "No")}");
        sb.AppendLine();
    }

    private static void FormatHandSelectMarkdown(StringBuilder sb, Dictionary<string, object?> handSelect)
    {
        sb.AppendLine("## In-Combat Card Selection");

        if (handSelect.TryGetValue("prompt", out var promptObj) && promptObj != null)
            sb.AppendLine($"*{promptObj}*");
        sb.AppendLine();

        string mode = handSelect.TryGetValue("mode", out var m) ? m?.ToString() ?? "simple_select" : "simple_select";
        if (string.Equals(mode, "upgrade_select", StringComparison.Ordinal))
            sb.AppendLine("**Mode:** Upgrade selection");
        sb.AppendLine();

        if (handSelect.TryGetValue("cards", out var cardsObj) && cardsObj is List<Dictionary<string, object?>> cards && cards.Count > 0)
        {
            sb.AppendLine("### Selectable Cards");
            foreach (var card in cards)
            {
                string starCost = card.TryGetValue("star_cost", out var sc) && sc != null ? $" + {sc} star" : "";
                sb.AppendLine($"- [{card["index"]}] **{card["name"]}** ({card["cost"]} energy{starCost}) [{card["type"]}] - {card["description"]}");
                AppendUpgradePreviewLine(sb, card);
            }
            sb.AppendLine();
        }

        if (handSelect.TryGetValue("selected_cards", out var selObj) && selObj is List<Dictionary<string, object?>> selected && selected.Count > 0)
        {
            sb.AppendLine("### Already Selected");
            foreach (var card in selected)
                sb.AppendLine($"- {FormatCardDetails(card)}");
            sb.AppendLine();
        }

        bool canConfirm = handSelect.TryGetValue("can_confirm", out var cc) && cc is true;
        sb.AppendLine($"Use `combat_select_card(card_index)` to select. Can confirm: {(canConfirm ? "Yes - use `combat_confirm_selection`" : "No - select more cards")}");
        sb.AppendLine();
    }

    private static void FormatCardSelectMarkdown(StringBuilder sb, Dictionary<string, object?> cardSelect)
    {
        string screenType = cardSelect.TryGetValue("screen_type", out var st) ? st?.ToString() ?? "select" : "select";
        string screenLabel = screenType switch
        {
            "transform" => "Transform",
            "upgrade" => "Upgrade",
            "enchant" => "Enchant",
            "select" => "Select",
            "simple_select" => "Select",
            _ => screenType
        };
        sb.AppendLine($"## Card Selection: {screenLabel}");

        if (cardSelect.TryGetValue("prompt", out var promptObj) && promptObj != null)
        {
            sb.AppendLine($"*{promptObj}*");
        }

        AppendCardSelectCounts(sb, cardSelect);
        AppendCardSelectCards(sb, cardSelect);
        AppendCardSelectFooter(sb, cardSelect);
    }

    /// <summary>
    /// How many are picked out of how many the screen wants. Without this line a caller on a
    /// multi-card screen cannot tell whether confirming will do anything.
    /// </summary>
    private static void AppendCardSelectCounts(StringBuilder sb, Dictionary<string, object?> cardSelect)
    {
        if (cardSelect.TryGetValue("selected_count", out var selCountObj) && selCountObj is int selCount)
        {
            string wanted = "";
            if (cardSelect.TryGetValue("required_count", out var reqObj) && reqObj is int req)
                wanted = $" of {req} required";
            else if (cardSelect.TryGetValue("min_select", out var minObj) && minObj is int min
                     && cardSelect.TryGetValue("max_select", out var maxObj) && maxObj is int max)
                wanted = $" ({min}-{max} accepted)";
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Selected: {selCount}{wanted}**");
        }
        sb.AppendLine();
    }

    private static void AppendCardSelectCards(StringBuilder sb, Dictionary<string, object?> cardSelect)
    {
        if (!cardSelect.TryGetValue("cards", out var cardsObj) || cardsObj is not List<Dictionary<string, object?>> cards)
            return;

        sb.AppendLine("### Cards");
        foreach (var card in cards)
        {
            string starCost = card.TryGetValue("star_cost", out var sc) && sc != null ? $" + {sc} star" : "";
            string selected = card.TryGetValue("selected", out var selObj) && selObj is true ? "[x] " : "";
            sb.AppendLine($"- {selected}[{card["index"]}] **{card["name"]}** ({card["cost"]} energy{starCost}) [{card["type"]}] {card["rarity"]} - {card["description"]}");
            AppendUpgradePreviewLine(sb, card);
        }
        sb.AppendLine();
    }

    private static void AppendCardSelectFooter(StringBuilder sb, Dictionary<string, object?> cardSelect)
    {
        bool preview = cardSelect.TryGetValue("preview_showing", out var pv) && pv is true;
        bool canConfirm = cardSelect.TryGetValue("can_confirm", out var cc) && cc is true;
        bool canCancel = cardSelect.TryGetValue("can_cancel", out var cn) && cn is true;

        if (preview)
        {
            sb.AppendLine("**Preview is showing** - the next action should be `confirm_selection` to finish, or `cancel_selection` to go back. Do not select the same card again unless you intend to deselect it.");
            AppendPreviewCards(sb, cardSelect);
        }
        else
        {
            sb.AppendLine($"**Select cards** using `select_card(index)`. Can confirm: {(canConfirm ? "Yes" : "No")} | Can cancel: {(canCancel ? "Yes" : "No")}");
        }
        sb.AppendLine();
    }

    private static void AppendPreviewCards(StringBuilder sb, Dictionary<string, object?> cardSelect)
    {
        if (!cardSelect.TryGetValue("preview_cards", out var previewCardsObj)
            || previewCardsObj is not List<Dictionary<string, object?>> previewCards
            || previewCards.Count == 0)
            return;

        sb.AppendLine("### Preview Cards");
        foreach (var card in previewCards)
        {
            string starCost = card.TryGetValue("star_cost", out var sc) && sc != null ? $" + {sc} star" : "";
            sb.AppendLine($"- **{card["name"]}** ({card["cost"]} energy{starCost}) [{card["type"]}] {card["rarity"]} - {card["description"]}");
        }
        sb.AppendLine();
    }

    private static void FormatBundleSelectMarkdown(StringBuilder sb, Dictionary<string, object?> bundleSelect)
    {
        sb.AppendLine("## Bundle Selection");

        if (bundleSelect.TryGetValue("prompt", out var promptObj) && promptObj != null)
            sb.AppendLine($"*{promptObj}*");
        sb.AppendLine();

        if (bundleSelect.TryGetValue("bundles", out var bundlesObj) && bundlesObj is List<Dictionary<string, object?>> bundles && bundles.Count > 0)
        {
            sb.AppendLine("### Bundles");
            foreach (var bundle in bundles)
            {
                sb.AppendLine($"- [{bundle["index"]}] Bundle with {bundle["card_count"]} card(s)");
                if (bundle.TryGetValue("cards", out var cardsObj) && cardsObj is List<Dictionary<string, object?>> cards)
                {
                    foreach (var card in cards)
                        sb.AppendLine($"  {FormatCardDetails(card)}");
                }
            }
            sb.AppendLine();
        }

        bool preview = bundleSelect.TryGetValue("preview_showing", out var pv) && pv is true;
        bool canConfirm = bundleSelect.TryGetValue("can_confirm", out var cc) && cc is true;
        bool canCancel = bundleSelect.TryGetValue("can_cancel", out var cn) && cn is true;

        if (preview)
        {
            sb.AppendLine("**Preview is showing** - use `confirm_bundle_selection()` to confirm or `cancel_bundle_selection()` to go back.");
            if (bundleSelect.TryGetValue("preview_cards", out var previewCardsObj) && previewCardsObj is List<Dictionary<string, object?>> previewCards && previewCards.Count > 0)
            {
                sb.AppendLine("### Preview Cards");
                foreach (var card in previewCards)
                    sb.AppendLine($"- {FormatCardDetails(card)}");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine($"Use `select_bundle(index)` to open a bundle preview. Can confirm: {(canConfirm ? "Yes" : "No")} | Can cancel: {(canCancel ? "Yes" : "No")}");
            sb.AppendLine();
        }
    }

    private static void FormatCrystalSphereMarkdown(StringBuilder sb, Dictionary<string, object?> crystalSphere)
    {
        sb.AppendLine("## Crystal Sphere");

        if (crystalSphere.TryGetValue("instructions_title", out var titleObj) && titleObj != null)
            sb.AppendLine($"**{titleObj}**");
        if (crystalSphere.TryGetValue("instructions_description", out var descObj) && descObj != null)
            sb.AppendLine(descObj.ToString());
        sb.AppendLine();

        string tool = crystalSphere.TryGetValue("tool", out var toolObj) ? toolObj?.ToString() ?? "none" : "none";
        string divinationsLeft = crystalSphere.TryGetValue("divinations_left_text", out var dlObj) && dlObj != null
            ? dlObj.ToString()!
            : "Unknown";
        sb.AppendLine($"**Tool:** {tool} | **Divinations:** {divinationsLeft}");
        sb.AppendLine();

        if (crystalSphere.TryGetValue("clickable_cells", out var cellsObj) && cellsObj is List<Dictionary<string, object?>> clickableCells && clickableCells.Count > 0)
        {
            sb.AppendLine("### Clickable Cells");
            foreach (var cell in clickableCells)
                sb.AppendLine($"- ({cell["x"]}, {cell["y"]})");
            sb.AppendLine();
        }

        if (crystalSphere.TryGetValue("revealed_items", out var itemsObj) && itemsObj is List<Dictionary<string, object?>> revealedItems && revealedItems.Count > 0)
        {
            sb.AppendLine("### Revealed Items");
            foreach (var item in revealedItems)
                sb.AppendLine($"- **{item["item_type"]}** at ({item["x"]}, {item["y"]}) size {item["width"]}x{item["height"]}");
            sb.AppendLine();
        }

        bool canProceed = crystalSphere.TryGetValue("can_proceed", out var cp) && cp is true;
        if (canProceed)
        {
            sb.AppendLine("Use `crystal_sphere_proceed()` to continue.");
        }
        else
        {
            sb.AppendLine("Use `crystal_sphere_set_tool(tool)` with `big` or `small`, then `crystal_sphere_click_cell(x, y)`.");
        }
        sb.AppendLine();
    }

    private static void FormatTreasureMarkdown(StringBuilder sb, Dictionary<string, object?> treasure)
    {
        if (treasure.TryGetValue("relics", out var relicsObj) && relicsObj is List<Dictionary<string, object?>> relics && relics.Count > 0)
        {
            sb.AppendLine("## Treasure Relics");
            foreach (var relic in relics)
                sb.AppendLine($"- [{relic["index"]}] {FormatRelicDetails(relic)}");
            sb.AppendLine();
            sb.AppendLine("Use `treasure_claim_relic(relic_index)` to claim a relic.");
        }
        else
        {
            sb.AppendLine("Chest is opening...");
        }
        sb.AppendLine();

        bool canProceed = treasure.TryGetValue("can_proceed", out var cp) && cp is true;
        if (canProceed)
            sb.AppendLine("**Can proceed:** Yes");
        sb.AppendLine();
    }

    private static string FormatStatusAmount(object? amount)
    {
        if (amount is int i && i == -1) return "indefinite";
        return amount?.ToString() ?? "0";
    }

    /// <summary>
    /// "(2)" normally, "(2, shown as 0)" when the on-screen number is a different
    /// quantity than the stack count (e.g. Tender shows cards played this turn).
    /// </summary>
    private static string FormatStatusAmountWithDisplay(Dictionary<string, object?> power)
    {
        string amount = FormatStatusAmount(power.GetValueOrDefault("amount"));
        if (power.TryGetValue("display_amount", out var shown) && shown != null)
            return $"{amount}, shown as {FormatStatusAmount(shown)}";
        return amount;
    }

    private static void FormatListSection(StringBuilder sb, string title, Dictionary<string, object?> parent, string key,
        Func<Dictionary<string, object?>, string> formatter)
    {
        if (parent.TryGetValue(key, out var listObj) && listObj is List<Dictionary<string, object?>> list && list.Count > 0)
        {
            sb.AppendLine($"### {title}");
            foreach (var item in list)
                sb.AppendLine(formatter(item));
            sb.AppendLine();
        }
    }

    private static void FormatPotionsSection(StringBuilder sb, Dictionary<string, object?> playerDict)
    {
        if (!playerDict.TryGetValue("potions", out var potionsObj)
            || potionsObj is not List<Dictionary<string, object?>> potions
            || potions.Count == 0)
            return;
        int max = playerDict.TryGetValue("max_potion_slots", out var ms) && ms is int msi ? msi : potions.Count;
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Potions ({potions.Count}/{max})");
        foreach (var p in potions)
            sb.AppendLine($"- [{p["slot"]}] **{p["name"]}**: {p["description"]}");
        sb.AppendLine();
    }

    private static void FormatMapVotesMarkdown(StringBuilder sb, Dictionary<string, object?> mapData)
    {
        if (!mapData.TryGetValue("votes", out var votesObj) || votesObj is not List<Dictionary<string, object?>> votes || votes.Count == 0)
            return;

        sb.AppendLine("## Map Votes");
        foreach (var vote in votes)
        {
            string youTag = vote["is_local"] is true ? " (YOU)" : "";
            if (vote["voted"] is true)
                sb.AppendLine($"- **{vote["player"]}**{youTag}: voted for ({vote["vote_col"]},{vote["vote_row"]})");
            else
                sb.AppendLine($"- **{vote["player"]}**{youTag}: *waiting...*");
        }
        bool allVoted = mapData.TryGetValue("all_voted", out var av) && av is true;
        if (allVoted)
            sb.AppendLine("**All players have voted!**");
        sb.AppendLine();
    }

    private static void FormatEventVotesMarkdown(StringBuilder sb, Dictionary<string, object?> eventData)
    {
        bool isShared = eventData.TryGetValue("is_shared", out var sh) && sh is true;
        if (!isShared) return;

        if (!eventData.TryGetValue("votes", out var votesObj) || votesObj is not List<Dictionary<string, object?>> votes || votes.Count == 0)
            return;

        sb.AppendLine("## Event Votes (Shared Event)");
        foreach (var vote in votes)
        {
            string youTag = vote["is_local"] is true ? " (YOU)" : "";
            if (vote["voted"] is true)
                sb.AppendLine($"- **{vote["player"]}**{youTag}: voted for option {vote["vote_option"]}");
            else
                sb.AppendLine($"- **{vote["player"]}**{youTag}: *waiting...*");
        }
        bool allVoted = eventData.TryGetValue("all_voted", out var av) && av is true;
        if (allVoted)
            sb.AppendLine("**All players have voted!**");
        sb.AppendLine();
    }

    private static void FormatTreasureBidsMarkdown(StringBuilder sb, Dictionary<string, object?> treasureData)
    {
        if (treasureData.TryGetValue("is_bidding_phase", out var bp) && bp is not true)
            return;

        if (!treasureData.TryGetValue("bids", out var bidsObj) || bidsObj is not List<Dictionary<string, object?>> bids || bids.Count == 0)
            return;

        sb.AppendLine("## Treasure Bids");
        foreach (var bid in bids)
        {
            string youTag = bid["is_local"] is true ? " (YOU)" : "";
            if (bid["voted"] is true)
                sb.AppendLine($"- **{bid["player"]}**{youTag}: bid on relic #{bid["vote_relic_index"]}");
            else
                sb.AppendLine($"- **{bid["player"]}**{youTag}: *waiting...*");
        }
        bool allBid = treasureData.TryGetValue("all_bid", out var ab) && ab is true;
        if (allBid)
            sb.AppendLine("**All players have bid!**");
        sb.AppendLine();
    }

    private static void CollectKeywordsFromState(object? obj, Dictionary<string, string> glossary)
    {
        if (obj is Dictionary<string, object?> dict)
        {
            if (dict.TryGetValue("keywords", out var kw) && kw is List<Dictionary<string, object?>> keywords)
            {
                foreach (var keyword in keywords)
                {
                    string? name = keyword.GetValueOrDefault("name")?.ToString();
                    string? desc = keyword.GetValueOrDefault("description")?.ToString();
                    if (name == null)
                        continue;
                    // A keyword with no text used to be dropped from the glossary
                    // entirely, so it showed up in a card's keyword list with nothing
                    // to explain it and no sign that the game itself has no text for it.
                    glossary.TryAdd(name, desc ?? "(no description in game data)");
                }
            }
            foreach (var (key, value) in dict)
            {
                if (!string.Equals(key, "keywords", StringComparison.Ordinal))
                    CollectKeywordsFromState(value, glossary);
            }
        }
        else if (obj is List<Dictionary<string, object?>> list)
        {
            foreach (var item in list)
                CollectKeywordsFromState(item, glossary);
        }
    }
}
