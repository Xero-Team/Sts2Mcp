using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, JsonElement> data)
    {
        var guard = TryBeginRunAction(action, out var runState, out var player);
        if (guard != null)
            return guard;

        return action switch
        {
            "open_pause_menu" or "pause" => ExecuteOpenPauseMenu(runState),
            "play_card" => ExecutePlayCard(player, data),
            "use_potion" => ExecuteUsePotion(player, data),
            "sell_potion" => ExecuteSellPotion(player, data),
            "discard_potion" => ExecuteDiscardPotion(player, data),
            "end_turn" => ExecuteEndTurn(player),
            "choose_map_node" => ExecuteChooseMapNode(data),
            "map_draw" => ExecuteMapDraw(data),
            "map_clear_drawings" => ExecuteMapClearDrawings(),
            "choose_event_option" => ExecuteChooseEventOption(data),
            "advance_dialogue" => ExecuteAdvanceDialogue(),
            "choose_rest_option" => ExecuteChooseRestOption(player, data),
            "shop_open" => ExecuteShopOpen(player),
            "shop_purchase" => ExecuteShopPurchase(player, data),
            "claim_reward" => ExecuteClaimReward(data),
            "select_card_reward" => ExecuteSelectCardReward(data),
            "skip_card_reward" => ExecuteSkipCardReward(),
            "select_card_reward_alternative" => ExecuteSelectCardRewardAlternative(data),
            "proceed" => ExecuteProceed(),
            "select_card" => ExecuteSelectCard(data),
            "confirm_selection" => ExecuteConfirmSelection(),
            "cancel_selection" => ExecuteCancelSelection(),
            "select_bundle" => ExecuteSelectBundle(data),
            "confirm_bundle_selection" => ExecuteConfirmBundleSelection(),
            "cancel_bundle_selection" => ExecuteCancelBundleSelection(),
            "combat_select_card" => ExecuteCombatSelectCard(data),
            "combat_confirm_selection" => ExecuteCombatConfirmSelection(),
            "select_relic" => ExecuteSelectRelic(data),
            "skip_relic_selection" => ExecuteSkipRelicSelection(),
            "claim_treasure_relic" => ExecuteClaimTreasureRelic(data),
            "crystal_sphere_set_tool" => ExecuteCrystalSphereSetTool(data),
            "crystal_sphere_click_cell" => ExecuteCrystalSphereClickCell(data),
            "crystal_sphere_proceed" => ExecuteCrystalSphereProceed(),
            "resume" => ExecuteResumeFromPauseMenu(),
            "give_up" => ExecuteGiveUp(),
            "debug_start_encounter" => ExecuteDebugStartEncounter(data),
            "debug_start_event" => ExecuteDebugStartEvent(data),
            "debug_start_shop" => ExecuteDebugStartShop(),
            "debug_force_play_phase" => ExecuteDebugForcePlayPhase(player),
            "debug_add_card" => ExecuteDebugAddCard(player, data),
            "debug_set_energy" => ExecuteDebugSetEnergy(player, data),
            "debug_set_stars" => ExecuteDebugSetStars(player, data),
            "debug_add_power" => ExecuteDebugAddPower(player, data),
            "debug_enchant_card" => ExecuteDebugEnchantCard(player, data),
            "debug_enter_next_act" => ExecuteDebugEnterNextAct(),
            "debug_gain_max_hp" => ExecuteDebugGainMaxHp(player, data),
            "debug_upgrade_deck" => ExecuteDebugUpgradeDeck(player),
            "return_to_main_menu" => ExecuteReturnToMainMenu(),
            _ => Error($"Unknown action: {action}", "unknown_action")
        };
    }

    /// <summary>
    /// The preconditions every in-run action shares, or the error that stops the action.
    /// </summary>
    private static Dictionary<string, object?>? TryBeginRunAction(
        string action,
        out RunState runState,
        out Player player)
    {
        runState = null!;
        player = null!;
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress", "run_not_in_progress");

        runState = RunManager.Instance.DebugOnlyGetState()!;
        var found = LocalContext.GetMe(runState);
        if (found == null)
            return Error("Could not find local player", "local_player_unavailable");
        player = found;

        var tree = (Godot.Engine.GetMainLoop()) as SceneTree;
        if (tree?.Root != null && IsAnyFtueVisible(tree.Root))
            return Error("Blocking popup active. Use menu_select with one of the advertised popup options before gameplay actions.", "blocking_popup_active");

        return RunSubmenuBlockingError(tree, action);
    }

    // The in-run pause menu is opened by the richer ExecuteOpenPauseMenu(IRunState) below
    // (top-bar button, or the run's submenu stack); these two are the shortcut actions for
    // leaving it, and they share the one button map in ExecutePauseMenuOption so the action
    // names and the menu_select option names can never disagree.
    private static Dictionary<string, object?> ExecuteResumeFromPauseMenu()
    {
        var tree = (Godot.Engine.GetMainLoop()) as SceneTree;
        var pauseMenu = tree?.Root != null ? FindFirst<NPauseMenu>(tree.Root) : null;
        if (pauseMenu == null || !IsNodeVisible(pauseMenu))
            return Error("Pause menu is not open. Use open_pause_menu first.");

        return ExecutePauseMenuOption(pauseMenu, "resume");
    }

    private static Dictionary<string, object?> ExecuteGiveUp()
    {
        var tree = (Godot.Engine.GetMainLoop()) as SceneTree;
        var pauseMenu = tree?.Root != null ? FindFirst<NPauseMenu>(tree.Root) : null;
        if (pauseMenu == null || !IsNodeVisible(pauseMenu))
            return Error("Pause menu is not open. Use open_pause_menu first.");

        return ExecutePauseMenuOption(pauseMenu, "give_up");
    }

    /// <summary>
    /// Reads an integer parameter, accepting any of the given names.
    /// Actions historically disagreed on whether the card index was called
    /// "card_index" or "index", which produced a run of silent no-ops and
    /// "Missing 'index'" errors for callers that guessed the other spelling.
    /// Every name listed here is accepted; the first one is what the error names.
    /// </summary>
    private static bool TryGetIntParam(
        Dictionary<string, JsonElement> data,
        out int value,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!data.TryGetValue(name, out var elem))
                continue;
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out value))
                return true;
            if (elem.ValueKind == JsonValueKind.String
                && int.TryParse(elem.GetString(), CultureInfo.InvariantCulture, out value))
                return true;
        }
        value = 0;
        return false;
    }

    private static Dictionary<string, object?> MissingIntParam(string description, params string[] names)
        => Error($"Missing '{names[0]}' ({description}). Accepted names: {string.Join(", ", names)}");

    /// <summary>Reads a string parameter under any of the given names.</summary>
    private static string? TryReadStringParam(Dictionary<string, JsonElement> data, params string[] names)
    {
        foreach (var name in names)
        {
            if (!data.TryGetValue(name, out var elem))
                continue;
            if (elem.ValueKind != JsonValueKind.String)
                continue;
            var value = elem.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    /// <summary>
    /// Reads the 'target' parameter as a string, accepting a bare JSON number so that
    /// a combat_id can be passed as either 3 or "3". GetString() throws on a number,
    /// which used to surface as a 500 rather than a usable error.
    /// Returns null when no usable target was supplied.
    /// </summary>
    private static string? TryReadTargetId(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("target", out var elem))
            return null;

        string? raw = elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.ToString(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static Dictionary<string, object?> ExecutePlayCard(Player player, Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");
        if (!IsCombatPlayPhase())
            return Error("Not in play phase - cannot act during enemy turn");
        if (CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled");
        if (!player.Creature.IsAlive)
            return Error("Player creature is dead - cannot play cards");

        var combatState = player.Creature.CombatState;
        if (combatState == null)
            return Error("No combat state");

        // Get card by index in hand
        var hand = player.PlayerCombatState?.Hand;
        if (hand == null)
            return Error("No hand available");

        if (!TryResolveHandCard(data, hand, combatState, out var card, out var cardError))
            return cardError!;

        if (!card.CanPlay(out var reason, out _))
            return Error($"Card '{card.Title}' cannot be played: {reason}");

        var target = ResolvePlayCardTarget(player, combatState, card, data);
        if (target.Failure != null)
            return target.Failure;

        // Play the card via the action queue (same path as the game UI)
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            Recorder.CombatRecorder.TagApiAction(new PlayCardAction(card, target.Target)));

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Playing '{card.Title}'" + (target.Target != null ? $" targeting {DescribeCreature(target.Target)}" : "")
        };
    }

    /// <summary>
    /// Finds the card the request names. Prefer the uid: card_index shifts as soon as a card
    /// leaves the hand, so a multi-card plan built from one state read goes wrong after the
    /// first play.
    /// </summary>
    private static bool TryResolveHandCard(
        Dictionary<string, JsonElement> data,
        CardPile hand,
        ICombatState combatState,
        out CardModel card,
        out Dictionary<string, object?>? error)
    {
        error = null;
        card = null!;
        string? cardUid = TryReadStringParam(data, "card_uid", "uid");
        if (cardUid != null)
        {
            RefreshCardUidRegistry(combatState);
            card = hand.Cards.FirstOrDefault(c => string.Equals(GetStableCardUid(c), cardUid, StringComparison.Ordinal))!;
            if (card == null)
            {
                error = Error(
                    $"Card '{cardUid}' is not in hand. Hand: "
                    + string.Join(", ", hand.Cards.Select(GetStableCardUid)));
                return false;
            }
            return true;
        }

        if (!TryGetIntParam(data, out int cardIndex, "card_index", "index"))
        {
            error = Error("Missing card selector. Provide 'card_uid' (preferred, stable across plays) or 'card_index' (position in hand).");
            return false;
        }
        if (cardIndex < 0 || cardIndex >= hand.Cards.Count)
        {
            error = Error($"card_index {cardIndex} out of range (hand has {hand.Cards.Count} cards)");
            return false;
        }
        card = hand.Cards[cardIndex];
        return true;
    }

    /// <summary>A resolved target for a play or a potion, or the error that refused it.</summary>
    private sealed record PlayTargetResolution(
        Creature? Target,
        Dictionary<string, object?>? Failure);

    private static PlayTargetResolution ResolvePlayCardTarget(
        Player player, ICombatState combatState, CardModel card, Dictionary<string, JsonElement> data)
        => card.TargetType switch
        {
            TargetType.AnyEnemy => ResolveEnemyTarget(combatState, card, data),
            TargetType.AnyAlly => ResolveAllyTarget(player, combatState, card, data),
            // No card currently uses AnyPlayer (only potions do), and
            // CardModel.IsValidTarget rejects ANY non-null target for it -
            // null is the only value that lets the play execute.
            _ => new PlayTargetResolution(null, null),
        };

    private static PlayTargetResolution ResolveEnemyTarget(
        ICombatState combatState, CardModel card, Dictionary<string, JsonElement> data)
    {
        string? targetId = TryReadTargetId(data);
        if (targetId == null)
        {
            var alive = combatState.Enemies.Where(e => e.IsAlive).ToList();
            if (alive.Count != 1)
            {
                return new PlayTargetResolution(null, Error(
                    $"Card '{card.Title}' requires a target. Provide 'target' with an entity_id "
                    + $"or combat_id ({alive.Count} enemies alive)."));
            }
            return new PlayTargetResolution(alive[0], null);
        }

        var target = ResolveTarget(combatState, targetId);
        return target == null
            ? new PlayTargetResolution(null, Error($"Target '{targetId}' not found among alive enemies"))
            : new PlayTargetResolution(target, null);
    }

    private static PlayTargetResolution ResolveAllyTarget(
        Player player, ICombatState combatState, CardModel card, Dictionary<string, JsonElement> data)
    {
        // AnyAlly = another living player, never self: the game UI excludes
        // self from targeting and CardCmd's auto-play picks among allies
        // != owner. (CardModel.IsValidTarget only checks Side, so a self
        // target would slip through mechanically - but it is a play the
        // game never allows a human to make.) A null target is rejected by
        // IsValidTarget and silently cancels the play.
        // Optional 'target' picks the teammate (players[].entity_id, e.g.
        // "player_1", or a numeric combat_id); default = lowest-HP living
        // teammate. All current AnyAlly cards are multiplayer-only.
        Creature? target = null;
        if (data.TryGetValue("target", out var allyElem))
        {
            target = ResolvePlayerTarget(player, combatState, allyElem.GetString() ?? "");
            if (target == player.Creature)
            {
                return new PlayTargetResolution(null, Error(
                    $"Card '{card.Title}' targets an ally and cannot target yourself. Valid targets: {ListAllyTargetIds(player)}"));
            }
            if (target is { IsAlive: false })
                target = null;
            // Unresolvable ids (older clients send enemy ids here) fall
            // through to the default teammate instead of failing the play.
        }
        target ??= player.RunState.Players
            .Where(p => p != player && p.Creature.IsAlive)
            .OrderBy(p => p.Creature.CurrentHp)
            .FirstOrDefault()?.Creature;
        return target == null
            ? new PlayTargetResolution(null, Error($"Card '{card.Title}' requires a living teammate to target and there is none"))
            : new PlayTargetResolution(target, null);
    }

    private static Dictionary<string, object?> ExecuteEndTurn(Player player)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");
        if (!IsCombatPlayPhase())
            return Error("Not in play phase - cannot act during enemy turn");
        if (CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled (turn may already be ending)");

        // Match the game's own CanTurnBeEnded guard (NEndTurnButton.cs:114-123)
        var hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand != null && (hand.InCardPlay || hand.CurrentMode != NPlayerHand.Mode.Play))
            return Error("Cannot end turn while a card is being played or hand is in selection mode");

        // The same input the end-turn button sends (NEndTurnButton.CallReleaseLogic): an
        // EndPlayerTurnAction through the action queue, which then runs PlayerCmd.EndTurn itself.
        // Calling PlayerCmd.EndTurn from here instead ended the turn outside the queue, so the
        // game's combat replay never saw the player end it and could not be replayed.
        if (CombatManager.Instance.IsPlayerReadyToEndTurn(player))
            return Error("Turn is already ending");
        int turnNumber = player.PlayerCombatState?.TurnNumber ?? -1;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            Recorder.CombatRecorder.TagApiAction(new EndPlayerTurnAction(player, turnNumber)));

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Ending turn"
        };
    }

    /// <summary>
    /// "slot" is the fixed slot number on the potion belt, not an index into the
    /// non-empty potions. After using the potion in slot 0, the survivor stays in
    /// slot 1 — asking for slot 0 then has to say so, and say where the potions are.
    /// </summary>
    private static string DescribeEmptyPotionSlot(Player player, int slot)
    {
        var occupied = new List<string>();
        for (int i = 0; i < player.PotionSlots.Count; i++)
        {
            var p = player.GetPotionAtSlotIndex(i);
            if (p != null)
                occupied.Add($"{i}={SafeGetText(() => p.Title) ?? "?"}");
        }

        return occupied.Count == 0
            ? $"No potion in slot {slot} (no potions held)"
            : $"No potion in slot {slot}. Occupied slots: {string.Join(", ", occupied)}. "
              + "'slot' is the belt slot from state player.potions[].slot, not a position in the list.";
    }

    private static Dictionary<string, object?> ExecuteUsePotion(Player player, Dictionary<string, JsonElement> data)
    {
        if (!TryGetIntParam(data, out int slot, "slot", "potion_index", "index"))
            return MissingIntParam("potion slot index", "slot", "potion_index", "index");
        if (slot < 0 || slot >= player.PotionSlots.Count)
            return Error($"Potion slot {slot} out of range (player has {player.PotionSlots.Count} slots)");

        var potion = player.GetPotionAtSlotIndex(slot);
        if (potion == null)
            return Error(DescribeEmptyPotionSlot(player, slot));

        var unusable = ValidatePotionUsable(player, potion);
        if (unusable != null)
            return unusable;

        var targetResolution = ResolvePotionTarget(player, player.Creature.CombatState, potion, data);
        if (targetResolution.Failure != null)
            return targetResolution.Failure;
        var target = targetResolution.Target;

        if (target != null && !potion.IsValidTarget(target))
            return Error($"Potion '{SafeGetText(() => potion.Title)}' cannot target {SafeGetText(() => target.Monster?.Title) ?? "that creature"}");

        using (Recorder.CombatRecorder.ExpectApiAction(typeof(UsePotionAction)))
            potion.EnqueueManualUse(target);

        string targetMsg = potion.TargetType switch
        {
            TargetType.AnyEnemy => $" targeting {SafeGetText(() => target?.Monster?.Title) ?? "enemy"}",
            TargetType.Self => " on self",
            TargetType.AnyPlayer or TargetType.AnyAlly =>
                target == player.Creature ? " on self" : $" on {DescribeCreature(target)}",
            _ => ""
        };

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Using potion '{SafeGetText(() => potion.Title)}' from slot {slot}{targetMsg}"
        };
    }

    /// <summary>
    /// The reasons a potion cannot be used right now, in the order the game checks them. Some
    /// potions are only usable against the merchant (Foul Potion sells for gold instead of
    /// exploding), and that target is only findable while the inventory panel is closed, so
    /// that case closes it and re-checks rather than refusing.
    /// </summary>
    private static Dictionary<string, object?>? ValidatePotionUsable(Player player, PotionModel potion)
    {
        if (potion.IsQueued)
            return Error($"Potion '{SafeGetText(() => potion.Title)}' is already queued for use");
        if (potion.Owner.Creature.IsDead)
            return Error("Cannot use potion - player creature is dead");
        if (!potion.PassesCustomUsabilityCheck
            && (!TryCloseMerchantInventory(player) || !potion.PassesCustomUsabilityCheck))
        {
            return Error(
                $"Potion '{SafeGetText(() => potion.Title)}' cannot be used right now"
                + (IsInMerchantRoom(player)
                    ? " (in a shop: this potion has no merchant interaction, or the merchant is not available)"
                    : ""));
        }

        bool inCombat = CombatManager.Instance.IsInProgress;
        if (potion.Usage == PotionUsage.CombatOnly)
        {
            if (!inCombat)
                return Error($"Potion '{SafeGetText(() => potion.Title)}' can only be used in combat");
            if (!IsCombatPlayPhase())
                return Error("Cannot use potions outside of play phase");
        }
        else if (potion.Usage == PotionUsage.Automatic)
        {
            return Error($"Potion '{SafeGetText(() => potion.Title)}' is automatic and cannot be manually used");
        }

        if (inCombat && CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled");
        return null;
    }

    private static PlayTargetResolution ResolvePotionTarget(
        Player player, ICombatState? combatState, PotionModel potion, Dictionary<string, JsonElement> data)
        => potion.TargetType switch
        {
            TargetType.AnyEnemy => ResolvePotionEnemyTarget(combatState, potion, data),
            // PotionModel.IsValidTarget requires target == owner for Self
            // (unlike cards, where Self passes no target).
            TargetType.Self => new PlayTargetResolution(player.Creature, null),
            TargetType.AnyPlayer => ResolvePotionPlayerTarget(player, combatState, data),
            TargetType.AnyAlly => ResolvePotionAllyTarget(player, combatState, potion, data),
            _ => new PlayTargetResolution(null, null),
        };

    private static PlayTargetResolution ResolvePotionEnemyTarget(
        ICombatState? combatState, PotionModel potion, Dictionary<string, JsonElement> data)
    {
        if (combatState == null)
            return new PlayTargetResolution(null, Error("No combat state for target resolution"));

        string? targetId = TryReadTargetId(data);
        if (targetId == null)
        {
            // With a single living enemy there is nothing to disambiguate, so
            // pick it rather than refusing. Otherwise say so loudly: this used
            // to fall through to target = null and the potion was consumed
            // against nothing while the call reported success.
            var alive = combatState.Enemies.Where(e => e.IsAlive).ToList();
            if (alive.Count != 1)
            {
                return new PlayTargetResolution(null, Error(
                    $"Potion '{SafeGetText(() => potion.Title)}' requires a target enemy. "
                    + $"Provide 'target' with an entity_id or combat_id ({alive.Count} enemies alive)."));
            }
            return new PlayTargetResolution(alive[0], null);
        }

        var target = ResolveTarget(combatState, targetId);
        return target == null
            ? new PlayTargetResolution(null, Error($"Target '{targetId}' not found among alive enemies"))
            : new PlayTargetResolution(target, null);
    }

    private static PlayTargetResolution ResolvePotionPlayerTarget(
        Player player, ICombatState? combatState, Dictionary<string, JsonElement> data)
    {
        // Any living player, self included (PotionModel.IsValidTarget).
        // Optional 'target' throws the potion to a teammate
        // (players[].entity_id, e.g. "player_1", or a numeric
        // combat_id); default = self, the pre-existing behavior.
        if (!data.TryGetValue("target", out var playerElem))
            return new PlayTargetResolution(player.Creature, null);

        string playerId = playerElem.GetString() ?? "";
        var target = ResolvePlayerTarget(player, combatState, playerId);
        return target is not { IsAlive: true }
            ? new PlayTargetResolution(null, Error($"Target '{playerId}' not found among living players"))
            : new PlayTargetResolution(target, null);
    }

    private static PlayTargetResolution ResolvePotionAllyTarget(
        Player player, ICombatState? combatState, PotionModel potion, Dictionary<string, JsonElement> data)
    {
        // Another living player, never self (PotionModel.IsValidTarget
        // rejects self and the use would be silently cancelled). No
        // such potion exists today; handled for completeness.
        if (!data.TryGetValue("target", out var allyElem))
        {
            var teammate = player.RunState.Players
                .Where(p => p != player && p.Creature.IsAlive)
                .OrderBy(p => p.Creature.CurrentHp)
                .FirstOrDefault()?.Creature;
            return teammate == null
                ? new PlayTargetResolution(null, Error($"Potion '{SafeGetText(() => potion.Title)}' requires a living teammate to target and there is none"))
                : new PlayTargetResolution(teammate, null);
        }

        string allyId = allyElem.GetString() ?? "";
        var target = ResolvePlayerTarget(player, combatState, allyId);
        if (target == player.Creature)
        {
            return new PlayTargetResolution(null, Error(
                $"Potion '{SafeGetText(() => potion.Title)}' targets an ally and cannot target yourself. Valid targets: {ListAllyTargetIds(player)}"));
        }
        return target is not { IsAlive: true }
            ? new PlayTargetResolution(null, Error($"Target '{allyId}' not found among living teammates"))
            : new PlayTargetResolution(target, null);
    }

    /// <summary>
    /// Sells a potion to the merchant by using it on them — the Foul Potion's second
    /// mode, worth 100 gold each. The API had no way to do this at all: use_potion in a
    /// shop returned ok and changed nothing, and the shop state exposed no sell action,
    /// so three Foul Potions over a run were 300 gold silently left on the floor.
    /// This is use_potion with the shop precondition checked up front.
    /// </summary>
    private static Dictionary<string, object?> ExecuteSellPotion(Player player, Dictionary<string, JsonElement> data)
    {
        if (!IsInMerchantRoom(player))
            return Error("Not in a shop - potions can only be sold to a merchant");

        if (!TryGetIntParam(data, out int slot, "slot", "potion_index", "index"))
            return MissingIntParam("potion slot index", "slot", "potion_index", "index");
        if (slot < 0 || slot >= player.PotionSlots.Count)
            return Error($"Potion slot {slot} out of range (player has {player.PotionSlots.Count} slots)");

        var potion = player.GetPotionAtSlotIndex(slot);
        if (potion == null)
            return Error(DescribeEmptyPotionSlot(player, slot));

        if (!CanSellPotionToMerchant(player, potion))
            return Error(
                $"Potion '{SafeGetText(() => potion.Title)}' cannot be sold to the merchant. "
                + "Only potions with a merchant interaction (e.g. Foul Potion) can be.");

        return ExecuteUsePotion(player, data);
    }

    private static bool IsInMerchantRoom(Player player)
    {
        return TryRead<bool?>(() =>
        {
            if (player.RunState.CurrentRoom is MerchantRoom)
                return true;
            return player.RunState.CurrentRoom is EventRoom eventRoom
                   && eventRoom.CanonicalEvent is FakeMerchant;
        }) ?? false;
    }

    /// <summary>
    /// True when this potion's usability depends on the merchant being present and it is.
    /// Determined by asking the potion itself, after making sure the inventory panel is
    /// closed, so no potion list needs to be hard-coded here.
    /// </summary>
    internal static bool CanSellPotionToMerchant(Player player, PotionModel potion)
    {
        return TryRead<bool?>(() =>
        {
            if (!IsInMerchantRoom(player))
                return false;
            if (potion.Usage == PotionUsage.CombatOnly || potion.Usage == PotionUsage.Automatic)
                return false;
            if (potion.PassesCustomUsabilityCheck)
                return HasMerchantInteraction(player, potion);
            TryCloseMerchantInventory(player);
            return potion.PassesCustomUsabilityCheck && HasMerchantInteraction(player, potion);
        }) ?? false;
    }

    /// <summary>
    /// Whether the potion advertises a merchant target at all. Potions that do expose a
    /// static GetFoulPotionMerchantTarget-style lookup; anything without one is an
    /// ordinary out-of-combat potion, not a sale.
    /// </summary>
    private static bool HasMerchantInteraction(Player player, PotionModel potion)
    {
        return TryRead<bool?>(() =>
        {
            var method = potion.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name.Contains("MerchantTarget", StringComparison.Ordinal));
            if (method == null)
                return false;

            var result = method.Invoke(null, new object?[] { player.RunState.CurrentRoom });
            if (result == null)
                return false;

            var item1 = result.GetType().GetField("Item1")?.GetValue(result);
            return item1 != null;
        }) ?? false;
    }

    /// <summary>Closes the merchant inventory panel if it is open. Returns true if a shop was found.</summary>
    private static bool TryCloseMerchantInventory(Player player)
    {
        // Closing a panel that is already gone is the normal case here.
        return TryReadQuiet<bool?>(() =>
        {
            if (NMerchantRoom.Instance is { } merchRoom && player.RunState.CurrentRoom is MerchantRoom)
            {
                if (merchRoom.Inventory?.IsOpen == true)
                {
                    var backBtn = FindFirst<NBackButton>(merchRoom);
                    if (backBtn is { IsEnabled: true })
                        backBtn.ForceClick();
                }
                return true;
            }

            if (NEventRoom.Instance is { } evtRoom)
            {
                var fmNode = FindFirst<NFakeMerchant>(evtRoom);
                if (fmNode != null)
                {
                    var fmInventory = FindFirst<NMerchantInventory>(fmNode);
                    if (fmInventory is { IsOpen: true })
                    {
                        var backBtn = FindFirst<NBackButton>(fmNode);
                        if (backBtn is { IsEnabled: true })
                            backBtn.ForceClick();
                    }
                    return true;
                }
            }

            return false;
        }) ?? false;
    }

    private static Dictionary<string, object?> ExecuteDiscardPotion(Player player, Dictionary<string, JsonElement> data)
    {
        if (!TryGetIntParam(data, out int slot, "slot", "potion_index", "index"))
            return MissingIntParam("potion slot index", "slot", "potion_index", "index");
        if (slot < 0 || slot >= player.PotionSlots.Count)
            return Error($"Potion slot {slot} out of range (player has {player.PotionSlots.Count} slots)");

        var potion = player.GetPotionAtSlotIndex(slot);
        if (potion == null)
            return Error(DescribeEmptyPotionSlot(player, slot));
        if (potion.IsQueued)
            return Error($"Potion '{SafeGetText(() => potion.Title)}' is already queued and cannot be discarded");

        string potionName = SafeGetText(() => potion.Title) ?? "unknown";
        // The potion popup's discard button (NPotionPopup.OnDiscardButtonPressed) goes through a
        // DiscardPotionGameAction; so does this, so a discard mid-combat is in the game's replay.
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            Recorder.CombatRecorder.TagApiAction(
                new DiscardPotionGameAction(player, (uint)slot, CombatManager.Instance.IsInProgress)));

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Discarded potion '{potionName}' from slot {slot}"
        };
    }

    private static Dictionary<string, object?> ExecuteChooseEventOption(Dictionary<string, JsonElement> data)
    {
        var uiRoom = NEventRoom.Instance;
        if (uiRoom == null)
            return Error("Event room is not open");

        if (!TryGetIntParam(data, out int index, "index", "option_index"))
            return MissingIntParam("event option index", "index", "option_index");

        var buttons = FindAll<NEventOptionButton>(uiRoom)
            .Where(button => button.Visible && button.IsVisibleInTree())
            .ToList();

        if (buttons.Count == 0)
            return Error("No event options available");
        if (index < 0 || index >= buttons.Count)
            return Error($"Event option index {index} out of range ({buttons.Count} options)");

        var button = buttons[index];
        var option = button.Option;
        if (option.IsLocked)
            return Error($"Event option {index} is locked");

        // NClickableControl.ForceClick is an unconditional OnRelease + signal
        // emit - it never consults IsEnabled - so this action has to apply the
        // game's own gate itself. An option that starts a fight runs its body
        // across several awaits (fade out, combat state sync, combat asset
        // preload), and during all of it the buttons are still in the tree, so
        // a client polling at 10 Hz re-picks the same option and fires the body
        // a second time, which wedges the run. Both flags below are set the
        // instant a choice begins executing: NEventRoom.BeforeOptionChosen
        // calls DisableOptionButtons(), and EventOption.Chosen() assigns
        // WasChosen synchronously before awaiting OnChosen.
        if (IsRunTransitionInFlight())
            return Error("A room transition is already resolving - wait for the destination room to finish loading");
        if (option.WasChosen)
            return Error($"Event option {index} was already chosen");
        if (!button.IsEnabled)
            return Error($"Event option {index} is not clickable right now - a choice is already resolving");

        string title = SafeGetText(() => option.Title) ?? "option";

        button.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Choosing event option: {title}"
        };
    }

    private static Dictionary<string, object?> ExecuteAdvanceDialogue()
    {
        var uiRoom = NEventRoom.Instance;
        if (uiRoom == null)
            return Error("Event room is not open");

        var ancientLayout = FindFirst<NAncientEventLayout>(uiRoom);
        if (ancientLayout == null)
            return Error("No ancient dialogue active");

        var hitbox = ancientLayout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
        if (hitbox == null || !hitbox.Visible || !hitbox.IsEnabled)
            return Error("Dialogue hitbox not available - dialogue may have ended");

        hitbox.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Advancing dialogue"
        };
    }

    private static Dictionary<string, object?> ExecuteChooseRestOption(Player player, Dictionary<string, JsonElement> data)
    {
        if (player.RunState.CurrentRoom is not RestSiteRoom restSiteRoom)
            return Error("Not at a rest site");

        var uiRoom = NRestSiteRoom.Instance;
        if (uiRoom == null)
            return Error("Rest site room is not open");

        // Resolve against the model list, which is what the state's options[].index counts.
        var options = restSiteRoom.Options.ToList();
        if (options.Count == 0)
            return Error("No rest site options available");

        var selectorError = ResolveRestOptionIndex(data, options, uiRoom, out int index);
        if (selectorError != null)
            return selectorError;

        var option = options[index];
        if (!option.IsEnabled)
            return Error($"Rest option [{index}] {option.OptionId} is disabled. Available: {DescribeRestOptions(options, uiRoom)}");

        var button = FindRestSiteButton(uiRoom, option);
        if (button == null)
            return Error(
                $"Rest option [{index}] {option.OptionId} has no button on screen right now; re-read state. "
                + $"Available: {DescribeRestOptions(options, uiRoom)}");

        // Same gate as the event action above: ForceClick bypasses IsEnabled, and
        // NRestSiteButton.SelectOption re-runs the option's whole body without
        // consulting it either. The chosen option stays in the room's option list
        // until that body finishes - and a heal that offers rewards (Tiny Mailbox)
        // sits there for as long as the potions go unclaimed - so a client allowed
        // to pick again (Miniature Tent) would otherwise fire a second option, or
        // the same one twice, into the one still resolving. DisableOptions() runs
        // the instant a selection begins; the buttons are rebuilt (enabled) once
        // it completes, so this only ever blocks the in-flight window.
        if (!button.IsEnabled)
            return Error($"Rest option [{index}] {option.OptionId} is not clickable right now - an option is already resolving");

        string optionName = SafeGetText(() => option.Title) ?? option.OptionId;

        button.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Selecting rest site option: {optionName}",
            ["option_id"] = option.OptionId,
            ["index"] = index
        };
    }

    private static Dictionary<string, object?>? ResolveRestOptionIndex(
        Dictionary<string, JsonElement> data,
        List<RestSiteOption> options,
        NRestSiteRoom uiRoom,
        out int index)
    {
        index = -1;
        string? optionId = TryReadStringParam(data, "option_id", "id", "option");
        bool hasIndex = TryGetIntParam(data, out index, "index", "option_index");

        if (optionId != null)
        {
            int found = options.FindIndex(o =>
                string.Equals(o.OptionId, optionId, StringComparison.OrdinalIgnoreCase));
            if (found < 0)
                return Error($"No rest option '{optionId}' at this rest site. Available: {DescribeRestOptions(options, uiRoom)}");
            // A caller that sends both is working from a stale read if they disagree;
            // picking one silently is how you heal when you meant to smith.
            if (hasIndex && index != found)
                return Error(
                    $"option_id '{optionId}' is index {found}, but index {index} was also given. "
                    + $"Send one of them. Available: {DescribeRestOptions(options, uiRoom)}");
            index = found;
            return null;
        }

        if (!hasIndex)
            return Error(
                "Missing rest site option selector. Provide 'option_id' (preferred; 'id'/'option' also accepted) "
                + $"or 'index' ('option_index'). Available: {DescribeRestOptions(options, uiRoom)}");
        if (index < 0 || index >= options.Count)
            return Error($"Rest option index {index} out of range ({options.Count} options). Available: {DescribeRestOptions(options, uiRoom)}");
        return null;
    }

    private static string DescribeRestOptions(List<RestSiteOption> options, NRestSiteRoom uiRoom)
        => string.Join(", ", options.Select((o, i) =>
            $"[{i}] {o.OptionId} ({(SafeGetBool(() => o.IsEnabled) == true ? "enabled" : "disabled")}"
            + $"{(FindRestSiteButton(uiRoom, o) == null ? ", no button on screen" : "")})"));

    /// <summary>
    /// Finds the on-screen button that belongs to a specific rest site option.
    /// Deliberately never falls back to positional indexing: the scene-tree order of
    /// NRestSiteButton is not the order of RestSiteRoom.Options, so counting buttons
    /// presses a different option - and still reports success - as soon as a relic adds
    /// an option (Shovel, Girya, the Byrdonis egg) or a disabled one is not rendered.
    /// </summary>
    internal static NRestSiteButton? FindRestSiteButton(NRestSiteRoom uiRoom, RestSiteOption option)
    {
        // The game-side lookup is best effort; a button mid-teardown can also throw on
        // .Option, so both halves fall through silently to the other one.
        var button = TryReadQuiet(() => uiRoom.GetButtonForOption(option));
        if (button != null)
            return button;

        // RestSiteOption overrides Equals/==, so this is identity by model, not by position.
        return TryReadQuiet(() => FindAll<NRestSiteButton>(uiRoom)
            .FirstOrDefault(candidate => candidate.Option == option));
    }

    private static Dictionary<string, object?> ExecuteShopOpen(Player player)
    {
        if (player.RunState.CurrentRoom is MerchantRoom)
        {
            var merchantRoom = NMerchantRoom.Instance;
            if (merchantRoom?.Inventory == null)
                return Error("Shop inventory UI is not ready yet; wait a moment and retry");
            if (merchantRoom.Inventory.IsOpen)
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = "Shop inventory is already open"
                };

            merchantRoom.OpenInventory();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = "Opening shop inventory"
            };
        }

        if (player.RunState.CurrentRoom is EventRoom eventRoom
            && eventRoom.CanonicalEvent is FakeMerchant
            && (eventRoom.LocalMutableEvent ?? eventRoom.CanonicalEvent) is FakeMerchant fakeMerchant)
        {
            if (fakeMerchant.StartedFight)
                return Error("The fake merchant is no longer available");

            var eventUi = NEventRoom.Instance;
            var fakeMerchantNode = eventUi == null ? null : FindFirst<NFakeMerchant>(eventUi);
            var inventoryUi = fakeMerchantNode == null ? null : FindFirst<NMerchantInventory>(fakeMerchantNode);
            if (fakeMerchantNode == null || inventoryUi == null)
                return Error("Fake merchant inventory UI is not ready yet; wait a moment and retry");
            if (inventoryUi.IsOpen)
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = "Fake merchant inventory is already open"
                };

            var button = fakeMerchantNode.MerchantButton;
            if (button == null || !button.Visible || !button.IsEnabled)
                return Error("Fake merchant inventory cannot be opened right now");

            button.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = "Opening fake merchant inventory"
            };
        }

        return Error("Not in a shop");
    }

    private static Dictionary<string, object?> ExecuteShopPurchase(Player player, Dictionary<string, JsonElement> data)
    {
        MerchantInventory? inventory = null;

        if (player.RunState.CurrentRoom is MerchantRoom merchantRoom)
        {
            var merchUI = NMerchantRoom.Instance;
            if (merchUI?.Inventory != null && !merchUI.Inventory.IsOpen)
                merchUI.OpenInventory();
            // v0.107 split the merchant into per-player inventories; take the local one.
            inventory = merchantRoom.GetLocalInventory() ?? GetMerchantInventory(merchantRoom);
        }
        else if (player.RunState.CurrentRoom is EventRoom eventRoom
                 && eventRoom.CanonicalEvent is FakeMerchant
                 && (eventRoom.LocalMutableEvent ?? eventRoom.CanonicalEvent) is FakeMerchant fakeMerchant)
        {
            var uiRoom = NEventRoom.Instance;
            var fmNode = uiRoom == null ? null : FindFirst<NFakeMerchant>(uiRoom);
            var inventoryUI = fmNode == null ? null : FindFirst<NMerchantInventory>(fmNode);
            if (inventoryUI?.IsOpen != true)
                return Error("Fake merchant inventory is closed. Use shop_open before purchasing.");
            inventory = fakeMerchant.Inventory;
        }
        else
        {
            return Error("Not in a shop");
        }

        if (inventory == null)
            return Error("Shop inventory not ready yet; wait a moment and retry");

        if (!TryGetIntParam(data, out int index, "index", "item_index"))
            return MissingIntParam("shop item index", "index", "item_index");

        var allEntries = inventory.AllEntries.ToList();
        if (index < 0 || index >= allEntries.Count)
            return Error($"Shop item index {index} out of range ({allEntries.Count} items)");

        var entry = allEntries[index];
        if (!entry.IsStocked)
            return Error("Item is sold out");
        if (!entry.EnoughGold)
            return Error($"Not enough gold (need {entry.Cost}, have {player.Gold})");

        // Fire-and-forget purchase (same path as AutoSlay)
        _ = entry.OnTryPurchaseWrapper(inventory);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Purchasing item for {entry.Cost} gold"
        };
    }

    private static Dictionary<string, object?> ExecuteChooseMapNode(Dictionary<string, JsonElement> data)
    {
        var guard = TryGetTravelableMap(out var mapScreen, out var travelable);
        if (guard != null)
            return guard;

        var targetError = ResolveMapNodeTarget(data, travelable, out var target);
        if (targetError != null)
            return targetError;

        var pt = target.Point;
        mapScreen!.OnMapPointSelectedLocally(target);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Traveling to {pt.PointType} at ({pt.coord.col},{pt.coord.row})"
        };
    }

    /// <summary>
    /// The map is open and a travel can start right now; otherwise the error to return.
    /// </summary>
    private static Dictionary<string, object?>? TryGetTravelableMap(
        out NMapScreen? mapScreen,
        out List<NMapPoint> travelable)
    {
        travelable = new List<NMapPoint>();
        mapScreen = NMapScreen.Instance;
        if (mapScreen == null || (!mapScreen.IsOpen && !IsNodeVisible(mapScreen)))
            return Error("Map screen is not open");

        // The game gates a map click on NMapPoint.IsTravelable + IsInputAllowed;
        // this action reaches OnMapPointSelectedLocally directly, so it has to
        // apply the same gate itself. Without it a call landing in the window
        // between picking a node and the destination room finishing its load
        // enqueues a SECOND VoteForMapCoordAction on top of the one still
        // resolving, which wedges the run.
        if (IsMapTravelInFlight())
            return Error("A travel is already resolving - wait for the destination room to finish loading");

        // An act change leaves the new act's map drawn and travelable while it is
        // still resolving, and travelling into that window strands the transition
        // permanently - see GetTravelableMapPoints for the mechanism. Reported
        // separately from the empty-options case below so the reason is legible.
        if (IsRunTransitionInFlight())
            return Error("A run transition is already resolving - wait for it to finish before travelling");

        travelable = GetTravelableMapPoints(mapScreen);
        if (travelable.Count == 0)
            return Error("No travelable map nodes available (the current room is not finished yet)");

        return null;
    }

    private static Dictionary<string, object?>? ResolveMapNodeTarget(
        Dictionary<string, JsonElement> data,
        List<NMapPoint> travelable,
        out NMapPoint target)
    {
        target = null!;
        // Coordinates are the safe way to name a node: "index" is a position in the
        // current option list, so it shifts whenever the set of travelable nodes does,
        // and a stale index silently routes the run down the wrong branch.
        bool hasCol = TryGetIntParam(data, out int col, "col");
        bool hasRow = TryGetIntParam(data, out int row, "row");

        if (hasCol && hasRow)
        {
            target = travelable.FirstOrDefault(mp =>
                mp.Point.coord.col == col && mp.Point.coord.row == row)!;
            if (target == null)
                return Error(
                    $"No travelable node at ({col},{row}). Travelable: "
                    + string.Join(", ", travelable.Select(mp => $"{mp.Point.PointType} ({mp.Point.coord.col},{mp.Point.coord.row})")));
            return null;
        }
        if (hasCol || hasRow)
            return Error("Provide both 'col' and 'row' to choose a node by coordinate, or 'index' to choose by list position.");

        if (!TryGetIntParam(data, out int index, "index", "node_index"))
            return Error("Missing node selector. Provide 'col' and 'row' (preferred, stable), or 'index' (position in next_options).");
        if (index < 0 || index >= travelable.Count)
            return Error($"Map node index {index} out of range ({travelable.Count} options available)");
        target = travelable[index];
        return null;
    }

    private static Dictionary<string, object?> ExecuteClaimReward(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (IsMapScreenOpenOrVisible() || overlay is not NRewardsScreen rewardsScreen)
            return Error("Rewards screen is not open");

        var enabledButtons = GetRewardButtons(rewardsScreen);
        string? rewardId = TryReadStringParam(data, "reward_id");
        string? revision = TryReadStringParam(data, "screen_revision");
        if (data.ContainsKey("reward_id") && string.IsNullOrWhiteSpace(rewardId))
            return Error("reward_id must be a non-empty string");
        if (data.ContainsKey("screen_revision") && string.IsNullOrWhiteSpace(revision))
            return Error("screen_revision must be a non-empty string from rewards state");
        bool hasIndex = data.ContainsKey("index") || data.ContainsKey("reward_index");
        int? index = null;
        if (hasIndex)
        {
            if (!TryGetIntParam(data, out int parsedIndex, "index", "reward_index"))
                return MissingIntParam("current reward index", "index", "reward_index");
            index = parsedIndex;
        }
        var error = _rewardSession.Resolve(rewardId, index, revision, out var entry);
        if (error != null) return Error(error);
        var button = enabledButtons.First(b => ReferenceEquals(b.Reward, entry!.Key));

        var reward = button.Reward!;
        string rewardDesc = GetRewardTypeName(reward);
        if (reward is GoldReward g)
            rewardDesc = $"gold ({g.Amount})";
        else if (reward is PotionReward p)
        {
            rewardDesc = $"potion ({SafeGetText(() => p.Potion?.Title)})";

            // The game silently refuses a potion reward when the belt is full: the
            // button stays enabled, the reward stays in the list, and nothing happens.
            // Reporting that as "ok" turned a full belt into an infinite claim loop.
            var localPlayer = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState()!);
            if (localPlayer != null && !localPlayer.HasOpenPotionSlots)
                return Error(
                    $"Potion slots full ({localPlayer.PotionSlots.Count(slot => slot != null)}/{localPlayer.MaxPotionCount}); "
                    + $"cannot claim {rewardDesc}. Use discard_potion first, or claim the other rewards and proceed.");
        }
        else if (reward is CardReward)
            rewardDesc = "card (opens card selection)";

        button.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Claiming reward: {rewardDesc}",
            ["reward_id"] = entry!.Id,
            ["action_stage"] = "submitted",
            ["screen_revision"] = _rewardSession.Revision
        };
    }

    private static Dictionary<string, object?> ExecuteSelectCardReward(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCardRewardSelectionScreen cardScreen)
            return Error("Card reward selection screen is not open");

        if (!TryGetIntParam(data, out int cardIndex, "card_index", "index"))
            return MissingIntParam("index of the offered card", "card_index", "index");

        var cardHolders = FindAllSortedByPosition<NCardHolder>(cardScreen)
            .Where(holder => holder.CardModel != null && holder.Visible && holder.IsVisibleInTree())
            .ToList();
        if (cardIndex < 0 || cardIndex >= cardHolders.Count)
            return Error($"Card index {cardIndex} out of range (screen has {cardHolders.Count} cards)");

        var holder = cardHolders[cardIndex];
        string cardName = SafeGetText(() => holder.CardModel?.Title) ?? "unknown";
        var parent = GetCardRewardParent(cardScreen);
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["parent_reward_id"] = parent?.Id,
            ["action_stage"] = "submitted",
            ["message"] = $"Selecting card: {cardName}"
        };
    }

    private static Dictionary<string, object?> ExecuteSkipCardReward()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCardRewardSelectionScreen cardScreen)
            return Error("Card reward selection screen is not open");

        var alternatives = BuildCardRewardAlternatives(cardScreen);
        if (alternatives.Count == 0)
            return Error("No skip option available on this card reward");

        // Pick the Skip option by id rather than "whatever button is first". A relic
        // can add its own alternative (Pael's Wing's SACRIFICE), and clicking the wrong
        // one here silently spends the reward on something else.
        int skipIndex = alternatives.FindIndex(a =>
            string.Equals(a.GetValueOrDefault("option_id")?.ToString(), "Skip", StringComparison.OrdinalIgnoreCase));
        if (skipIndex < 0)
            return Error(
                "No Skip option on this card reward. Available: "
                + DescribeAlternatives(alternatives)
                + ". Use select_card_reward_alternative.");

        return ClickCardRewardAlternative(cardScreen, alternatives, skipIndex);
    }

    /// <summary>
    /// Takes one of the card reward's alternative options by id (preferred) or index.
    ///
    /// Skip used to be the only one reachable, so a relic that adds its own alternative
    /// was unusable from the API — Pael's Wing turns a card reward into progress towards
    /// a relic, and skip_card_reward does not count as sacrificing, so the relic did
    /// nothing at all for a whole run.
    /// </summary>
    private static Dictionary<string, object?> ExecuteSelectCardRewardAlternative(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCardRewardSelectionScreen cardScreen)
            return Error("Card reward selection screen is not open");

        var alternatives = BuildCardRewardAlternatives(cardScreen);
        if (alternatives.Count == 0)
            return Error("This card reward has no alternative options");

        string? optionId = TryReadStringParam(data, "option_id", "option", "alternative");
        int chosen;

        if (optionId != null)
        {
            chosen = alternatives.FindIndex(a =>
                string.Equals(a.GetValueOrDefault("option_id")?.ToString(), optionId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.GetValueOrDefault("title")?.ToString(), optionId, StringComparison.OrdinalIgnoreCase));
            if (chosen < 0)
                return Error($"No alternative '{optionId}' on this card reward. Available: {DescribeAlternatives(alternatives)}");
        }
        else
        {
            if (!TryGetIntParam(data, out chosen, "index", "option_index"))
                return Error($"Missing 'option_id' (or 'index'). Available: {DescribeAlternatives(alternatives)}");
            if (chosen < 0 || chosen >= alternatives.Count)
                return Error($"Alternative index {chosen} out of range ({alternatives.Count} options). Available: {DescribeAlternatives(alternatives)}");
        }

        return ClickCardRewardAlternative(cardScreen, alternatives, chosen);
    }

    private static Dictionary<string, object?> ClickCardRewardAlternative(
        NCardRewardSelectionScreen cardScreen,
        List<Dictionary<string, object?>> alternatives,
        int index)
    {
        var buttons = FindAll<NCardRewardAlternativeButton>(cardScreen);
        if (index < 0 || index >= buttons.Count)
            return Error($"Alternative index {index} is no longer on screen; re-read state");

        var button = buttons[index];
        if (!button.IsEnabled)
            return Error($"Alternative '{alternatives[index].GetValueOrDefault("option_id")}' is disabled");

        var parent = GetCardRewardParent(cardScreen);
        var uiOptions = GetInstanceFieldValue(cardScreen, "_extraOptions")
            as System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative>;
        bool? closesWithoutConsuming = uiOptions != null && index < uiOptions.Count
            ? uiOptions[index].AfterSelected == MegaCrit.Sts2.Core.Entities.Rewards.PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward
            : null;
        button.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["parent_reward_id"] = parent?.Id,
            ["action_stage"] = "submitted",
            ["closes_selection_without_consuming_reward"] = closesWithoutConsuming,
            ["message"] = $"Taking card reward alternative: {alternatives[index].GetValueOrDefault("title") ?? alternatives[index].GetValueOrDefault("option_id")}",
            ["option_id"] = alternatives[index].GetValueOrDefault("option_id")
        };
    }

    private static string DescribeAlternatives(List<Dictionary<string, object?>> alternatives)
        => string.Join(", ", alternatives.Select(a => $"[{a.GetValueOrDefault("index")}] {a.GetValueOrDefault("option_id")}"));

    private static Dictionary<string, object?> ExecuteProceed()
        => ProceedFromRewards()
           ?? ProceedFromRestSite()
           ?? ProceedFromMerchant()
           ?? ProceedFromFakeMerchant()
           ?? ProceedFromTreasure()
           ?? Error("No proceed button available or enabled");

    private static Dictionary<string, object?>? ProceedFromRewards()
    {
        if (NOverlayStack.Instance?.Peek() is not NRewardsScreen rewardsScreen)
            return null;
        var btn = FindFirst<NProceedButton>(rewardsScreen);
        if (btn == null || !IsControlVisibleOrActionable(btn))
            return null;
        btn.ForceClick();
        return Ok("Proceeding from rewards");
    }

    private static Dictionary<string, object?>? ProceedFromRestSite()
    {
        if (NRestSiteRoom.Instance is not { } restRoom || !IsControlVisibleOrActionable(restRoom.ProceedButton))
            return null;
        restRoom.ProceedButton.ForceClick();
        return Ok("Proceeding from rest site");
    }

    private static Dictionary<string, object?>? ProceedFromMerchant()
    {
        if (NMerchantRoom.Instance is not { } merchRoom)
            return null;
        if (merchRoom.Inventory?.IsOpen == true)
        {
            if (!TryCloseMerchantInventoryPanel(merchRoom.Inventory))
                return Error("Shop inventory is open but could not be closed");
            return Ok("Closing shop inventory; call proceed again to leave the shop room");
        }
        if (!IsControlVisibleOrActionable(merchRoom.ProceedButton))
            return null;
        merchRoom.ProceedButton.ForceClick();
        return Ok("Proceeding from shop");
    }

    private static Dictionary<string, object?>? ProceedFromFakeMerchant()
    {
        if (NEventRoom.Instance is not { } evtRoom)
            return null;
        var fmNode = FindFirst<NFakeMerchant>(evtRoom);
        if (fmNode == null)
            return null;

        var fmInventory = FindFirst<NMerchantInventory>(fmNode);
        if (fmInventory is { IsOpen: true })
        {
            if (!TryCloseMerchantInventoryPanel(fmInventory))
                return Error("Fake merchant inventory is open but could not be closed");
            return Ok("Closing fake merchant inventory; call proceed again to leave the event");
        }

        var proceedBtn = FindFirst<NProceedButton>(fmNode);
        if (proceedBtn == null || !IsControlVisibleOrActionable(proceedBtn))
            return null;
        proceedBtn.ForceClick();
        return Ok("Proceeding from fake merchant");
    }

    private static Dictionary<string, object?>? ProceedFromTreasure()
    {
        var treasureUI = FindFirst<NTreasureRoom>(
            ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);
        if (treasureUI == null || !IsControlVisibleOrActionable(treasureUI.ProceedButton))
            return null;
        treasureUI.ProceedButton.ForceClick();
        return Ok("Proceeding from treasure room");
    }

    private static Dictionary<string, object?> Ok(string message)
        => new(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = message };

    private static bool TryCloseMerchantInventoryPanel(NMerchantInventory inventory)
    {
        return TryRead<bool?>(() =>
        {
            var closeMethod = inventory.GetType().GetMethod(
                "Close",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (closeMethod == null)
                return false;

            closeMethod.Invoke(inventory, null);
            return true;
        }) ?? false;
    }

    private static Dictionary<string, object?> ExecuteSelectCard(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();

        if (!TryGetIntParam(data, out int index, "index", "card_index"))
            return MissingIntParam("card index in the grid", "index", "card_index");

        // Optional override so a caller can force an interaction path without
        // waiting on a mod rebuild: "grid", "holder", or "auto" (default).
        string method = data.TryGetValue("method", out var methodElem)
            ? (methodElem.GetString() ?? "auto").ToLowerInvariant()
            : "auto";

        if (overlay is NCardGridSelectionScreen gridScreen)
            return ExecuteSelectCardInGrid(gridScreen, index, method);
        if (overlay is NChooseACardSelectionScreen chooseScreen)
            return ExecuteSelectCardInChooseScreen(chooseScreen, index);

        return Error("No card selection screen is open");
    }

    private static Dictionary<string, object?> ExecuteSelectCardInGrid(
        NCardGridSelectionScreen gridScreen, int index, string method)
    {
        // Once a card has opened the preview, the grid is no longer the active
        // interaction surface. Some clients repeat select_card(index) because
        // older state responses only exposed card indices. Treat that repeated
        // selection as confirmation instead of emitting a no-op grid signal.
        var previewResult = ConfirmOpenGridPreview(gridScreen);
        if (previewResult != null)
            return previewResult;

        if (!TryResolveGridPress(gridScreen, method, out var grid, out var holders, out var failure))
            return failure!;

        if (index < 0 || index >= holders.Count)
            return Error($"Card index {index} out of range ({holders.Count} cards available)");

        var holder = holders[index];
        var cardModel = holder.CardModel;
        string cardName = SafeGetText(() => cardModel?.Title) ?? "unknown";

        // Pressing the holder drives the real signal chain, which is what the
        // choose-a-card path already does. "grid" remains available via the method
        // parameter for anything that depends on the old path.
        bool useHolder = !string.Equals(method, "grid", StringComparison.Ordinal);

        // The screen's own selection set, so the outcome is read back from the game
        // instead of assumed.
        var selection = GetCardSelectSelection(gridScreen);
        bool? wasSelected = (selection != null && cardModel != null) ? selection.Contains(cardModel) : null;

        if (useHolder)
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        else
            grid!.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);

        return DescribeGridPress(gridScreen, selection, cardModel, cardName, wasSelected, useHolder);
    }

    /// <summary>
    /// Treats a repeated select_card while the preview is open as the confirmation it was
    /// meant to be. Null when no preview container is showing.
    /// </summary>
    private static Dictionary<string, object?>? ConfirmOpenGridPreview(NCardGridSelectionScreen gridScreen)
    {
        foreach (var containerName in new[] { "%UpgradeSinglePreviewContainer", "%UpgradeMultiPreviewContainer", "%PreviewContainer" })
        {
            if (gridScreen.GetNodeOrNull<Godot.Control>(containerName)?.Visible != true)
                continue;

            var confirmResult = ExecuteConfirmSelection();
            if (confirmResult.TryGetValue("status", out var status) && string.Equals(status?.ToString(), "ok", StringComparison.Ordinal))
            {
                confirmResult["message"] =
                    "Card selection preview was already open; confirming the selected card instead of selecting it again";
            }
            return confirmResult;
        }

        return null;
    }

    /// <summary>
    /// The grid to press and its holders, or the reason there is nothing to press.
    /// </summary>
    /// <remarks>
    /// NCardGrid.HolderPressed is the signal the grid *emits after* a holder is pressed.
    /// Emitting it directly notifies the grid's subscribers while skipping the grid's own
    /// selection bookkeeping, so screens that read that bookkeeping never register the
    /// click: the call reports success and the screen sits unchanged. That stranded runs on
    /// "Choose a card to Enchant" and on event screens like "Choose 2 Common Cards to Add to
    /// Your Deck", where every card could be clicked and Confirm still refused. That is why
    /// the holder press is the default and the grid signal is only kept as an option.
    ///
    /// While a preview covers the grid the game takes the grid out of the input path
    /// (FocusBehaviorRecursive = Disabled, scrolling off). Firing HolderPressed anyway still
    /// mutates the selection behind the panel, and re-reaching MaxSelect re-runs
    /// PreviewSelection, which appends another copy of every selected card to the preview row
    /// - the "select_card adds instead of toggling" symptom of A-7.
    /// </remarks>
    private static bool TryResolveGridPress(
        NCardGridSelectionScreen gridScreen,
        string method,
        out NCardGrid? grid,
        out List<NGridCardHolder> holders,
        out Dictionary<string, object?>? failure)
    {
        failure = null;
        grid = FindFirst<NCardGrid>(gridScreen);
        if (grid == null && !string.Equals(method, "holder", StringComparison.Ordinal))
        {
            holders = new List<NGridCardHolder>();
            failure = Error("Card grid not found in selection screen");
            return false;
        }

        if (FindVisibleCardSelectPreview(gridScreen) != null)
        {
            holders = new List<NGridCardHolder>();
            failure = Error("A selection preview is open - use confirm_selection to apply it, or cancel_selection to go back to the grid");
            return false;
        }

        holders = FindAllSortedByPosition<NGridCardHolder>(gridScreen);
        return true;
    }

    private static Dictionary<string, object?> DescribeGridPress(
        NCardGridSelectionScreen gridScreen,
        HashSet<CardModel>? selection,
        CardModel? cardModel,
        string cardName,
        bool? wasSelected,
        bool useHolder)
    {
        // The screen can complete and close on the press itself (NSimpleCardSelectScreen
        // with RequireManualConfirmation off), leaving nothing to read back.
        if (!IsCardSelectScreenStillOpen(gridScreen))
            return Ok($"Selected {cardName} - selection complete, screen closed");

        if (selection == null || cardModel == null)
            return Ok($"Toggling card selection: {cardName}");

        bool isSelected = selection.Contains(cardModel);
        if (wasSelected == isSelected)
            return DescribeUnchangedCardPress(gridScreen, cardName, isSelected);

        var toggled = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"{(isSelected ? "Selected" : "Deselected")} {cardName}",
            ["selected"] = isSelected,
            ["selected_count"] = selection.Count,
            // Reported so a caller that sees no effect knows what to retry.
            ["method"] = useHolder ? "holder" : "grid",
            ["screen"] = gridScreen.GetType().Name
        };

        var prefs = GetCardSelectPrefs(gridScreen);
        if (prefs.HasValue)
        {
            toggled["min_select"] = prefs.Value.MinSelect;
            toggled["max_select"] = prefs.Value.MaxSelect;
        }

        // Reaching MaxSelect opens the preview by itself on these screens; the caller
        // needs to know the next action is confirm/cancel, not another select_card.
        if (FindVisibleCardSelectPreview(gridScreen) != null)
            toggled["preview_showing"] = true;

        return toggled;
    }

    /// <summary>
    /// The press did not change the selection. The one screen that ignores a press is
    /// NSimpleCardSelectScreen once the set is full, so say which limit refused it instead
    /// of reporting a success for a no-op.
    /// </summary>
    private static Dictionary<string, object?> DescribeUnchangedCardPress(
        NCardGridSelectionScreen gridScreen, string cardName, bool isSelected)
    {
        var limit = GetCardSelectPrefs(gridScreen);
        if (isSelected)
            return Error($"{cardName} is still selected - the screen did not accept the press");
        return limit.HasValue
            ? Error($"{cardName} was not selected - at most {limit.Value.MaxSelect} card(s) can be selected, deselect one first")
            : Error($"{cardName} was not selected - the screen did not accept the press");
    }

    private static Dictionary<string, object?> ExecuteSelectCardInChooseScreen(
        NChooseACardSelectionScreen chooseScreen, int index)
    {
        var holders = FindAllSortedByPosition<NGridCardHolder>(chooseScreen)
            .Where(holder => holder.CardModel != null)
            .ToList();
        if (index < 0 || index >= holders.Count)
            return Error($"Card index {index} out of range ({holders.Count} cards available)");

        // `SelectHolder` swallows a press inside the screen's 350 ms opening
        // deadzone without failing, so reporting `ok` here would tell a
        // client its pick landed when nothing happened at all. See
        // `ChooseCardScreenAcceptsInput`, which exports the same gate as
        // `card_select.can_select` so a client can wait instead of guess.
        if (!ChooseCardScreenAcceptsInput(chooseScreen))
            return Error("Choose-a-card screen is still inside its 350ms opening deadzone - retry once card_select.can_select is true");

        var holder = holders[index];
        if (holder.CardModel == null)
            return Error($"Card index {index} does not contain a card");
        if (!IsCardHolderSelectable(holder))
            return Error($"Card index {index} is not selectable");

        string cardName = SafeGetText(() => holder.CardModel?.Title) ?? "unknown";
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);

        return Ok($"Choosing card: {cardName}");
    }

    /// <summary>
    /// Whether a selection screen is still the screen being driven. The screens take
    /// themselves off the overlay stack (NOverlayStack.Remove) the moment they complete,
    /// so this going false is the proof that a confirm press actually landed.
    /// </summary>
    private static bool IsCardSelectScreenStillOpen(NCardGridSelectionScreen screen)
        => IsLiveNode(screen) && ReferenceEquals(NOverlayStack.Instance?.Peek(), screen);

    private static Dictionary<string, object?> ExecuteConfirmSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NChooseACardSelectionScreen)
            return Error("Choose-a-card screen requires no confirmation - use select_card(index) to pick directly");
        if (overlay is not NCardGridSelectionScreen screen)
            return Error("No card selection screen is open");

        // The count the screen itself holds, and the min/max it will accept. The game's
        // completion check is `count >= MinSelect && count <= MaxSelect`; when it fails the
        // click is simply swallowed, which is how confirm_selection used to return ok and
        // do nothing (A-7).
        var selection = GetCardSelectSelection(screen);
        var prefs = GetCardSelectPrefs(screen);
        int? selectedCount = selection?.Count;

        // Step two of the screen: a preview panel is up, and its own Confirm is the button
        // that actually applies the selection. This has to be checked first - the screen's
        // main Confirm is still enabled underneath, and on these screens pressing it means
        // "open the preview", which appends a second copy of every selected card (A-7).
        var preview = FindVisibleCardSelectPreview(screen);
        if (preview != null)
            return ConfirmSelectionPreview(screen, preview, selectedCount);

        // Step one: check the count before pressing anything.
        var countFailure = ValidateSelectedCount(selectedCount, prefs);
        if (countFailure != null)
            return countFailure;

        var mainConfirm = ResolveMainConfirm(screen);
        if (mainConfirm is not { IsEnabled: true })
        {
            return Error(selectedCount.HasValue && prefs.HasValue
                ? $"No confirm button is currently enabled ({selectedCount.Value} selected, {prefs.Value.MinSelect}-{prefs.Value.MaxSelect} accepted)"
                : "No confirm button is currently enabled - select more cards first");
        }

        mainConfirm.ForceClick();
        return DescribeConfirmedSelection(screen, selectedCount);
    }

    private static Dictionary<string, object?> ConfirmSelectionPreview(
        NCardGridSelectionScreen screen, Control preview, int? selectedCount)
    {
        var previewConfirm = GetCardSelectPreviewConfirm(preview);
        if (previewConfirm is not { IsEnabled: true })
            return Error("The selection preview's confirm button is not enabled - use cancel_selection to go back to the grid");

        previewConfirm.ForceClick();
        if (IsCardSelectScreenStillOpen(screen))
        {
            return Error(selectedCount is 0
                ? "The preview is empty - cancel_selection to go back to the grid and select at least one card"
                : "The preview's confirm did nothing - the screen would not accept the current selection");
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Confirming selection from preview"
        };
    }

    /// <summary>
    /// Step one: refuse a count the screen would only swallow. The game accepts a selection
    /// when it is at least MinSelect and at most MaxSelect.
    /// </summary>
    private static Dictionary<string, object?>? ValidateSelectedCount(
        int? selectedCount, CardSelectorPrefs? prefs)
    {
        if (!selectedCount.HasValue || !prefs.HasValue)
            return null;

        int min = prefs.Value.MinSelect;
        int max = prefs.Value.MaxSelect;
        if (selectedCount.Value < min)
            return Error($"{selectedCount.Value} of {min} required card(s) selected - select {min - selectedCount.Value} more before confirming");
        if (selectedCount.Value > max)
            return Error($"{selectedCount.Value} cards selected but at most {max} can be confirmed - deselect {selectedCount.Value - max}");
        return null;
    }

    /// <summary>
    /// The screen's own Confirm, or any enabled, visible NConfirmButton in the screen tree for
    /// a subclass that names its confirm button something else. On the
    /// remove/upgrade/enchant/transform screens this opens the preview; only
    /// NSimpleCardSelectScreen completes straight from here.
    /// </summary>
    private static NConfirmButton? ResolveMainConfirm(NCardGridSelectionScreen screen)
    {
        var mainConfirm = GetCardSelectMainConfirm(screen);
        if (mainConfirm is { IsEnabled: true })
            return mainConfirm;
        return FindAll<NConfirmButton>(screen).FirstOrDefault(b => b.IsEnabled && b.IsVisibleInTree());
    }

    /// <summary>
    /// Reads the screen after the press: gone means it landed, a preview means the press only
    /// opened the preview, and still on the grid means the game swallowed it.
    /// </summary>
    private static Dictionary<string, object?> DescribeConfirmedSelection(
        NCardGridSelectionScreen screen, int? selectedCount)
    {
        // The screen takes itself off the overlay stack the moment it completes, so a stack
        // that no longer has it on top is the proof that the press landed.
        if (!IsCardSelectScreenStillOpen(screen))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = "Confirming selection"
            };
        }

        // Say which of the two steps just happened rather than reporting "confirmed" for a
        // press that only opened the preview.
        if (FindVisibleCardSelectPreview(screen) != null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = selectedCount.HasValue
                    ? $"Selection preview opened ({selectedCount.Value} card(s)) - call confirm_selection again to apply it"
                    : "Selection preview opened - call confirm_selection again to apply it",
                ["preview_showing"] = true
            };
        }

        // Still on the grid, no preview: the game swallowed the press. The enchant and
        // remove screens do that with an empty selection (their confirm handlers bail on
        // Count == 0), which is the "ok that did nothing" of A-7.
        return Error(selectedCount is 0
            ? "No card is selected - select at least one card, or use cancel_selection to leave the screen"
            : "The confirm press did nothing - the screen would not accept the current selection");
    }

    private static Dictionary<string, object?> ExecuteCancelSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();

        // Handle choose-a-card screen (skip button)
        if (overlay is NChooseACardSelectionScreen chooseScreen)
        {
            var skipButton = chooseScreen.GetNodeOrNull<NClickableControl>("SkipButton");
            if (IsControlVisibleOrActionable(skipButton))
            {
                skipButton.ForceClick();
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = "Skipping card choice"
                };
            }
            return Error("No skip option available - a card must be chosen");
        }

        if (overlay is not NCardGridSelectionScreen screen)
            return Error("No card selection screen is open");

        // If a preview is showing, cancel back to the grid. This also clears the screen's
        // selection and empties the preview row, which is the only way out of a preview
        // that was opened with the wrong cards.
        var preview = FindVisibleCardSelectPreview(screen);
        if (preview != null)
        {
            var cancelBtn = GetCardSelectPreviewCancel(preview);
            if (cancelBtn is { IsEnabled: true })
            {
                cancelBtn.ForceClick();
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = "Cancelling preview - returning to card selection (selection cleared)"
                };
            }
        }

        // Close the screen entirely
        var closeButton = screen.GetNodeOrNull<NBackButton>("%Close");
        if (IsControlVisibleOrActionable(closeButton))
        {
            closeButton.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = "Closing card selection screen"
            };
        }

        return Error("No cancel/close button is currently enabled - selection may be mandatory");
    }

    private static Dictionary<string, object?> ExecuteSelectBundle(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseABundleSelectionScreen screen)
            return Error("No bundle selection screen is open");

        if (!TryGetIntParam(data, out int index, "index", "bundle_index"))
            return MissingIntParam("bundle index", "index", "bundle_index");

        var previewContainer = screen.GetNodeOrNull<Godot.Control>("%BundlePreviewContainer");
        if (previewContainer?.Visible == true)
            return Error("A bundle preview is already open - confirm or cancel it first");

        var bundles = FindAll<NCardBundle>(screen)
            .Where(bundle => bundle.Visible
                             && bundle.IsVisibleInTree()
                             && bundle.Hitbox.IsEnabled
                             && IsNodeVisible(bundle.Hitbox))
            .ToList();
        if (index < 0 || index >= bundles.Count)
            return Error($"Bundle index {index} out of range ({bundles.Count} bundles available)");

        bundles[index].Hitbox.ForceClick();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Selecting bundle {index}"
        };
    }

    private static Dictionary<string, object?> ExecuteConfirmBundleSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseABundleSelectionScreen screen)
            return Error("No bundle selection screen is open");

        var confirmButton = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        if (!IsControlVisibleOrActionable(confirmButton))
            return Error("Bundle confirm button is not enabled");

        confirmButton.ForceClick();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Confirming bundle selection"
        };
    }

    private static Dictionary<string, object?> ExecuteCancelBundleSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseABundleSelectionScreen screen)
            return Error("No bundle selection screen is open");

        var cancelButton = screen.GetNodeOrNull<NBackButton>("%Cancel");
        if (!IsControlVisibleOrActionable(cancelButton))
            return Error("Bundle cancel button is not enabled");

        cancelButton.ForceClick();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Cancelling bundle selection"
        };
    }

    private static Dictionary<string, object?> ExecuteCombatSelectCard(Dictionary<string, JsonElement> data)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !hand.IsInCardSelection)
            return Error("No in-combat card selection is active");

        if (!TryGetIntParam(data, out int index, "card_index", "index"))
            return MissingIntParam("index of the card in hand", "card_index", "index");

        var holders = hand.ActiveHolders;
        if (index < 0 || index >= holders.Count)
            return Error($"Card index {index} out of range ({holders.Count} selectable cards)");

        var holder = holders[index];
        if (holder.CardModel == null)
            return Error($"Card index {index} does not contain a card");
        if (!IsCardHolderSelectable(holder))
            return Error($"Card index {index} is not selectable");

        string cardName = SafeGetText(() => holder.CardModel?.Title) ?? "unknown";

        // Emit the Pressed signal - same path the game UI uses
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Selecting card from hand: {cardName}"
        };
    }

    private static Dictionary<string, object?> ExecuteCombatConfirmSelection()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !hand.IsInCardSelection)
            return Error("No in-combat card selection is active");

        var confirmBtn = hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton");
        if (!IsControlVisibleOrActionable(confirmBtn))
            return Error("Confirm button is not enabled - select more cards first");

        confirmBtn.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Confirming combat card selection"
        };
    }

    private static Dictionary<string, object?> ExecuteSelectRelic(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseARelicSelection screen)
            return Error("No relic selection screen is open");

        if (!TryGetIntParam(data, out int index, "index", "relic_index"))
            return MissingIntParam("relic index", "index", "relic_index");

        var holders = FindAll<NRelicBasicHolder>(screen)
            .Where(holder => holder.Relic?.Model != null && holder.IsEnabled && holder.Visible && holder.IsVisibleInTree())
            .ToList();
        if (index < 0 || index >= holders.Count)
            return Error($"Relic index {index} out of range ({holders.Count} relics available)");

        var holder = holders[index];
        string relicName = SafeGetText(() => holder.Relic?.Model?.Title) ?? "unknown";
        holder.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Selecting relic: {relicName}"
        };
    }

    private static Dictionary<string, object?> ExecuteSkipRelicSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseARelicSelection screen)
            return Error("No relic selection screen is open");

        var skipButton = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        if (skipButton is not { IsEnabled: true, Visible: true } || !skipButton.IsVisibleInTree())
            return Error("No skip option available");

        skipButton.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Skipping relic selection"
        };
    }

    private static Dictionary<string, object?> ExecuteClaimTreasureRelic(Dictionary<string, JsonElement> data)
    {
        var treasureUI = FindFirst<NTreasureRoom>(
            ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);
        if (treasureUI == null)
            return Error("Treasure room is not open");

        var relicCollection = treasureUI.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
        if (relicCollection?.Visible != true)
            return Error("Relic collection is not visible - chest may not be opened yet");

        if (!TryGetIntParam(data, out int index, "index", "relic_index"))
            return MissingIntParam("relic index", "index", "relic_index");

        var holders = FindAll<NTreasureRoomRelicHolder>(relicCollection)
            .Where(h => h.IsEnabled && h.Visible && h.IsVisibleInTree())
            .ToList();

        if (index < 0 || index >= holders.Count)
            return Error($"Relic index {index} out of range ({holders.Count} relics available)");

        var holder = holders[index];
        string relicName = SafeGetText(() => holder.Relic?.Model?.Title) ?? "unknown";
        holder.ForceClick();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Claiming treasure relic: {relicName}"
        };
    }

    private static Dictionary<string, object?> ExecuteCrystalSphereSetTool(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCrystalSphereScreen screen)
            return Error("Crystal Sphere screen is not open");

        if (!data.TryGetValue("tool", out var toolElem))
            return Error("Missing 'tool' (expected 'big' or 'small')");

        string tool = toolElem.GetString() ?? "";
        var button = tool switch
        {
            "big" => screen.GetNodeOrNull<NClickableControl>("%BigDivinationButton"),
            "small" => screen.GetNodeOrNull<NClickableControl>("%SmallDivinationButton"),
            _ => null
        };

        if (button == null)
            return Error($"Unknown Crystal Sphere tool: {tool}");
        if (!button.Visible || !button.IsVisibleInTree() || !button.IsEnabled)
            return Error($"Crystal Sphere tool '{tool}' is not available");

        button.ForceClick();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Setting Crystal Sphere tool to {tool}"
        };
    }

    private static Dictionary<string, object?> ExecuteCrystalSphereClickCell(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCrystalSphereScreen screen)
            return Error("Crystal Sphere screen is not open");

        if (!data.TryGetValue("x", out var xElem))
            return Error("Missing 'x' (cell x-coordinate)");
        if (!data.TryGetValue("y", out var yElem))
            return Error("Missing 'y' (cell y-coordinate)");

        int x = xElem.GetInt32();
        int y = yElem.GetInt32();

        var cell = FindAll<NCrystalSphereCell>(screen)
            .FirstOrDefault(c => c.Entity.X == x && c.Entity.Y == y);
        if (cell == null)
            return Error($"Crystal Sphere cell ({x}, {y}) was not found");
        if (!cell.Entity.IsHidden || !cell.Visible || !cell.IsVisibleInTree())
            return Error($"Crystal Sphere cell ({x}, {y}) is not clickable");

        cell.EmitSignal(NClickableControl.SignalName.Released, cell);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Clicking Crystal Sphere cell ({x}, {y})"
        };
    }

    private static Dictionary<string, object?> ExecuteCrystalSphereProceed()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCrystalSphereScreen screen)
            return Error("Crystal Sphere screen is not open");

        var proceedButton = FindCrystalSphereProceedButton(screen);
        if (proceedButton == null)
            return Error("Crystal Sphere proceed button is not enabled");

        proceedButton.ForceClick();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Proceeding from Crystal Sphere"
        };
    }

    private static NProceedButton? FindCrystalSphereProceedButton(NCrystalSphereScreen screen)
    {
        var namedButton = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (IsControlVisibleOrActionable(namedButton))
            return namedButton;

        return FindAll<NProceedButton>(screen)
            .FirstOrDefault(IsControlVisibleOrActionable);
    }

    /// <summary>
    /// Resolve a player creature from a target id: either "player_N"
    /// (N = index in runState.Players, as exposed in players[].entity_id)
    /// or a numeric combat_id. Returns null if the id doesn't name a player.
    /// </summary>
    private static Creature? ResolvePlayerTarget(Player me, ICombatState? combatState, string entityId)
    {
        if (uint.TryParse(entityId, out uint combatId))
        {
            var byId = combatState?.GetCreature(combatId);
            return byId is { IsPlayer: true } ? byId : null;
        }

        const string prefix = "player_";
        if (entityId.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(entityId.Substring(prefix.Length), CultureInfo.InvariantCulture, out int idx)
            && idx >= 0 && idx < me.RunState.Players.Count)
            return me.RunState.Players[idx].Creature;

        return null;
    }

    /// <summary>Comma list of the caller's living teammates' entity ids, for error messages.</summary>
    private static string ListAllyTargetIds(Player me)
    {
        var ids = me.RunState.Players
            .Select((p, i) => (p, i))
            .Where(t => t.p != me && t.p.Creature.IsAlive)
            .Select(t => $"player_{t.i}")
            .ToList();
        return ids.Count > 0 ? string.Join(", ", ids) : "(none)";
    }

    /// <summary>Display name for a creature: monster title, player character title, or "target".</summary>
    private static string DescribeCreature(Creature? creature)
    {
        if (creature == null)
            return "target";
        return SafeGetText(() => creature.Monster?.Title)
               ?? SafeGetText(() => creature.Player?.Character.Title)
               ?? "target";
    }

    private static Creature? ResolveTarget(ICombatState combatState, string entityId)
    {
        // Try to match by entity_id pattern: "model_entry_N"
        // First try matching by combat_id if it's a pure number
        if (uint.TryParse(entityId, out uint combatId))
            return combatState.GetCreature(combatId);

        // Match by entity_id (e.g., "jaw_worm_0"). Ids come from the same registry the
        // state builder uses, so they keep pointing at the creature they were read for
        // even if another enemy died in between.
        RefreshEntityIdRegistry(combatState);
        foreach (var creature in combatState.Enemies)
        {
            if (!creature.IsAlive) continue;
            if (string.Equals(GetStableEntityId(creature), entityId, StringComparison.Ordinal))
                return creature;
        }

        return null;
    }

    /// <summary>
    /// Reveals every obtained-but-unrevealed epoch, opening the Timeline if needed.
    ///
    /// A finished run leaves its epochs "Obtained". Until they are revealed the main
    /// menu hides `singleplayer` entirely, so the API could not start the next run —
    /// the single thing that made unattended play impossible, because every run ended
    /// in a screenshot and a hand-placed click.
    ///
    /// One step per call so the caller can poll; the response carries `done` and
    /// `pending_epoch_ids`. Reveals go through NEpochSlot.RevealEpoch, the same method
    /// the slot's own click handler calls, so the epoch's unlocks are granted and the
    /// progress file is written exactly as a manual reveal would.
    /// </summary>
    internal static Dictionary<string, object?> ExecuteTimelineRevealEpochs()
    {
        var tree = (Engine.GetMainLoop()) as SceneTree;
        if (tree?.Root == null)
            return Error("Cannot access scene tree");

        var pending = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot");

        var timelineScreen = FindFirst<NTimelineScreen>(tree.Root);
        bool timelineVisible = timelineScreen != null && IsNodeVisible(timelineScreen);

        if (!timelineVisible)
        {
            if (pending.Count == 0)
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = "No epochs pending reveal",
                    ["pending_epoch_ids"] = pending,
                    ["done"] = true
                };

            var mainMenu = FindFirst<NMainMenu>(tree.Root);
            if (mainMenu == null)
                return Error("Not on the main menu; open the main menu before revealing epochs");

            var opened = ClickMenuButtonField(mainMenu, "_timelineButton", "Opening timeline to reveal epochs");
            if (string.Equals((string?)opened["status"], "error", StringComparison.Ordinal))
                return opened;

            opened["pending_epoch_ids"] = pending;
            opened["done"] = false;
            opened["retry"] = true;
            return opened;
        }

        // Dismiss the tutorial, any open unlock/inspect screen, and drain the queue
        // before looking for the next slot. ExecuteMenuSelect("advance") already
        // encodes that order, and finishes by revealing one slot.
        var step = ExecuteMenuSelect("advance");
        bool done = step.TryGetValue("done", out var d) && d is true;

        pending = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot");
        step["pending_epoch_ids"] = pending;

        if (done && pending.Count == 0)
        {
            // Leave the timeline so the caller lands back on a main menu that has
            // `singleplayer` again.
            var back = ExecuteMenuSelect("back");
            step["message"] = "All epochs revealed; returned to the main menu";
            step["left_timeline"] = string.Equals((string?)back["status"], "ok", StringComparison.Ordinal);
            step["done"] = true;
            return step;
        }

        step["done"] = false;
        step["retry"] = true;
        return step;
    }

    /// <summary>
    /// Reveals the first epoch slot that is obtained but not yet revealed.
    /// Returns null when there is nothing left to reveal.
    /// </summary>
    private static Dictionary<string, object?>? TryRevealOneEpochSlot(NTimelineScreen timelineScreen)
    {
        var pending = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot");
        if (pending.Count == 0)
            return null;

        // Look for the slot first. IsTimelineScreenBusy is a coarse heuristic (it reads
        // _isUiVisible and the input blocker) and reports "busy" on a timeline that is
        // sitting still and perfectly interactive, so it must not gate the reveal —
        // only explain why no slot is available yet.
        var slot = FindAll<NEpochSlot>(timelineScreen)
            .FirstOrDefault(s => s.State == EpochSlotState.Obtained);

        if (slot == null)
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = IsTimelineScreenBusy(timelineScreen)
                    ? "Timeline is still animating; retry after the next state poll"
                    : "Epochs are obtained but no revealable slot is mounted on the timeline yet; retry after the next state poll",
                ["pending_epoch_ids"] = pending,
                ["retry"] = true
            };

        string epochId = SafeGetText(() => slot.model?.Id) ?? "unknown";

        // Mirror NEpochSlot.OnRelease for the Obtained branch: block input, drop focus,
        // then reveal. RevealEpoch writes the progress state, opens the inspect screen
        // and runs the unlock animation, which is what actually grants the epoch's cards
        // and relics. It is private only because nothing but the click handler calls it.
        var reveal = TryRead<System.Reflection.MethodInfo?>("timeline.reveal_epoch.method",
            () => typeof(NEpochSlot).GetMethod(
                "RevealEpoch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        if (reveal == null)
            return Error("NEpochSlot.RevealEpoch is missing - the game's timeline API changed");

        var failure = TryAction("timeline.reveal_epoch", () =>
        {
            timelineScreen.DisableInput();
            slot.GetViewport()?.GuiReleaseFocus();
            reveal.Invoke(slot, null);
        });
        if (failure != null)
        {
            TryDoQuiet(timelineScreen.EnableInput);
            return Error($"Failed to reveal epoch '{epochId}': {failure}");
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Revealing epoch {epochId}",
            ["revealed_epoch_id"] = epochId,
            ["pending_epoch_ids"] = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot"),
            ["retry"] = true
        };
    }

    internal static Dictionary<string, object?> ExecuteMenuSelect(string option, string? seed = null, int? ascension = null)
    {
        option = option.Trim();

        // An empty option is allowed when there is another instruction to carry out
        // (currently: setting the ascension level on character select).
        if (string.IsNullOrEmpty(option) && ascension == null)
            return Error("Missing menu option", "missing_menu_option");

        var tree = (Engine.GetMainLoop()) as SceneTree;
        if (tree?.Root == null)
            return Error("Cannot access scene tree");

        var gameOverResult = TryExecuteGameOverMenuOption(tree.Root, option);
        if (gameOverResult != null)
            return gameOverResult;

        var ftueResult = TryExecuteFtueMenuOption(tree.Root, option);
        if (ftueResult != null)
            return ftueResult;

        var popupResult = TryExecutePopupMenuOption(tree.Root, option);
        if (popupResult != null)
            return popupResult;

        var runMenuResult = TryExecuteRunMenuOption(tree.Root, option);
        if (runMenuResult != null)
            return runMenuResult;

        // Timeline screen - advance through epoch reveals.
        var timelineScreen = FindFirst<NTimelineScreen>(tree.Root);
        if (timelineScreen != null && IsNodeVisible(timelineScreen))
            return ExecuteTimelineMenuOption(timelineScreen, tree.Root, option);

        var lobbyResult = TryExecuteLobbyScreenOption(tree.Root, option, seed, ascension);
        if (lobbyResult != null)
            return lobbyResult;

        // Main menu — click a menu button
        var mainMenu = FindFirst<NMainMenu>(tree.Root);
        if (mainMenu != null)
            return ExecuteMainMenuOption(tree.Root, mainMenu, option);

        return Error("Not on a menu screen", "not_on_menu");
    }

    /// <summary>
    /// The menu options of a game-over screen. Prefers the active overlay stack entry so
    /// hidden or preloaded game-over nodes cannot hijack normal menu navigation.
    /// </summary>
    private static Dictionary<string, object?>? TryExecuteGameOverMenuOption(Node root, string option)
    {
        var gameOver = NOverlayStack.Instance?.Peek() as NGameOverScreen;
        gameOver ??= FindAll<NGameOverScreen>(root).FirstOrDefault(IsNodeVisible);
        if (gameOver == null)
            return null;

        if (string.Equals(option, "continue", StringComparison.OrdinalIgnoreCase))
            return Error("Game-over option 'continue' is not actionable. Use: main_menu");

        if (!string.Equals(option, "main_menu", StringComparison.OrdinalIgnoreCase))
            return Error($"Unknown game over option: {option}. Use: main_menu");

        var result = ClickMenuButtonField(gameOver, "_mainMenuButton", "Returning to main menu");
        if (string.Equals((string?)result["status"], "ok", StringComparison.Ordinal))
            return result;

        var returnMethod = gameOver.GetType().GetMethod(
            "ReturnToMainMenu",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        if (returnMethod != null)
        {
            returnMethod.Invoke(gameOver, null);
            return Ok("Returning to main menu");
        }

        return Error("Game-over main_menu option is not available");
    }

    /// <summary>
    /// The tutorial/FTUE popups: the "Enable Tutorials?" dialog and any other popup with an
    /// advance control.
    /// </summary>
    private static Dictionary<string, object?>? TryExecuteFtueMenuOption(Node root, string option)
    {
        var tutorialFtue = FindVisibleAcceptTutorialsFtue(root);
        if (tutorialFtue != null && IsFtueNodeActive(tutorialFtue))
        {
            if (!string.Equals(option, "yes", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(option, "no", StringComparison.OrdinalIgnoreCase))
            {
                return Error($"Unknown tutorial prompt option: {option}. Use: yes, no");
            }

            var popup = tutorialFtue.GetType().GetField("_verticalPopup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tutorialFtue);
            if (popup != null)
            {
                var isYes = string.Equals(option, "yes", StringComparison.OrdinalIgnoreCase);
                var btnField = isYes ? "<YesButton>k__BackingField" : "<NoButton>k__BackingField";
                var btn = popup.GetType().GetField(btnField, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(popup);
                if (btn is NClickableControl clickable &&
                    IsPopupButtonActionable(clickable))
                {
                    clickable.ForceClick();
                    return Ok($"Tutorials: {(isYes ? "enabled" : "disabled")}");
                }
            }
            return Error("Tutorial popup visible but buttons not accessible");
        }

        // Any other FTUE/tutorial popup with a confirm button.
        var ftue = FindVisibleGenericFtue(root);
        if (ftue == null)
            return null;

        if (!string.Equals(option, "advance", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(option, "proceed", StringComparison.OrdinalIgnoreCase))
        {
            return Error($"Unknown tutorial option: {option}. Use: advance, proceed");
        }

        var ftueClickable = FindFtueAdvanceButton(ftue);
        if (ftueClickable != null)
        {
            ftueClickable.ForceClick();
            return Ok("Dismissed tutorial popup");
        }

        return Error("Tutorial popup visible but no actionable advance/proceed control is available; retry after the next state poll");
    }

    /// <summary>
    /// Generic blocking menu popups, such as the first-run warning that appears over
    /// character select on fresh profiles.
    /// </summary>
    private static Dictionary<string, object?>? TryExecutePopupMenuOption(Node root, string option)
    {
        var verticalPopup = FindVisibleVerticalPopup(root);
        if (verticalPopup != null)
            return ExecutePopupOption(GetPopupOptions(verticalPopup), option);

        var popupButtonOptions = GetVisiblePopupButtonOptions(root);
        return popupButtonOptions.Count > 0
            ? ExecutePopupOption(popupButtonOptions, option)
            : null;
    }

    /// <summary>
    /// The screens pushed over a run: settings first (so it keeps winning while it sits on
    /// top of the pause menu), then the pause menu and anything above it on the run's
    /// submenu stack (the compendium and the screens it opens).
    /// </summary>
    private static Dictionary<string, object?>? TryExecuteRunMenuOption(Node root, string option)
    {
        // Settings screen - checked before every screen below, and in particular before
        // the main-menu branch, which is gated on NMainMenu and so never runs during a
        // run. Without this the API could open settings ('settings' on the main menu, or
        // the pause menu in a run) and had no way back out.
        var settingsScreen = FindFirst<NSettingsScreen>(root);
        if (settingsScreen != null && IsNodeVisible(settingsScreen))
            return ExecuteSubmenuBackOption(settingsScreen, option);

        var runSubmenu = GetOpenRunSubmenu(root);
        if (runSubmenu is NPauseMenu pauseMenu)
            return ExecutePauseMenuOption(pauseMenu, option);
        return runSubmenu != null ? ExecuteSubmenuBackOption(runSubmenu, option) : null;
    }

    private static Dictionary<string, object?> ExecuteTimelineMenuOption(
        NTimelineScreen timelineScreen, Node root, string option)
    {
        if (string.Equals(option, "advance", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option, "proceed", StringComparison.OrdinalIgnoreCase))
        {
            return AdvanceTimeline(timelineScreen, root);
        }

        if (string.Equals(option, "back", StringComparison.OrdinalIgnoreCase))
            return GoBackFromTimeline(timelineScreen);

        return Error($"Unknown timeline option: {option}. Use: advance, back");
    }

    /// <summary>One "advance" step: open panels first, then the queued unlocks, then a reveal.</summary>
    private static Dictionary<string, object?> AdvanceTimeline(NTimelineScreen timelineScreen, Node root)
    {
        var tutorialResult = TryClickTimelineTutorial(root);
        if (tutorialResult != null)
            return tutorialResult;

        var buttonResult = TryClickTimelineAdvanceButton(root);
        if (buttonResult != null)
            return buttonResult;

        var inspectResult = TryCloseEpochInspectScreen(root);
        if (inspectResult != null)
            return inspectResult;

        // An unlock screen that is already open ("<character> is now playable",
        // "you unlocked these cards") blocks everything behind it until its
        // confirm button is pressed.
        var openUnlockResult = TryConfirmOpenUnlockScreen(timelineScreen);
        if (openUnlockResult != null)
            return openUnlockResult;

        var queuedUnlockResult = TryHandleQueuedTimelineUnlock(timelineScreen);
        if (queuedUnlockResult != null)
            return queuedUnlockResult;

        var revealResult = TryRevealOneEpochSlot(timelineScreen);
        if (revealResult != null)
            return revealResult;

        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = "No more epochs to advance", ["done"] = true };
    }

    /// <summary>Timeline tutorial screen (first-time "Proceed" button), when it is up.</summary>
    private static Dictionary<string, object?>? TryClickTimelineTutorial(Node root)
    {
        var tutorial = FindFirst<NTimelineTutorial>(root);
        if (tutorial == null || !IsNodeVisible(tutorial))
            return null;

        var ackBtn = tutorial.GetType().GetField("_acknowledgeButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tutorial);
        if (ackBtn is NClickableControl ackClickable &&
            ackClickable.IsEnabled &&
            IsNodeVisible(ackClickable))
        {
            ackClickable.ForceClick();
            return Ok("Clicked tutorial proceed button");
        }
        return null;
    }

    /// <summary>The first enabled, visible confirm or proceed button in the tree.</summary>
    private static Dictionary<string, object?>? TryClickTimelineAdvanceButton(Node root)
    {
        var confirmBtn = FindFirst<NConfirmButton>(root);
        if (confirmBtn != null && IsNodeVisible(confirmBtn) && confirmBtn.IsEnabled)
        {
            confirmBtn.ForceClick();
            return Ok("Clicked confirm button");
        }

        var proceedBtn = FindFirst<NProceedButton>(root);
        if (proceedBtn != null && IsNodeVisible(proceedBtn) && proceedBtn.IsEnabled)
        {
            proceedBtn.ForceClick();
            return Ok("Clicked proceed button");
        }
        return null;
    }

    /// <summary>The epoch detail view, closed through its button when that is available.</summary>
    private static Dictionary<string, object?>? TryCloseEpochInspectScreen(Node root)
    {
        var inspectScreen = FindFirst<NEpochInspectScreen>(root);
        if (inspectScreen == null || !IsNodeVisible(inspectScreen))
            return null;

        var closeBtn = inspectScreen.GetType().GetField("_closeButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(inspectScreen);
        if (closeBtn is NClickableControl closeClickable &&
            closeClickable.IsEnabled &&
            IsNodeVisible(closeClickable))
        {
            closeClickable.ForceClick();
            return Ok("Closed epoch inspect screen");
        }
        inspectScreen.Close();
        return Ok("Closed epoch inspect screen");
    }

    private static Dictionary<string, object?> GoBackFromTimeline(NTimelineScreen timelineScreen)
    {
        // Try NBackButton directly on NTimelineScreen
        var backBtn = timelineScreen.GetType().GetField("_backButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(timelineScreen);
        if (backBtn is NClickableControl { IsEnabled: true } backClickable)
        {
            backClickable.ForceClick();
            return Ok("Going back from timeline");
        }

        // Try the submenu stack
        var stack = timelineScreen.GetType().GetField("_stack", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(timelineScreen);
        if (stack != null)
        {
            var popMethod = stack.GetType().GetMethod("Pop");
            popMethod?.Invoke(stack, null);
            return Ok("Popped timeline from stack");
        }
        return Error("Back button not available on timeline");
    }

    /// <summary>
    /// The lobby screens: friend join, multiplayer load, custom run, character select and
    /// profile select. Character select can outlive or be mounted separately from NMainMenu,
    /// so it is handled before the main-menu routing.
    /// </summary>
    private static Dictionary<string, object?>? TryExecuteLobbyScreenOption(
        Node root, string option, string? seed, int? ascension)
    {
        // Multiplayer Join Friend screen — friend list, refresh, back, or join_<index>/<player_id>.
        var joinScreen = FindFirst<NJoinFriendScreen>(root);
        if (joinScreen != null && IsNodeVisible(joinScreen))
            return ExecuteJoinScreenMenuOption(joinScreen, option);

        // Multiplayer Load Game lobby — confirm/embark/unready/back to resume saved MP run.
        var loadLobby = FindFirst<NMultiplayerLoadGameScreen>(root);
        if (loadLobby != null && IsNodeVisible(loadLobby))
            return ExecuteLoadLobbyMenuOption(loadLobby, option);

        // Custom mode setup screen (replaces char select for custom runs). Handle
        // it before the plain char select and before main-menu routing.
        var customRun = FindFirst<NCustomRunScreen>(root);
        if (customRun != null && IsNodeVisible(customRun))
            return ExecuteCustomRunMenuOption(customRun, option, seed);

        var charSelect = FindFirst<NCharacterSelectScreen>(root);
        if (charSelect != null && IsNodeVisible(charSelect))
            return ExecuteCharacterSelectMenuOption(charSelect, option, seed, ascension);

        var profileScreen = FindFirst<NProfileScreen>(root);
        return profileScreen != null && IsNodeVisible(profileScreen)
            ? ExecuteProfileSelectMenuOption(profileScreen, option)
            : null;
    }

    private static Dictionary<string, object?> ExecuteMainMenuOption(
        Node root, NMainMenu mainMenu, string option)
    {
        // Check if we're on singleplayer submenu
        var spSubmenu = FindFirst<NSingleplayerSubmenu>(root);
        if (spSubmenu != null && IsNodeVisible(spSubmenu))
            return ExecuteGameModeSubmenuOption(spSubmenu, option, "singleplayer");

        // Check if we're on multiplayer host submenu
        var mpHostSubmenu = FindFirst<NMultiplayerHostSubmenu>(root);
        if (mpHostSubmenu != null && IsNodeVisible(mpHostSubmenu))
            return ExecuteGameModeSubmenuOption(mpHostSubmenu, option, "multiplayer host");

        // Check if we're on multiplayer submenu
        var mpSubmenu = FindFirst<NMultiplayerSubmenu>(root);
        if (mpSubmenu != null && IsNodeVisible(mpSubmenu))
            return ExecuteMultiplayerSubmenuOption(mpSubmenu, option);

        // Main menu buttons
        var menuFieldName = option.ToLowerInvariant() switch
        {
            "singleplayer" => "_singleplayerButton",
            "multiplayer" => "_multiplayerButton",
            "compendium" => "_compendiumButton",
            "timeline" => "_timelineButton",
            "settings" => "_settingsButton",
            "continue" => "_continueButton",
            "abandon_run" => "_abandonRunButton",
            "quit" => "_quitButton",
            _ => null
        };
        if (menuFieldName == null)
            return Error($"Unknown menu option: {option}", "unknown_menu_option");

        return ClickMenuButtonField(mainMenu, menuFieldName, $"Selected {option}", $"Option '{option}' is not available");
    }

    /// <summary>The standard/daily/custom buttons shared by both game-mode submenus.</summary>
    private static Dictionary<string, object?> ExecuteGameModeSubmenuOption(
        Node submenu, string option, string screenLabel)
    {
        if (string.Equals(option, "back", StringComparison.OrdinalIgnoreCase))
            return ClickMenuButtonField(submenu, "_backButton", "Going back", "Back button is not available");

        var fieldName = option.ToLowerInvariant() switch
        {
            "standard" => "_standardButton",
            "daily" => "_dailyButton",
            "custom" => "_customButton",
            _ => null
        };
        if (fieldName == null)
            return Error($"Unknown {screenLabel} option: {option}. Use: standard, daily, custom, back");

        return ClickMenuButtonField(submenu, fieldName, $"Selected {option}", $"Option '{option}' is not available (locked)");
    }

    private static Dictionary<string, object?> ExecuteMultiplayerSubmenuOption(
        Node mpSubmenu, string option)
    {
        if (string.Equals(option, "back", StringComparison.OrdinalIgnoreCase))
            return ClickMenuButtonField(mpSubmenu, "_backButton", "Going back", "Back button is not available");

        var fieldName = option.ToLowerInvariant() switch
        {
            "host" => "_hostButton",
            "join" => "_joinButton",
            "load" => "_loadButton",
            "abandon" => "_abandonButton",
            _ => null
        };
        if (fieldName == null)
            return Error($"Unknown multiplayer option: {option}. Use: host, join, load, abandon, back");

        return ClickMenuButtonField(mpSubmenu, fieldName, $"Selected {option}", $"Option '{option}' is not available");
    }


    private static Dictionary<string, object?> ExecutePopupOption(
        List<(string Name, NClickableControl Button)> options,
        string option)
    {
        foreach (var candidate in options)
        {
            if (!string.Equals(candidate.Name, option, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsPopupButtonActionable(candidate.Button))
                return Error($"Popup option '{candidate.Name}' is disabled");

            candidate.Button.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = $"Selected popup option: {candidate.Name}"
            };
        }

        return Error($"Popup option '{option}' is not actionable. Use: {string.Join(", ", options.Select(candidate => candidate.Name))}");
    }

    private static Dictionary<string, object?> ExecuteProfileSelectMenuOption(
        NProfileScreen profileScreen,
        string option)
    {
        if (string.Equals(option, "back", StringComparison.OrdinalIgnoreCase))
        {
            var backBtn = GetInstanceFieldValue(profileScreen, "_backButton")
                ?? FindFirst<NBackButton>(profileScreen);
            if (backBtn is NClickableControl backClickable &&
                backClickable.IsEnabled &&
                IsNodeVisible(backClickable))
            {
                backClickable.ForceClick();
                return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = "Going back from profile select" };
            }
            return Error("Back button not available on profile select");
        }

        var normalized = option.ToLowerInvariant();
        if (normalized.StartsWith("profile_", StringComparison.Ordinal))
            normalized = normalized["profile_".Length..];
        else if (normalized.StartsWith("slot_", StringComparison.Ordinal))
            normalized = normalized["slot_".Length..];

        if (int.TryParse(normalized, CultureInfo.InvariantCulture, out var profileId))
            return ExecuteProfileAction("switch", profileId);

        return Error($"Unknown profile select option: {option}. Use: profile_1, profile_2, profile_3, back");
    }

    /// <summary>
    /// Leaves any screen sitting on a submenu stack: the settings screen, and whatever
    /// the in-run pause menu opens (the compendium and its sub-screens).
    ///
    /// Only 'back' is offered. Driving those screens (settings tabs, tickboxes, sliders,
    /// compendium pages) is deliberately not implemented - the bug being fixed is that
    /// the API could enter a screen and not leave it.
    /// </summary>
    private static Dictionary<string, object?> ExecuteSubmenuBackOption(NSubmenu submenu, string option)
    {
        var screen = SubmenuScreenName(submenu);
        var normalized = option.ToLowerInvariant();
        if (!string.Equals(normalized, "back", StringComparison.Ordinal) && !string.Equals(normalized, "close", StringComparison.Ordinal) && !string.Equals(normalized, "resume", StringComparison.Ordinal))
            return Error($"Unknown {screen} option: {option}. Use: back");

        var backButton = GetSubmenuBackButton(submenu);
        if (backButton != null && backButton.IsEnabled && IsNodeVisible(backButton))
        {
            backButton.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = $"Left the {screen} screen" };
        }

        // The back button is hidden or disabled while the submenu stack animates, which
        // would otherwise leave the caller stuck on a screen with no other exit. Popping
        // the stack is what the button itself does, so this lands in the same place.
        var popped = TryRead<Dictionary<string, object?>?>("menu.submenu.pop", () =>
        {
            var stack = GetInstanceFieldValue(submenu, "_stack");
            var popMethod = stack?.GetType().GetMethod("Pop");
            if (stack == null || popMethod == null)
                return null;

            popMethod.Invoke(stack, null);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = $"Left the {screen} screen (popped the submenu stack)"
            };
        });
        if (popped != null)
            return popped;

        return Error($"The {screen} screen is open but neither its back button nor its submenu stack could close it");
    }

    /// <summary>
    /// The pause menu's own options.
    ///
    /// 'give_up' is deliberately not confirmed here: NPauseMenu.OnGiveUpButtonPressed
    /// puts an NAbandonRunConfirmPopup in the modal container, which the next state
    /// reports as menu_screen "popup" with yes/no. Abandoning a run is irreversible, so
    /// the caller has to say yes to it explicitly.
    /// </summary>
    private static Dictionary<string, object?> ExecutePauseMenuOption(NPauseMenu pauseMenu, string option)
    {
        var normalized = option.ToLowerInvariant();
        string? field = normalized switch
        {
            "resume" or "back" or "close" or "continue" => "_resumeButton",
            "settings" => "_settingsButton",
            "compendium" => "_compendiumButton",
            "give_up" or "giveup" or "abandon" or "abandon_run" => "_giveUpButton",
            "disconnect" => "_disconnectButton",
            "save_and_quit" or "main_menu" or "quit" => "_saveAndQuitButton",
            _ => null
        };

        if (field == null)
        {
            var available = BuildPauseMenuOptions(pauseMenu)
                .Select(entry => entry.GetValueOrDefault("name")?.ToString())
                .Where(name => !string.IsNullOrEmpty(name));
            return Error($"Unknown pause menu option: {option}. Use: {string.Join(", ", available)}");
        }

        var message = field switch
        {
            "_resumeButton" => "Resumed the run",
            "_settingsButton" => "Opened settings. Use menu_select option 'back' to return to the pause menu.",
            "_compendiumButton" => "Opened the compendium. Use menu_select option 'back' to return to the pause menu.",
            "_giveUpButton" => "Opened the give-up confirmation. Answer it with menu_select option 'yes' "
                               + "(this abandons the run permanently) or 'no'.",
            "_disconnectButton" => "Disconnecting from the multiplayer run",
            _ => "Saving and returning to the main menu. The run can be resumed there with menu_select option 'continue'."
        };

        return ClickMenuButtonField(
            pauseMenu,
            field,
            message,
            $"Pause menu option '{normalized}' is not available in this run");
    }

    /// <summary>
    /// Opens the in-run pause menu the way the top-bar gear does.
    ///
    /// Without it there was no API way into the pause menu and therefore no API way out
    /// of a run at all: 'abandon_run' is a main-menu button and 'main_menu' only exists
    /// on the game-over screen, so an unattended agent that got stuck needed a mouse.
    /// </summary>
    private static Dictionary<string, object?> ExecuteOpenPauseMenu(IRunState runState)
    {
        var tree = Engine.GetMainLoop() as SceneTree;

        var open = GetOpenRunSubmenu(tree?.Root);
        if (open is NPauseMenu)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["already_open"] = true,
                ["message"] = "Pause menu is already open."
            };
        }

        if (open != null)
        {
            // Clicking the gear now would toggle the whole capstone shut and resume the
            // run - the opposite of what was asked for.
            return Error(
                $"The {SubmenuScreenName(open)} screen is already open over the run. "
                + "Use menu_select option 'back' to return to the pause menu.");
        }

        // A card still in flight resolves against the room that is about to be covered.
        // No hand outside combat is the normal case, so this read is quiet.
        if (TryReadQuiet<bool?>(() => NPlayerHand.Instance is { InCardPlay: true }) == true)
            return Error("A card is still being played. Wait for is_resolving to clear, then pause.");

        var pauseButton = SafeGetTopBarPauseButton(tree?.Root);
        if (pauseButton != null)
        {
            TryDo("menu.pause.force_click", pauseButton.ForceClick);
            if (GetOpenRunSubmenu(tree?.Root) is NPauseMenu)
                return OpenedPauseMenuResult();
        }

        // Fallback for a top bar that is hidden or not yet initialized: the same two
        // calls NTopBarPauseButton.OnRelease makes.
        Dictionary<string, object?>? opened = null;
        var openFailure = TryAction("menu.pause.open_direct", () =>
        {
            var stack = MegaCrit.Sts2.Core.Nodes.NRun.Instance?.GlobalUi?.SubmenuStack;
            if (stack?.ShowScreen(CapstoneSubmenuType.PauseMenu) is NPauseMenu menu)
            {
                menu.Initialize(runState);
                opened = OpenedPauseMenuResult();
            }
        });
        if (opened != null)
            return opened;
        if (openFailure != null)
            return Error($"Could not open the pause menu: {openFailure}");

        return Error("Could not open the pause menu: neither the top-bar pause button nor the run's submenu stack is available");
    }

    private static Dictionary<string, object?> OpenedPauseMenuResult()
        => new(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["already_open"] = false,
            ["message"] = "Opened the pause menu. The run is paused; use menu_select to pick an option."
        };

    /// <summary>
    /// Prefers the live top bar's own button so the click follows exactly the path a
    /// human takes; the tree search covers a top bar that has not been wired up yet.
    /// </summary>
    private static NTopBarPauseButton? SafeGetTopBarPauseButton(Node? root)
    {
        // No top bar before the run scene is up, which is normal; only the tree search
        // can find the button then, so the read stays quiet.
        var button = TryReadQuiet(() =>
            MegaCrit.Sts2.Core.Nodes.NRun.Instance?.GlobalUi?.TopBar?.Pause);
        if (button != null && IsLiveNode(button))
            return button;

        return root == null ? null : FindFirst<NTopBarPauseButton>(root);
    }

    /// <summary>
    /// The pause menu and the screens reachable from it cover the room with a capstone
    /// screen the room knows nothing about, so a gameplay action sent while one is up
    /// lands on a screen the player cannot see (A-5). menu_select drives whatever is
    /// visible; everything else waits until it is closed.
    /// </summary>
    private static Dictionary<string, object?>? RunSubmenuBlockingError(SceneTree? tree, string action)
    {
        if (action is "open_pause_menu" or "pause")
            return null;

        var submenu = GetOpenRunSubmenu(tree?.Root);
        if (submenu == null)
            return null;

        var screen = submenu is NPauseMenu ? "pause menu" : $"{SubmenuScreenName(submenu)} screen";
        return Error(
            $"The {screen} is open over the run. Close it with menu_select "
            + "(option 'resume' on the pause menu, 'back' elsewhere) before sending gameplay actions.");
    }

    private static Dictionary<string, object?> ExecuteJoinScreenMenuOption(
        NJoinFriendScreen joinScreen,
        string option)
    {
        var normalized = option.ToLowerInvariant();

        if (string.Equals(normalized, "back", StringComparison.Ordinal))
            return ClickMenuButtonField(joinScreen, "_backButton", "Going back from join screen", "Back button is not available");

        if (string.Equals(normalized, "refresh", StringComparison.Ordinal))
            return RefreshJoinFriendList(joinScreen);

        // join_<index> or join_<player_id>
        if (normalized.StartsWith("join_", StringComparison.Ordinal))
            return JoinFriend(joinScreen, normalized["join_".Length..]);

        return Error($"Unknown join screen option: {option}. Use: refresh, back, join_<index>, join_<player_id>");
    }

    private static Dictionary<string, object?> RefreshJoinFriendList(NJoinFriendScreen joinScreen)
    {
        var refreshBtn = GetInstanceFieldValue(joinScreen, "_refreshButton") as NClickableControl;
        if (refreshBtn == null || !refreshBtn.IsEnabled || !IsNodeVisible(refreshBtn))
            return Error("Refresh button not available");
        refreshBtn.ForceClick();
        return Ok("Refreshing friend list");
    }

    private static Dictionary<string, object?> JoinFriend(NJoinFriendScreen joinScreen, string key)
    {
        var buttonContainer = GetInstanceFieldValue(joinScreen, "_buttonContainer") as Control;
        if (buttonContainer == null)
            return Error("Friend list container not available");

        // Build the in-order friend list once
        var friendButtons = new List<NJoinFriendButton>();
        foreach (var child in buttonContainer.GetChildren())
        {
            if (child is NJoinFriendButton fbtn)
                friendButtons.Add(fbtn);
        }

        if (friendButtons.Count == 0)
            return Error("No friends with open lobbies. Use 'refresh' to retry.");

        // Try as index first
        if (int.TryParse(key, CultureInfo.InvariantCulture, out int idx))
        {
            if (idx < 0 || idx >= friendButtons.Count)
                return Error($"Friend index {idx} out of range (0..{friendButtons.Count - 1})");
            if (!friendButtons[idx].IsEnabled)
                return Error($"Friend at index {idx} is not joinable right now");
            friendButtons[idx].ForceClick();
            return Ok($"Joining friend at index {idx}");
        }

        // Otherwise treat as ulong player id
        if (ulong.TryParse(key, out ulong playerId))
        {
            var match = friendButtons.FirstOrDefault(b => b.PlayerId == playerId);
            if (match == null)
                return Error($"No friend with player_id {playerId} in current list. Available indices: 0..{friendButtons.Count - 1}");
            if (!match.IsEnabled)
                return Error($"Friend {playerId} is not joinable right now");
            match.ForceClick();
            return Ok($"Joining friend {playerId}");
        }

        return Error($"Unknown join target: '{key}'. Use join_<index> (e.g. join_0) or join_<player_id>.");
    }

    private static Dictionary<string, object?> ExecuteLoadLobbyMenuOption(
        NMultiplayerLoadGameScreen loadLobby,
        string option)
    {
        var normalized = option.ToLowerInvariant();

        if (string.Equals(normalized, "confirm", StringComparison.Ordinal) || string.Equals(normalized, "embark", StringComparison.Ordinal) || string.Equals(normalized, "ready", StringComparison.Ordinal))
        {
            var confirmBtn = GetInstanceFieldValue(loadLobby, "_confirmButton") as NClickableControl;
            if (confirmBtn != null && confirmBtn.IsEnabled && IsNodeVisible(confirmBtn))
            {
                confirmBtn.ForceClick();
                return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = "Readied up to load saved MP run" };
            }
            return Error("Confirm button not available — already ready, or lobby not yet initialized");
        }

        if (string.Equals(normalized, "unready", StringComparison.Ordinal))
        {
            var unreadyBtn = GetInstanceFieldValue(loadLobby, "_unreadyButton") as NClickableControl;
            if (unreadyBtn != null && unreadyBtn.IsEnabled && IsNodeVisible(unreadyBtn))
            {
                unreadyBtn.ForceClick();
                return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = "Retracted ready vote" };
            }
            return Error("Unready button not available — you have not confirmed yet");
        }

        if (string.Equals(normalized, "back", StringComparison.Ordinal))
        {
            return ClickMenuButtonField(loadLobby, "_backButton", "Going back from load lobby", "Back button is not available");
        }

        return Error($"Unknown load lobby option: {option}. Use: confirm, embark, unready, back");
    }

    // Option-name prefix for a custom-run run modifier toggle: "modifier_<id>".
    // The state advertises modifiers under their bare id plus this option name.
    internal const string ModifierOptionPrefix = "modifier_";

    /// <summary>
    /// Sets the ascension level on the character select screen.
    /// SetAscensionLevel emits AscensionLevelChanged, which is what
    /// NCharacterSelectScreen listens to before calling StartRunLobby.SyncAscensionChange —
    /// i.e. the same path the on-screen arrows take.
    /// </summary>
    private static Dictionary<string, object?> ApplyAscension(NCharacterSelectScreen charSelect, int ascension)
    {
        var panel = GetInstanceFieldValue(charSelect, "_ascensionPanel") as NAscensionPanel;
        if (panel == null)
            return Error("Ascension panel is not available on this screen");

        int maxAscension = GetInstanceFieldValue(panel, "_maxAscension") is int max ? max : 0;
        if (ascension < 0 || ascension > maxAscension)
            return Error($"Ascension {ascension} out of range (0-{maxAscension} unlocked for the selected character)");

        panel.SetAscensionLevel(ascension);

        int applied = panel.Ascension;
        if (applied != ascension)
            return Error($"Ascension did not take: asked for {ascension}, panel reports {applied}");

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Ascension set to {applied}",
            ["ascension"] = applied,
            ["max_ascension"] = maxAscension
        };
    }

    private static Dictionary<string, object?> ExecuteCharacterSelectMenuOption(
        NCharacterSelectScreen charSelect,
        string option,
        string? seed,
        int? ascension = null)
    {
        return ExecuteStartRunScreenOption(
            charSelect,
            "_embarkButton",
            option,
            seed,
            runNoun: "run",
            noLobbySeedError: "Seeded embark is not supported for standard singleplayer from this API. "
                              + "Seed was not applied and the run was not started.",
            ascension: ascension);
    }

    // Custom mode setup. Same screen shape as character select — the confirm control is
    // _confirmButton instead of _embarkButton, and run-modifier tickboxes are present.
    private static Dictionary<string, object?> ExecuteCustomRunMenuOption(
        NCustomRunScreen customRun,
        string option,
        string? seed)
    {
        return ExecuteStartRunScreenOption(
            customRun,
            "_confirmButton",
            option,
            seed,
            runNoun: "custom run",
            noLobbySeedError: "Seed could not be applied (no lobby on this custom run screen); run was not started.");
    }

    // Shared dispatch for the two "start a run" screens (NCharacterSelectScreen and
    // NCustomRunScreen). They are unrelated types but expose the same controls under the
    // same field names, and everything here goes through GetInstanceFieldValue anyway, so
    // only the confirm button's field name and the wording of a couple of messages differ.
    // Options that belong to one screen only (modifier toggles) simply find nothing on
    // the other and fall through to the character lookup.
    private static Dictionary<string, object?> ExecuteStartRunScreenOption(
        Node screen,
        string confirmField,
        string option,
        string? seed,
        string runNoun,
        string noLobbySeedError,
        int? ascension = null)
    {
        // "ascension" is applied before anything else so it can accompany either the
        // character pick or the confirm, and can also be sent on its own. Only the
        // character-select screen has an ascension panel; the custom-run screen has none.
        if (ascension != null)
        {
            var ascensionResult = ApplyAscensionOption(screen, option, ascension.Value);
            if (ascensionResult != null)
                return ascensionResult;
        }

        if (string.Equals(option, "back", StringComparison.OrdinalIgnoreCase))
            return GoBackFromLobby(screen);
        if (string.Equals(option, "unready", StringComparison.OrdinalIgnoreCase))
            return RetractReadyVote(screen);
        if (string.Equals(option, "confirm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option, "embark", StringComparison.OrdinalIgnoreCase))
        {
            return EmbarkStartRun(screen, confirmField, seed, runNoun, noLobbySeedError);
        }

        // While a pending-unlock animation owns the screen, NCharacterSelectScreen.
        // PlayUnlockCharacterAnimation will Select() the newly unlocked character
        // ~1s after the screen opens, overwriting any selection made before it
        // finishes. Refuse (retryable) instead of racing it.
        if (IsCharacterSelectionBusy())
            return Error("Character selection is busy (unlock animation in progress); retry in a moment.");

        if (string.Equals(option, "ascension_up", StringComparison.OrdinalIgnoreCase))
            return ExecuteAscensionChange(screen, up: true);
        if (string.Equals(option, "ascension_down", StringComparison.OrdinalIgnoreCase))
            return ExecuteAscensionChange(screen, up: false);

        // Run-modifier toggle. Accepts both the advertised "modifier_<id>" option name and
        // the bare id the state reports under modifiers[].id.
        var modifierResult = TryToggleRunModifier(screen, option);
        if (modifierResult != null)
            return modifierResult;

        return SelectCharacterOption(screen, option);
    }

    /// <summary>
    /// Applies the ascension level. Returns the answer to send, or null when the caller
    /// should carry on with the requested option.
    /// </summary>
    private static Dictionary<string, object?>? ApplyAscensionOption(
        Node screen, string option, int ascension)
    {
        if (screen is not NCharacterSelectScreen ascensionScreen)
            return Error("Ascension can only be set on the character select screen");

        var ascensionResult = ApplyAscension(ascensionScreen, ascension);
        if (string.Equals((string?)ascensionResult["status"], "error", StringComparison.Ordinal))
            return ascensionResult;
        return string.IsNullOrEmpty(option) ? ascensionResult : null;
    }

    /// <summary>
    /// Leaves the lobby. _backButton leaves the lobby (and disconnects in MP).
    /// _unreadyButton is a separate control that only becomes enabled after the player has
    /// hit confirm/embark in MP — it retracts the ready vote without leaving. Surface them
    /// as distinct options so callers can pick deliberately. Fall back to unready when only
    /// it is actionable so older callers don't get stuck.
    /// </summary>
    private static Dictionary<string, object?> GoBackFromLobby(Node screen)
    {
        var backBtn = GetInstanceFieldValue(screen, "_backButton") as NClickableControl;
        if (backBtn != null && backBtn.IsEnabled && IsControlVisibleOrActionable(backBtn))
        {
            backBtn.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = "Going back" };
        }
        var unreadyFallback = GetInstanceFieldValue(screen, "_unreadyButton") as NClickableControl;
        if (unreadyFallback != null && unreadyFallback.IsEnabled && IsControlVisibleOrActionable(unreadyFallback))
        {
            unreadyFallback.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = "Retracted ready vote (back fell through to unready)" };
        }
        return Error("Back button not available");
    }

    private static Dictionary<string, object?> RetractReadyVote(Node screen)
    {
        var unreadyBtn = GetInstanceFieldValue(screen, "_unreadyButton") as NClickableControl;
        if (unreadyBtn != null && unreadyBtn.IsEnabled && IsControlVisibleOrActionable(unreadyBtn))
        {
            unreadyBtn.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = "Retracted ready vote" };
        }
        return Error("Unready button not available — you have not confirmed yet");
    }

    /// <summary>Sets the seed, if one was given, and presses confirm.</summary>
    private static Dictionary<string, object?> EmbarkStartRun(
        Node screen, string confirmField, string? seed, string runNoun, string noLobbySeedError)
    {
        if (!string.IsNullOrWhiteSpace(seed))
        {
            seed = seed.Trim();
            var lobby = GetInstanceFieldValue(screen, "_lobby") as StartRunLobby;
            if (lobby == null)
                return Error(noLobbySeedError);

            var seedFailure = TryAction("start_run.set_seed", () => lobby.SetSeed(seed));
            if (seedFailure != null)
                return Error($"Seeded embark failed before starting the run: {seedFailure}");
        }

        var confirmBtn = GetInstanceFieldValue(screen, confirmField);
        if (confirmBtn is NClickableControl confirmClickable && confirmClickable.IsEnabled)
        {
            var msg = string.IsNullOrEmpty(seed)
                ? $"Embarking on {runNoun}"
                : $"Embarking on {runNoun} (seed: {seed})";

            // Report the level the run will actually start at, read back from the panel.
            int? startingAscension = screen is NCharacterSelectScreen embarkScreen
                ? (GetInstanceFieldValue(embarkScreen, "_ascensionPanel") as NAscensionPanel)?.Ascension
                : null;
            if (startingAscension != null)
                msg += $" at ascension {startingAscension}";

            confirmClickable.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = msg,
                ["ascension"] = startingAscension
            };
        }
        return Error("Confirm button not available — select a character first");
    }

    private static Dictionary<string, object?> SelectCharacterOption(Node screen, string option)
    {
        var buttons = FindAll<NCharacterSelectButton>(screen);
        foreach (var btn in buttons)
        {
            if (btn.Character != null && (
                string.Equals(btn.Character.Id.Entry, option, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SafeGetText(() => btn.Character.Title), option, StringComparison.OrdinalIgnoreCase)))
            {
                if (btn.IsLocked)
                    return Error($"Character '{option}' is locked");
                // NCharacterSelectButton.Select() no-ops while the button still
                // thinks it is selected — and the unlock animation steals selection
                // without deselecting other buttons. Clear the flag first so the
                // full commit path always runs (delegate -> screen.SelectCharacter
                // -> lobby.SetLocalCharacter, which is what embark reads).
                btn.Deselect();
                btn.Select();
                return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = $"Selected {SafeGetText(() => btn.Character.Title)}. Use 'confirm' to embark." };
            }
        }
        return Error($"Character '{option}' not found. Available: {string.Join(", ", buttons.Where(b => !b.IsLocked).Select(b => b.Character?.Id.Entry))}");
    }

    internal static bool IsCharacterSelectionBusy()
    {
        return TryRead<bool?>(() =>
        {
            var progress = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.Progress;
            return progress != null &&
                   progress.PendingCharacterUnlock != MegaCrit.Sts2.Core.Models.ModelId.none;
        }) ?? false;
    }

    /// <summary>Set the Godot engine time scale (animation/game speed). Valid any time.</summary>
    internal static Dictionary<string, object?> ExecuteSetTimeScale(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("scale", out var scaleElem))
            return Error("Missing 'scale'");
        var parsedScale = TryRead<double?>("time_scale.scale", () => scaleElem.GetDouble());
        if (parsedScale == null)
            return Error("'scale' must be a number");
        double scale = parsedScale.Value;
        if (scale is < 0.25 or > 10.0)
            return Error($"'scale' {scale} out of range (0.25 - 10.0)");
        Godot.Engine.TimeScale = scale;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Engine time scale set to {scale:0.##}x",
            ["time_scale"] = scale
        };
    }

    /// <summary>Set the run ascension level on the (open) character select screen.
    /// The panel's AscensionLevelChanged signal makes the screen sync the lobby,
    /// which is what embark reads — same path as clicking the arrows.</summary>
    internal static Dictionary<string, object?> ExecuteSetAscension(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("level", out var levelElem))
            return Error("Missing 'level'");
        var parsedLevel = TryRead<int?>("ascension.level", () => levelElem.GetInt32());
        if (parsedLevel == null)
            return Error("'level' must be an integer");
        int level = parsedLevel.Value;
        if (level < 0)
            return Error("'level' must be >= 0");

        var tree = Godot.Engine.GetMainLoop() as SceneTree;
        var charSelect = tree?.Root != null ? FindFirst<NCharacterSelectScreen>(tree.Root) : null;
        if (charSelect == null || !IsNodeVisible(charSelect))
            return Error("Character select screen is not open");
        if (IsCharacterSelectionBusy())
            return Error("Character selection is busy (unlock animation in progress); retry in a moment.");

        if (GetInstanceFieldValue(charSelect, "_ascensionPanel") is not NAscensionPanel panel)
            return Error("Ascension panel not found");

        int maxAscension = 0;
        if (GetInstanceFieldValue(panel, "_maxAscension") is int max)
            maxAscension = max;
        if (level > maxAscension)
            return Error(
                $"Ascension {level} is not unlocked (max {maxAscension} for the selected " +
                "character). Select the character first; the cap is per-character.");

        panel.SetAscensionLevel(level);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Ascension set to {level} (max unlocked: {maxAscension})",
            ["ascension"] = level,
            ["max_ascension"] = maxAscension
        };
    }

    // Clicks one of the ascension panel's arrows. The game hides an arrow when that
    // direction is unavailable (either end of the range, or an MP client that may not
    // change the level), so an invisible arrow is a hard "no", not a transient state.
    private static Dictionary<string, object?> ExecuteAscensionChange(Node screen, bool up)
    {
        var panel = GetInstanceFieldValue(screen, "_ascensionPanel") as NAscensionPanel;
        if (panel == null || !IsNodeVisible(panel))
            return Error("Ascension is not adjustable on this screen");

        var arrow = GetInstanceFieldValue(panel, up ? "_rightArrow" : "_leftArrow") as NClickableControl;
        if (arrow == null || !IsControlVisibleOrActionable(arrow))
        {
            return Error(up
                ? $"Cannot raise ascension past {panel.Ascension}"
                : $"Cannot lower ascension below {panel.Ascension}");
        }

        arrow.ForceClick();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Ascension set to {panel.Ascension}",
            ["ascension"] = panel.Ascension
        };
    }

    // Returns null when `option` does not name a run modifier on this screen, so the
    // caller can keep looking (character names, etc.).
    //
    // Selection accepts the advertised "modifier_<key>" option name, the bare key, or the
    // raw modifier id when that id is unambiguous. It is ambiguous for the per-character
    // "character cards" modifiers, which all share one id — those must be addressed by key.
    //
    // Modifiers can also be mutually exclusive: ticking one makes the game untick others in
    // the same group (NCustomRunModifiersList.UntickMutuallyExclusiveModifiersForTickbox).
    // That happens synchronously inside ForceClick, so the response reports the full set of
    // modifiers ticked afterwards — the caller must not assume its own toggle was the only
    // change.
    private static Dictionary<string, object?>? TryToggleRunModifier(Node screen, string option)
    {
        var entries = GetRunModifierEntries(screen);
        if (entries.Count == 0)
            return null;

        bool explicitlyAModifier = option.StartsWith(ModifierOptionPrefix, StringComparison.OrdinalIgnoreCase);
        var token = explicitlyAModifier ? option.Substring(ModifierOptionPrefix.Length) : option;

        var matches = entries.Where(e => string.Equals(e.Key, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = entries.Where(e => string.Equals(e.Id, token, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            // Only claim the option when it was explicitly addressed as a modifier;
            // otherwise let the character lookup have it.
            return explicitlyAModifier
                ? Error($"Modifier '{token}' not found. Available: {string.Join(", ", entries.Select(e => e.Key))}")
                : null;
        }

        if (matches.Count > 1)
        {
            return Error($"Modifier '{token}' is ambiguous — {matches.Count} tickboxes share that id. "
                         + $"Use one of: {string.Join(", ", matches.Select(m => m.Key))}");
        }

        var target = matches[0];
        if (!target.Tickbox.IsEnabled)
            return Error($"Modifier '{target.Key}' cannot be toggled right now");

        var title = SafeGetText(() => target.Modifier.Title);
        var before = TickedModifierKeys(entries);
        bool wasTicked = target.Tickbox.IsTicked;
        target.Tickbox.ForceClick();
        var after = TickedModifierKeys(entries);

        var message = $"Modifier '{title ?? target.Key}' {(wasTicked ? "disabled" : "enabled")}";
        var alsoUnticked = before
            .Where(key => !after.Contains(key) && !string.Equals(key, target.Key, StringComparison.Ordinal))
            .ToList();
        if (alsoUnticked.Count > 0)
            message += $". Mutually exclusive, so also disabled: {string.Join(", ", alsoUnticked)}";

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = message,
            ["modifiers"] = after
        };
    }

    /// <summary>
    /// Presses the confirm button on an unlock screen that is currently open.
    /// Revealing an epoch queues these ("Regent is now playable"), and nothing else on
    /// the timeline responds until one is acknowledged. Returns null when none is open.
    /// </summary>
    private static Dictionary<string, object?>? TryConfirmOpenUnlockScreen(NTimelineScreen timelineScreen)
    {
        // No unlock screen queued is the normal case, so this read is quiet.
        var screen = TryReadQuiet(() => timelineScreen.CurrentUnlockScreen);

        if (screen == null || !IsNodeVisible(screen))
            return null;

        var confirm = GetInstanceFieldValue(screen, "_unlockConfirmButton") as NClickableControl
            ?? FindFirst<NUnlockConfirmButton>(screen)
            ?? (NClickableControl?)FindFirst<NAcknowledgeButton>(screen);

        if (confirm == null || !confirm.IsEnabled || !IsControlVisibleOrActionable(confirm))
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = $"{screen.GetType().Name} is open but its confirm button is not actionable yet; retry after the next state poll",
                ["open_unlock_type"] = screen.GetType().Name,
                ["retry"] = true
            };

        confirm.ForceClick();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = $"Confirmed {screen.GetType().Name}",
            ["open_unlock_type"] = screen.GetType().Name,
            ["retry"] = true
        };
    }

    private static Dictionary<string, object?>? TryHandleQueuedTimelineUnlock(NTimelineScreen timelineScreen)
    {
        bool isQueued;
        try
        {
            isQueued = timelineScreen.IsScreenQueued();
        }
        catch (System.ObjectDisposedException)
        {
            return QueuedUnlockRetry("Timeline changed while checking queued unlocks; retry after the next state poll");
        }

        if (!isQueued)
            return null;

        var queuedScreen = TryPeekQueuedTimelineScreen(timelineScreen);
        var queuedType = queuedScreen?.GetType().Name ?? "unlock";
        var queuedEpochIds = GetQueuedTimelineEpochIds(queuedScreen);

        var alreadyRevealed = BuildAlreadyRevealedUnlockResult(queuedType, queuedEpochIds);
        if (alreadyRevealed != null)
            return alreadyRevealed;

        if (string.Equals(queuedType, "NUnlockTimelineScreen", StringComparison.Ordinal) &&
            IsTimelineScreenBusy(timelineScreen))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "ok",
                ["message"] = "Timeline expansion is queued, but the timeline is still animating; retry after the next state poll",
                ["queued_unlock_type"] = queuedType,
                ["pending_epoch_ids"] = queuedEpochIds,
                ["retry"] = true
            };
        }

        return OpenQueuedTimelineScreen(timelineScreen, queuedType, queuedEpochIds);
    }

    /// <summary>A queued-unlock answer that asks the caller to poll again.</summary>
    private static Dictionary<string, object?> QueuedUnlockRetry(string message)
        => new(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = message,
            ["retry"] = true
        };

    /// <summary>
    /// A queued unlock naming an already revealed epoch would take the invalid unlock path,
    /// so it is reported instead of opened.
    /// </summary>
    private static Dictionary<string, object?>? BuildAlreadyRevealedUnlockResult(
        string queuedType, List<string> queuedEpochIds)
    {
        var alreadyRevealed = queuedEpochIds
            .Where(id => string.Equals(GetProgressEpochState(id), "Revealed", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (alreadyRevealed.Count == 0)
            return null;

        var alreadyRevealedSet = new HashSet<string>(alreadyRevealed, StringComparer.OrdinalIgnoreCase);
        var pendingEpochIds = queuedEpochIds
            .Where(id => !alreadyRevealedSet.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
            ["message"] = "Queued timeline unlock references already revealed epochs; not opening it to avoid an invalid unlock path",
            ["queued_unlock_type"] = queuedType,
            ["already_revealed_epoch_ids"] = alreadyRevealed,
            ["pending_epoch_ids"] = pendingEpochIds,
            ["manual_action_required"] = pendingEpochIds.Count > 0,
            ["done"] = true
        };
    }

    private static Dictionary<string, object?> OpenQueuedTimelineScreen(
        NTimelineScreen timelineScreen, string queuedType, List<string> queuedEpochIds)
        => RunAction(
            "timeline.queued_unlock.open",
            () =>
            {
                timelineScreen.OpenQueuedScreen();
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = $"Opening queued {queuedType}",
                    ["queued_unlock_type"] = queuedType,
                    ["pending_epoch_ids"] = queuedEpochIds
                };
            },
            ex => ex is System.ObjectDisposedException
                ? QueuedUnlockRetry("Timeline changed before the queued unlock could open; retry after the next state poll")
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "ok",
                    ["message"] = $"Skipped queued {queuedType}: {DescribeFailure(ex)}",
                    ["queued_unlock_type"] = queuedType,
                    ["retry"] = true
                });

    private static object? TryPeekQueuedTimelineScreen(NTimelineScreen timelineScreen)
    {
        return TryRead(() =>
        {
            var queue = GetInstanceFieldValue(timelineScreen, "_unlockScreens");
            if (queue == null)
                return null;

            var count = queue.GetType().GetProperty("Count")?.GetValue(queue) as int?;
            if (count <= 0)
                return null;

            return queue.GetType().GetMethod("Peek")?.Invoke(queue, null);
        });
    }

    private static bool IsTimelineScreenBusy(NTimelineScreen timelineScreen)
    {
        try
        {
            var isUiVisible = GetInstanceFieldValue(timelineScreen, "_isUiVisible") as bool?;
            if (isUiVisible == false)
                return true;

            var inputBlocker = GetInstanceFieldValue(timelineScreen, "_inputBlocker") as Control;
            if (inputBlocker != null && IsNodeVisible(inputBlocker))
                return true;
        }
        catch (System.ObjectDisposedException)
        {
            return true;
        }

        return false;
    }

    private static List<string> GetQueuedTimelineEpochIds(object? queuedScreen)
    {
        var ids = new List<string>();
        if (queuedScreen == null)
            return ids;

        AddEpochIdsFromObject(GetInstanceFieldValue(queuedScreen, "_epoch"), ids);
        AddEpochIdsFromObject(GetInstanceFieldValue(queuedScreen, "_unlockedEpochs"), ids);
        AddEpochIdsFromObject(GetInstanceFieldValue(queuedScreen, "_erasToUnlock"), ids);

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddEpochIdsFromObject(object? value, List<string> ids)
    {
        if (value == null)
            return;

        if (value is string id)
        {
            ids.Add(id);
            return;
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
                AddEpochIdsFromObject(item, ids);
            return;
        }

        var model = value.GetType().GetProperty("Model")?.GetValue(value) ?? value;
        var modelId = model.GetType().GetProperty("Id")?.GetValue(model)?.ToString();
        if (!string.IsNullOrEmpty(modelId))
            ids.Add(modelId);
    }

    private static string? GetProgressEpochState(string epochId)
    {
        return TryRead(() =>
        {
            var progress = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.Progress;
            return progress?.Epochs
                .FirstOrDefault(epoch => string.Equals(epoch.Id, epochId, StringComparison.OrdinalIgnoreCase))
                ?.State
                .ToString();
        });
    }

    private static List<string> GetProgressEpochIdsByState(params string[] states)
    {
        var stateSet = new HashSet<string>(states, StringComparer.OrdinalIgnoreCase);
        // No loaded save is the normal case outside the timeline, so this read is quiet.
        return TryReadQuiet(() =>
        {
            var progress = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.Progress;
            if (progress == null)
                return new List<string>();

            return progress.Epochs
                .Where(epoch => stateSet.Contains(epoch.State.ToString()))
                .Select(epoch => epoch.Id)
                .ToList();
        }) ?? new List<string>();
    }

    private static Dictionary<string, object?> ClickMenuButtonField(
        object owner,
        string fieldName,
        string successMessage,
        string? disabledMessage = null)
    {
        var btn = GetInstanceFieldValue(owner, fieldName);
        if (btn is NClickableControl clickable)
        {
            if (!clickable.Visible || !clickable.IsVisibleInTree())
                return Error(disabledMessage ?? $"Option '{fieldName}' is not available");

            if (!clickable.IsEnabled)
                return Error(disabledMessage ?? $"Option '{fieldName}' is not available");

            clickable.ForceClick();
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = successMessage };
        }

        return Error($"Could not find button '{fieldName}'");
    }
}
