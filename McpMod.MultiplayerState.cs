using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Mcp;

public static partial class McpMod
{
    // CombatHistoryEntry.RoundNumber is private; read it via cached reflection.
    private static readonly PropertyInfo? s_historyRoundProp =
        typeof(CombatHistoryEntry).GetProperty("RoundNumber",
            BindingFlags.NonPublic | BindingFlags.Instance);

    // CombatHistoryEntry._playerTurnNumbers is private; read it via cached
    // reflection so each row can carry the card OWNER's turn number. The public
    // RoundNumber is not enough: an extra turn carries the same RoundNumber as
    // the turn before it, so a consumer grouping rows into turns would merge the
    // two. PlayerCombatState.TurnNumber counts extra turns, and this dictionary
    // is the snapshot of it taken when the entry was logged.
    private static readonly FieldInfo? s_historyTurnNumbersField =
        typeof(CombatHistoryEntry).GetField("_playerTurnNumbers",
            BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>Called through BuildMultiplayerGameState(), which brackets it with the warning collector.</summary>
    private static Dictionary<string, object?> BuildMultiplayerGameStateCore(bool fullDetail = false)
    {
        Dictionary<string, object?> result = NewStateResult(kind: "multiplayer_state");
        var tree = Engine.GetMainLoop() as SceneTree;

        Dictionary<string, object?>? earlyState = BuildMultiplayerPreflightState(result, tree, out RunState? runState);
        if (earlyState != null)
            return earlyState;

        AppendMultiplayerMetadata(result, runState, out Player? localPlayer);

        // Same overlay-first detection logic as singleplayer
        IOverlayScreen? topOverlay = NOverlayStack.Instance?.Peek();
        AbstractRoom? currentRoom = runState.CurrentRoom;
        bool mapIsOpen = IsMapScreenOpenOrVisible();
        AppendMultiplayerScreenState(result, runState, topOverlay, currentRoom, mapIsOpen);

        if (currentRoom is CombatRoom
            && CombatManager.Instance.IsInProgress
            && result.TryGetValue("state_type", out object? overlayStateType)
            && overlayStateType is string overlayType
            && overlayType is "card_select" or "bundle_select")
        {
            result["battle"] = BuildMultiplayerBattleState(runState);
        }

        AppendMultiplayerRunState(result, runState, localPlayer, fullDetail);
        return result;
    }

    /// <summary>
    /// The answers that come before any state can be read: a blocking FTUE prompt, no run at
    /// all, an unreadable run, or a run that is not multiplayer.
    /// </summary>
    /// <remarks>
    /// Every FTUE in `Decompiled/src/Core/Nodes/Ftue/` gates only on per-profile flags, not on
    /// run mode, so they are equally reachable in multiplayer runs. Surfacing them here gives
    /// MP automation the same dismissal contract as singleplayer (see #71).
    /// </remarks>
    private static Dictionary<string, object?>? BuildMultiplayerPreflightState(
        Dictionary<string, object?> result, SceneTree? tree, out RunState runState)
    {
        runState = null!;

        if (tree?.Root != null)
        {
            Dictionary<string, object?>? ftueState = BuildVisibleFtueState(tree.Root);
            if (ftueState != null)
                return WithStateEnvelope(ftueState, "multiplayer_state");
        }

        if (!RunManager.Instance.IsInProgress)
        {
            result["state_type"] = "menu";
            result["message"] = "No run in progress. Player is in the main menu.";
            return result;
        }

        RunState? currentRun = RunManager.Instance.DebugOnlyGetState();
        if (currentRun == null)
        {
            result["state_type"] = "unknown";
            return result;
        }

        if (!RunManager.Instance.NetService.Type.IsMultiplayer())
        {
            result["state_type"] = "error";
            result["message"] = "Not in a multiplayer run. Use /api/v1/singleplayer instead.";
            return result;
        }

        runState = currentRun;
        return null;
    }

    private static void AppendMultiplayerMetadata(
        Dictionary<string, object?> result, RunState runState, out Player? localPlayer)
    {
        result["game_mode"] = "multiplayer";
        result["net_type"] = RunManager.Instance.NetService.Type.ToString();
        result["player_count"] = runState.Players.Count;
        localPlayer = LocalContext.GetMe(runState);
        if (localPlayer != null)
        {
            for (int i = 0; i < runState.Players.Count; i++)
            {
                if (runState.Players[i] == localPlayer)
                {
                    result["local_player_slot"] = i;
                    break;
                }
            }
        }
    }

    private static void AppendMultiplayerScreenState(
        Dictionary<string, object?> result,
        RunState runState,
        IOverlayScreen? topOverlay,
        AbstractRoom? currentRoom,
        bool mapIsOpen)
    {
        if (TryAppendMultiplayerOverlayState(result, topOverlay, mapIsOpen))
            return;
        AppendMultiplayerRoomState(result, runState, currentRoom);
    }

    /// <summary>The multiplayer overlay ladder, in the same order as the singleplayer one.</summary>
    private static bool TryAppendMultiplayerOverlayState(
        Dictionary<string, object?> result, IOverlayScreen? topOverlay, bool mapIsOpen)
        => TryAppendMultiplayerSelectionOverlay(result, topOverlay)
           || TryAppendMultiplayerCrystalSphereOverlay(result, topOverlay)
           || TryAppendMultiplayerCardRewardOverlay(result, topOverlay, mapIsOpen)
           || TryAppendMultiplayerRewardsOverlay(result, topOverlay, mapIsOpen)
           || TryAppendMultiplayerFallbackOverlay(result, topOverlay);

    private static bool TryAppendMultiplayerSelectionOverlay(
        Dictionary<string, object?> result, IOverlayScreen? topOverlay)
    {
        switch (topOverlay)
        {
            case NCardGridSelectionScreen cardSelectScreen:
                result["state_type"] = "card_select";
                result["card_select"] = BuildCardSelectState(cardSelectScreen);
                return true;
            case NChooseACardSelectionScreen chooseCardScreen:
                result["state_type"] = "card_select";
                result["card_select"] = BuildChooseCardState(chooseCardScreen);
                return true;
            case NChooseABundleSelectionScreen bundleScreen:
                result["state_type"] = "bundle_select";
                result["bundle_select"] = BuildBundleSelectState(bundleScreen);
                return true;
            case NChooseARelicSelection relicSelectScreen:
                result["state_type"] = "relic_select";
                result["relic_select"] = BuildRelicSelectState(relicSelectScreen);
                return true;
            default:
                return false;
        }
    }

    private static bool TryAppendMultiplayerCrystalSphereOverlay(
        Dictionary<string, object?> result, IOverlayScreen? topOverlay)
    {
        if (topOverlay is not NCrystalSphereScreen crystalSphereScreen)
            return false;

        result["state_type"] = "crystal_sphere";
        result["crystal_sphere"] = BuildCrystalSphereState(crystalSphereScreen);
        return true;
    }

    private static bool TryAppendMultiplayerCardRewardOverlay(
        Dictionary<string, object?> result, IOverlayScreen? topOverlay, bool mapIsOpen)
    {
        if (mapIsOpen || topOverlay is not NCardRewardSelectionScreen cardRewardScreen)
            return false;

        result["state_type"] = "card_reward";
        result["card_reward"] = BuildCardRewardState(cardRewardScreen);
        return true;
    }

    private static bool TryAppendMultiplayerRewardsOverlay(
        Dictionary<string, object?> result, IOverlayScreen? topOverlay, bool mapIsOpen)
    {
        if (topOverlay is not NRewardsScreen rewardsScreen
            || (mapIsOpen && !RewardsSetIsOutstanding(rewardsScreen)))
            return false;

        // Mirrors the singleplayer builder: a rewards set the run is still
        // waiting on outranks a visible map screen, or a set offered from
        // inside a room action (Tiny Mailbox's rest-site potions) is masked
        // as the room it came from and nobody can act on it.
        result["state_type"] = "rewards";
        result["rewards"] = BuildRewardsState(rewardsScreen);
        return true;
    }

    private static bool TryAppendMultiplayerFallbackOverlay(
        Dictionary<string, object?> result, IOverlayScreen? topOverlay)
    {
        if (topOverlay is NGameOverScreen)
        {
            result["state_type"] = "game_over";
            result["game_over"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["message"] = "Run ended.",
                ["options"] = new List<string> { "main_menu" }
            };
            return true;
        }

        if (topOverlay is not IOverlayScreen
            || topOverlay is NRewardsScreen
            || topOverlay is NCardRewardSelectionScreen)
            return false;

        result["state_type"] = "overlay";
        result["overlay"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["screen_type"] = topOverlay.GetType().Name,
            ["message"] = $"An overlay ({topOverlay.GetType().Name}) is active. It may require manual interaction in-game."
        };
        return true;
    }

    /// <summary>The room ladder, in the same order as the singleplayer one.</summary>
    private static void AppendMultiplayerRoomState(
        Dictionary<string, object?> result, RunState runState, AbstractRoom? currentRoom)
    {
        if (TryAppendMultiplayerLoadingCombat(result, currentRoom))
            return;
        if (TryAppendMultiplayerCombatRoom(result, runState, currentRoom))
            return;
        if (TryAppendMultiplayerEventOrMapRoom(result, runState, currentRoom))
            return;
        if (TryAppendMultiplayerShopRoom(result, runState, currentRoom))
            return;
        if (TryAppendMultiplayerRestOrTreasureRoom(result, runState, currentRoom))
            return;

        result["state_type"] = "unknown";
        result["room_type"] = currentRoom?.GetType().Name;
    }

    private static bool TryAppendMultiplayerLoadingCombat(
        Dictionary<string, object?> result, AbstractRoom? currentRoom)
    {
        if (currentRoom is not CombatRoom loadingCombat
            || CombatManager.Instance.IsInProgress
            || (!CombatManager.Instance.IsStarting
                && !IsMapTravelInFlight()
                && !IsRunTransitionInFlight()))
            return false;

        // Mirrors the singleplayer builder: the combat room is entered but
        // not started yet, and the map screen is still visible over it. See
        // the long comment there - reporting "map" here lets a client travel
        // a second time on top of the travel that is still resolving.
        result["state_type"] = loadingCombat.RoomType.ToString().ToLowerInvariant();
        result["combat_starting"] = true;
        result["message"] = "Combat is starting. Wait for the battle state; do not send actions yet.";
        return true;
    }

    private static bool TryAppendMultiplayerCombatRoom(
        Dictionary<string, object?> result, RunState runState, AbstractRoom? currentRoom)
    {
        if (currentRoom is not CombatRoom combatRoom)
            return false;

        if (CombatManager.Instance.IsInProgress)
        {
            NPlayerHand? playerHand = NPlayerHand.Instance;
            if (playerHand != null && playerHand.IsInCardSelection)
            {
                result["state_type"] = "hand_select";
                result["hand_select"] = BuildHandSelectState(playerHand);
                result["battle"] = BuildMultiplayerBattleState(runState);
            }
            else
            {
                result["state_type"] = combatRoom.RoomType.ToString().ToLowerInvariant();
                result["battle"] = BuildMultiplayerBattleState(runState);
            }
            return true;
        }

        // After combat ends - reward/card overlays are caught by top-level checks above.
        if (IsMapScreenOpenOrVisible())
        {
            result["state_type"] = "map";
            result["map"] = BuildMultiplayerMapState(runState);
        }
        else
        {
            result["state_type"] = combatRoom.RoomType.ToString().ToLowerInvariant();
            result["message"] = "Combat ended. Waiting for rewards...";
        }
        return true;
    }

    private static bool TryAppendMultiplayerEventOrMapRoom(
        Dictionary<string, object?> result, RunState runState, AbstractRoom? currentRoom)
    {
        if (currentRoom is EventRoom eventRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMultiplayerMapState(runState);
            }
            else if (eventRoom.CanonicalEvent is FakeMerchant)
            {
                result["state_type"] = "fake_merchant";
                result["fake_merchant"] = BuildFakeMerchantState(eventRoom);
            }
            else
            {
                result["state_type"] = "event";
                result["event"] = BuildMultiplayerEventState(eventRoom, runState);
            }
            return true;
        }

        if (currentRoom is MapRoom)
        {
            result["state_type"] = "map";
            result["map"] = BuildMultiplayerMapState(runState);
            return true;
        }

        return false;
    }

    private static bool TryAppendMultiplayerShopRoom(
        Dictionary<string, object?> result, RunState runState, AbstractRoom? currentRoom)
    {
        if (currentRoom is not MerchantRoom merchantRoom)
            return false;

        if (IsMapScreenOpenOrVisible())
        {
            result["state_type"] = "map";
            result["map"] = BuildMultiplayerMapState(runState);
        }
        else
        {
            result["state_type"] = "shop";
            result["shop"] = BuildShopState(merchantRoom, runState);
        }
        return true;
    }

    private static bool TryAppendMultiplayerRestOrTreasureRoom(
        Dictionary<string, object?> result, RunState runState, AbstractRoom? currentRoom)
    {
        if (currentRoom is RestSiteRoom restSiteRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMultiplayerMapState(runState);
            }
            else
            {
                result["state_type"] = "rest_site";
                result["rest_site"] = BuildRestSiteState(restSiteRoom);
            }
            return true;
        }

        if (currentRoom is TreasureRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMultiplayerMapState(runState);
            }
            else
            {
                result["state_type"] = "treasure";
                result["treasure"] = BuildMultiplayerTreasureState(runState);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// The blocks every multiplayer screen carries: run info and its RNG streams, the act's
    /// pre-rolled queues, the profile counters, the run context, the roster and the local
    /// player.
    /// </summary>
    private static void AppendMultiplayerRunState(
        Dictionary<string, object?> result, RunState runState, Player? localPlayer, bool fullDetail)
    {
        // Common run info
        var runInfo = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["player_count"] = runState.Players.Count,
            ["card_multiplayer_constraint"] = ((IRunState)runState).CardMultiplayerConstraint.ToString(),
            ["ascension"] = runState.AscensionLevel
        };
        // Run RNG streams on every screen, not just in combat. The host-synced
        // RunRngSet is shared by all players, so these counters reflect everyone.
        Dictionary<string, object?>? runRng = BuildRngStreams(runState);
        if (runRng != null)
            runInfo["rng"] = runRng;
        runInfo["visited_event_ids"] = runState.VisitedEventIds.Select(id => id.Entry).ToList();
        result["run"] = runInfo;

        // Keep the route-planning contract identical to singleplayer. These
        // pre-rolled queues are shared run state in co-op too; omitting them
        // forced every multiplayer map decision onto the local-node fallback.
        Dictionary<string, object?>? actInfo = BuildActState(runState);
        if (actInfo != null)
            result["act"] = actInfo;

        TryDo(() =>
        {
            result["profile"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["number_of_runs"] = runState.UnlockState.NumberOfRuns,
                ["first_run_ironclad_present"] = HasFirstRunIronclad(runState)
            };
        });

        // Fork addition: the same active-run context singleplayer reports, so a client
        // can correlate a co-op state read with the save files it came from.
        result["current_run"] = BuildActiveRunContext();

        // All players summary (always included for multiplayer)
        // Only the top-level roster follows `detail=full`. Room-local `players`
        // arrays remain compact summaries so a response never duplicates every
        // teammate's master deck, relics, and potions.
        result["players"] = BuildAllPlayersState(runState, fullDetail);

        // Always include full local player data (relics, potions, deck, etc.) on every screen,
        // matching singleplayer behavior from BuildGameState()
        if (localPlayer != null)
        {
            result["player"] = BuildPlayerState(localPlayer);
        }
    }

    private static Dictionary<string, object?> BuildMultiplayerBattleState(RunState runState)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        var battle = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (combatState == null)
        {
            battle["error"] = "Combat state unavailable";
            return battle;
        }

        battle["next_creature_id"] = NextCreatureId(combatState);
        battle["round"] = combatState.RoundNumber;
        battle["turn"] = combatState.CurrentSide.ToString().ToLowerInvariant();
        // IsCombatPlayPhase() instead of the compiled IsPlayPhase property the fork
        // used: STS2 0.107 removed that accessor (see McpMod.Helpers.cs).
        battle["is_play_phase"] = IsCombatPlayPhase();
        battle["player_actions_disabled"] = CombatManager.Instance.PlayerActionsDisabled;
        battle["all_players_ready"] = CombatManager.Instance.AllPlayersReadyToEndTurn();

        Player? localPlayer = LocalContext.GetMe(runState);
        if (localPlayer != null)
        {
            bool isReady = CombatManager.Instance.IsPlayerReadyToEndTurn(localPlayer);
            battle["local_player_ready_to_end_turn"] = isReady;

            string? endTurnBlockedReason = GetMultiplayerEndTurnBlockedReason(localPlayer);
            battle["can_end_turn"] = endTurnBlockedReason == null;
            battle["end_turn_blocked_reason"] = endTurnBlockedReason;

            string? undoBlockedReason = GetMultiplayerUndoEndTurnBlockedReason(localPlayer);
            battle["can_undo_end_turn"] = undoBlockedReason == null;
            battle["undo_end_turn_blocked_reason"] = undoBlockedReason;
        }

        battle["enemies"] = BuildEnemyList(combatState);

        // Run RNG stream coordinates — the host-synced RunRngSet is shared by
        // all players in lockstep MP, so the local counters reflect everyone.
        Dictionary<string, object?>? rngStreams = BuildRngStreams(runState);
        if (rngStreams != null)
            battle["rng_streams"] = rngStreams;

        // Combat history: every player's card plays + per-player damage dealt
        // to enemies.  MP combat is lockstep (ActionQueueSynchronizer replays
        // every peer's actions locally), so the local CombatHistory contains
        // remote players' plays too.
        TryDo(() =>
        {
            battle["card_plays"] = BuildCardPlaysState(combatState);
            battle["card_history"] = BuildCardHistoryState(combatState);
            battle["player_damage"] = BuildPlayerDamageState(runState, combatState.RoundNumber);
        });
        // History unavailable (e.g. combat tearing down) — omit the fields

        return battle;
    }

    private static int GetEntryRound(CombatHistoryEntry entry)
    {
        return TryRead<int?>(() => s_historyRoundProp?.GetValue(entry) is int r ? r : 0) ?? 0;
    }

    private static int? GetEntryPlayerTurn(CombatHistoryEntry entry, Player? owner)
    {
        if (owner == null)
            return null;

        // A changed field layout only costs the turn number: the row still carries the
        // round and the card id.
        return TryReadQuiet<int?>(() =>
            s_historyTurnNumbersField?.GetValue(entry) is Dictionary<ulong, int> turns
            && turns.TryGetValue(owner.NetId, out int turn)
                ? turn
                : null);
    }

    // The multiplayer synchronizer ids every mutable combat card; that identity
    // is what tells two copies of Strike apart across polls. Deliberately NOT
    // gated on Pile?.IsCombatPile the way BuildCardInfo is: a history row may
    // name a card that has since been transformed or destroyed and so belongs to
    // no pile, and its id is exactly what lets a consumer match it back to the
    // hand it was last seen in.
    private static uint? GetCombatCardId(CardModel? card)
    {
        if (card == null)
            return null;
        return TryRead(() => NetCombatCard.FromModel(card).CombatCardIndex);
    }

    // CardModel.Owner throws once the owning creature is gone (combat teardown,
    // a destroyed card), and every caller here is inside a state build that must
    // not fail as a whole.
    private static Player? CardOwner(CardModel? card)
    {
        if (card == null)
            return null;
        return TryRead(() => card.Owner);
    }

    private static List<Dictionary<string, object?>> BuildCardPlaysState(ICombatState combatState)
    {
        var plays = new List<Dictionary<string, object?>>();
        int idx = 0;
        foreach (CardPlayFinishedEntry entry in CombatManager.Instance.History.CardPlaysFinished)
        {
            CardPlay cardPlay = entry.CardPlay;
            string? target = null;
            TryDo(() =>
            {
                target = cardPlay.Target?.Monster?.Id.Entry
                         ?? SafeGetText(() => cardPlay.Target?.Player?.Character.Title);
            });
            /* target creature may be gone */

            plays.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // Stable per-combat sequence number — the history only appends
                // within a fight, so pollers dedup with "index > last seen".
                ["index"] = idx++,
                ["round"] = GetEntryRound(entry),
                // An extra turn shares its round with the turn before it, so
                // `round` alone cannot group plays into turns.
                ["player_turn"] = GetEntryPlayerTurn(entry, CardOwner(cardPlay.Card)),
                // Includes every player's turn snapshot and CurrentSide; an
                // owner's unchanged turn alone cannot group multiplayer rows.
                ["happened_this_turn"] = entry.HappenedThisTurn(combatState),
                ["player"] = SafeGetText(() => cardPlay.Card.Owner.Character.Title),
                ["is_local"] = LocalContext.IsMe(cardPlay.Card.Owner),
                ["card_id"] = SafeGetText(() => cardPlay.Card.Id.Entry),
                ["card_name"] = SafeGetText(() => cardPlay.Card.Title),
                // Joins this play to the instance-identified pile snapshots.
                ["combat_card_id"] = GetCombatCardId(cardPlay.Card),
                // Where the card went after resolving — the difference between
                // "played and discarded" and "played and exhausted".
                ["result_pile"] = cardPlay.ResultPile.ToString(),
                ["play_index"] = cardPlay.PlayIndex,
                ["play_count"] = cardPlay.PlayCount,
                ["is_auto_play"] = cardPlay.IsAutoPlay,
                // Immutable at finish, unlike the card's current keywords.
                ["was_ethereal"] = entry.WasEthereal,
                ["target"] = target
            });
        }
        return plays;
    }

    /// <summary>
    /// Where every card went during the current and previous player turn.
    ///
    /// `card_plays` answers "what was played"; this answers everything else, and
    /// the two together are what let a client reconstruct a whole turn's card
    /// churn without diffing pile snapshots across polls. Diffing cannot work on
    /// its own: a card drawn and discarded between two polls is never observed
    /// in any pile, and a card mid-resolution sits in PileType.Play, which is not
    /// exported at all.
    ///
    /// Deliberately NOT a source for "left in hand at end of turn":
    /// CombatManager.FlushPlayerHand moves the leftovers with CardPileCmd.Add
    /// rather than CardCmd.Discard, and only the latter reaches
    /// CombatHistory.CardDiscarded — so the end-of-turn flush logs nothing here.
    /// Every `discarded` row is therefore a mid-turn discard, and a client after
    /// the flush must read the hand instead (see `should_retain_this_turn`).
    ///
    /// Bounded to this turn plus each owner's previous turn so a long fight does
    /// not re-serialize hundreds of rows on every poll. Reading the previous turn
    /// is what lets a client wait until a turn is complete — the end-of-turn
    /// cleanup resolves after the turn ends — before scoring it.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildCardHistoryState(ICombatState combatState)
    {
        var rows = new List<Dictionary<string, object?>>();
        int idx = 0;
        foreach (CombatHistoryEntry entry in CombatManager.Instance.History.Entries)
        {
            string kind;
            CardModel? card;
            switch (entry)
            {
                case CardPlayFinishedEntry played: kind = "played"; card = played.CardPlay.Card; break;
                case CardDrawnEntry drawn: kind = "drawn"; card = drawn.Card; break;
                case CardDiscardedEntry discarded: kind = "discarded"; card = discarded.Card; break;
                case CardExhaustedEntry exhausted: kind = "exhausted"; card = exhausted.Card; break;
                case CardGeneratedEntry generated: kind = "generated"; card = generated.Card; break;
                default: continue;
            }
            if (card == null)
                continue;

            Player? owner = CardOwner(card);
            // An entry whose turn test throws cannot be placed in this window, so it is
            // skipped rather than reported without its scope.
            bool? inScope = TryReadQuiet<bool?>(() =>
                entry.HappenedThisTurn(combatState)
                || (owner != null && entry.HappenedLastPlayerTurn(owner)));
            if (inScope != true)
                continue;

            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = idx++,
                ["kind"] = kind,
                ["round"] = GetEntryRound(entry),
                ["player_turn"] = GetEntryPlayerTurn(entry, owner),
                ["is_local"] = LocalContext.IsMine(card),
                ["card_id"] = SafeGetText(() => card.Id.Entry),
                ["card_name"] = SafeGetText(() => card.Title),
                ["is_upgraded"] = card.IsUpgraded,
                ["combat_card_id"] = GetCombatCardId(card),
                ["from_hand_draw"] = entry is CardDrawnEntry drawnEntry && drawnEntry.FromHandDraw,
                // Machine-readable CardKeyword names. The `keywords` field on a
                // card object is the localized hover-tip list and cannot be
                // matched against reliably.
                ["keyword_ids"] = BuildKeywordIds(card),
            });
        }
        return rows;
    }

    /// <summary>
    /// CardKeyword enum names for a card, or null when they cannot be read.
    /// Distinct from the localized `keywords` hover tips that card objects carry.
    /// </summary>
    private static string[]? BuildKeywordIds(CardModel? card)
    {
        if (card == null)
            return null;
        return TryRead(() => card.Keywords.Select(kw => kw.ToString()).ToArray());
    }

    private static List<Dictionary<string, object?>> BuildPlayerDamageState(RunState runState, int currentRound)
    {
        // Aggregate unblocked (HP) damage dealt to enemies, per player per
        // round.  Pet damage credits the pet's owner; unattributable sources
        // (dealer-less DoTs) are skipped.
        int rounds = Math.Max(1, currentRound);
        var totals = new Dictionary<Player, int[]>();
        foreach (CombatHistoryEntry entry in CombatManager.Instance.History.Entries)
        {
            if (entry is not DamageReceivedEntry dmg)
                continue;
            if (dmg.Dealer == null || !dmg.Receiver.IsMonster)
                continue;
            Player? owner = dmg.Dealer.Player ?? dmg.Dealer.PetOwner;
            if (owner == null)
                continue;
            int round = Math.Clamp(GetEntryRound(entry), 1, rounds);
            if (!totals.TryGetValue(owner, out int[]? byRound))
            {
                byRound = new int[rounds];
                totals[owner] = byRound;
            }
            byRound[round - 1] += dmg.Result.UnblockedDamage;
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (Player player in runState.Players)
        {
            totals.TryGetValue(player, out int[]? byRound);
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["player"] = SafeGetText(() => player.Character.Title),
                ["is_local"] = LocalContext.IsMe(player),
                // by_round[i] = HP damage dealt to enemies in round i+1
                ["by_round"] = (byRound ?? new int[rounds]).ToList(),
                ["total"] = byRound?.Sum() ?? 0
            });
        }
        return result;
    }

    private static string? GetMultiplayerEndTurnBlockedReason(Player player)
    {
        if (!CombatManager.Instance.IsInProgress)
            return "NotInCombat";
        if (!IsCombatPlayPhase())
            return "NotInPlayPhase";
        if (CombatManager.Instance.PlayerActionsDisabled)
            return "PlayerActionsDisabled";
        if (!player.Creature.IsAlive)
            return "PlayerDead";
        if (CombatManager.Instance.IsPlayerReadyToEndTurn(player))
            return "AlreadyReady";

        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand != null && hand.InCardPlay)
            return "CardInPlay";
        if (hand != null && hand.CurrentMode != NPlayerHand.Mode.Play)
            return "HandSelectionMode";
        if (player.Creature.CombatState == null)
            return "NoCombatState";

        return null;
    }

    private static string? GetMultiplayerUndoEndTurnBlockedReason(Player player)
    {
        if (!CombatManager.Instance.IsInProgress)
            return "NotInCombat";
        if (!IsCombatPlayPhase())
            return "NotInPlayPhase";
        if (CombatManager.Instance.PlayerActionsDisabled)
            return "PlayerActionsDisabled";
        if (!player.Creature.IsAlive)
            return "PlayerDead";
        if (!CombatManager.Instance.IsPlayerReadyToEndTurn(player))
            return "NotReady";
        if (player.Creature.CombatState == null)
            return "NoCombatState";

        return null;
    }

    private static Dictionary<string, object?> BuildMultiplayerMapState(RunState runState)
    {
        // Start with the standard map state
        Dictionary<string, object?> state = BuildMapState(runState);

        // Add per-player vote data
        TryDo("map.votes", () =>
        {
            MapSelectionSynchronizer mapSync = RunManager.Instance.MapSelectionSynchronizer;
            var votes = new List<Dictionary<string, object?>>();

            foreach (Player player in runState.Players)
            {
                MapVote? vote = mapSync.GetVote(player);
                votes.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["player"] = SafeGetText(() => player.Character.Title),
                    ["is_local"] = LocalContext.IsMe(player),
                    ["voted"] = vote != null,
                    ["vote_col"] = vote?.coord.col,
                    ["vote_row"] = vote?.coord.row
                });
            }

            state["votes"] = votes;
            state["all_voted"] = votes.All(v => v["voted"] is true);
        });

        // All players summary
        state["players"] = BuildAllPlayersState(runState);

        return state;
    }

    private static Dictionary<string, object?> BuildMultiplayerEventState(EventRoom eventRoom, RunState runState)
    {
        // Start with the standard event state
        Dictionary<string, object?> state = BuildEventState(eventRoom);

        // Add multiplayer-specific event data
        TryDo("event.votes", () =>
        {
            EventSynchronizer eventSync = RunManager.Instance.EventSynchronizer;
            bool isShared = false;
            // Left silent: IsShared throws whenever no event is in progress, which is the
            // normal reading on every other screen - a warning here would fire constantly.
            TryDo(() => { isShared = eventSync.IsShared; });
            state["is_shared"] = isShared;

            if (isShared)
            {
                var votes = new List<Dictionary<string, object?>>();
                foreach (Player player in runState.Players)
                {
                    uint? vote = eventSync.GetPlayerVote(player);
                    votes.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["player"] = SafeGetText(() => player.Character.Title),
                        ["is_local"] = LocalContext.IsMe(player),
                        ["voted"] = vote != null,
                        ["vote_option"] = vote
                    });
                }
                state["votes"] = votes;
                state["all_voted"] = votes.All(v => v["voted"] is true);
            }
        });

        // All players summary
        state["players"] = BuildAllPlayersState(runState);

        return state;
    }

    private static Dictionary<string, object?> BuildMultiplayerTreasureState(RunState runState)
    {
        // Auto-open chest same as singleplayer. BeginRelicPicking() runs during
        // TreasureRoom.Enter(), so relics are already generated. The chest click
        // just triggers the UI animation + gold via OneOffSynchronizer - same path
        // as a human click or the game's own AutoSlay handler.
        Dictionary<string, object?> state = BuildTreasureState();

        // Add per-player bid data
        TryDo("treasure.bids", () =>
        {
            TreasureRoomRelicSynchronizer treasureSync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            IReadOnlyList<RelicModel>? currentRelics = treasureSync.CurrentRelics;

            state["is_bidding_phase"] = currentRelics != null;

            if (currentRelics != null)
            {
                var bids = new List<Dictionary<string, object?>>();
                foreach (Player player in runState.Players)
                {
                    TreasureRoomRelicSynchronizer.PlayerVote? vote = treasureSync.GetPlayerVote(player);
                    bids.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["player"] = SafeGetText(() => player.Character.Title),
                        ["is_local"] = LocalContext.IsMe(player),
                        ["voted"] = vote != null,
                        ["vote_relic_index"] = vote
                    });
                }
                state["bids"] = bids;
                state["all_bid"] = bids.All(b => b["voted"] is true);
            }
        });

        // All players summary
        state["players"] = BuildAllPlayersState(runState);

        return state;
    }

    private static List<Dictionary<string, object?>> BuildAllPlayersState(RunState runState, bool fullDetail = false)
    {
        bool inCombat = CombatManager.Instance.IsInProgress;
        var players = new List<Dictionary<string, object?>>();
        for (int i = 0; i < runState.Players.Count; i++)
            players.Add(BuildRosterEntry(runState.Players[i], i, inCombat, fullDetail));
        return players;
    }

    /// <summary>One roster row: identity and totals, plus whatever the fight allows.</summary>
    private static Dictionary<string, object?> BuildRosterEntry(
        Player player, int slot, bool inCombat, bool fullDetail)
    {
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Stable target id for ally/player-targeting cards and potions
            // (play_card / use_potion 'target' parameter).
            ["entity_id"] = $"player_{slot}",
            ["slot_index"] = slot,
            ["net_id"] = player.NetId.ToString(),
            ["character_id"] = player.Character.Id.Entry,
            ["character"] = SafeGetText(() => player.Character.Title),
            ["is_local"] = LocalContext.IsMe(player),
            ["hp"] = player.Creature.CurrentHp,
            ["max_hp"] = player.Creature.MaxHp,
            ["gold"] = player.Gold,
            ["is_alive"] = player.Creature.IsAlive
        };

        // Each player rolls their own rewards/shops/transformations off a
        // slot-seeded PlayerRngSet. See McpMod.RngStreams.cs.
        Dictionary<string, object?>? playerRng = BuildPlayerRngStreams(player);
        if (playerRng != null)
            entry["rng"] = playerRng;

        if (fullDetail)
            AddFullRunPlayerDetail(entry, player);

        if (!inCombat)
            return entry;

        entry["combat_id"] = player.Creature.CombatId;
        entry["block"] = player.Creature.Block;
        entry["is_ready_to_end_turn"] = CombatManager.Instance.IsPlayerReadyToEndTurn(player);

        // Include pets for teammates (local player's pets are under "player")
        if (!LocalContext.IsMe(player))
            AppendTeammateState(entry, player);
        return entry;
    }

    private static void AppendTeammateState(Dictionary<string, object?> entry, Player player)
    {
        List<Dictionary<string, object?>> pets = BuildPetsState(player);
        if (pets.Count > 0)
        {
            entry["pets"] = pets;
        }

        TryDo(() => AppendTeammateCombatState(entry, player));
        /* teammate combat state mid-transition — omit hand */
    }

    /// <summary>
    /// A teammate's combat state. MP combat is lockstep — every player's PlayerCombatState is
    /// replicated locally — so a teammate's hand cards are known client-side even though the
    /// game UI hides them (this mirrors the STS2-ShowPlayerHandCards mod). BuildCardInfo is
    /// used rather than BuildCardState so CanPlay hooks are not run against a non-local
    /// player's card in the local combat context.
    /// </summary>
    private static void AppendTeammateCombatState(Dictionary<string, object?> entry, Player player)
    {
        PlayerCombatState? cs = player.PlayerCombatState;
        if (cs == null)
            return;

        var hand = new List<Dictionary<string, object?>>();
        int cardIndex = 0;
        foreach (CardModel card in cs.Hand.Cards)
        {
            Dictionary<string, object?> info = BuildCardInfo(card);
            info["index"] = cardIndex++;
            info["target_type"] = card.TargetType.ToString();
            hand.Add(info);
        }
        entry["hand"] = hand;
        entry["draw_pile_count"] = cs.DrawPile.Cards.Count;
        entry["discard_pile_count"] = cs.DiscardPile.Cards.Count;
        entry["exhaust_pile_count"] = cs.ExhaustPile.Cards.Count;
        entry["play_pile_count"] = cs.PlayPile.Cards.Count;
        entry["is_resolving"] = PlayerIsResolvingEffect(player);

        // Full pile contents so the teammate's turn can be forward-modelled. Draw
        // pile is in true order (index 0 = next draw), valid until their next
        // shuffle — same guarantee as the local player's.
        entry["draw_pile"] = BuildPileCardList(cs.DrawPile.Cards, PileType.Draw);
        entry["discard_pile"] = BuildPileCardList(cs.DiscardPile.Cards, PileType.Discard);
        entry["exhaust_pile"] = BuildPileCardList(cs.ExhaustPile.Cards, PileType.Exhaust);
        entry["play_pile"] = BuildPileCardList(cs.PlayPile.Cards, PileType.Play);

        // Powers/status + orbs — also needed to model their turn
        // (Strength/Vulnerable scaling, Defect orbs).
        entry["status"] = BuildPowersState(player.Creature);
        AddOrbsState(entry, player);

        // Relics + potions make the teammate's turn fully searchable
        // (per-card play legality, potion actions).
        entry["relics"] = BuildRelicsList(player);
        AddPotionsState(entry, player);

        // Energy getters run hooks (Hook.ModifyMaxEnergy) —
        // isolate them so a throw still leaves the hand intact.
        TryDo(() =>
        {
            entry["energy"] = cs.Energy;
            entry["max_energy"] = cs.MaxEnergy;
        });

        // Emotion Chip's owner-turn history, as for the local player. Isolated so a
        // missing history omits only this field, which the planner then reports.
        TryDo(() =>
        {
            entry["damage_not_fully_blocked_this_owner_turn"] = DamageNotFullyBlockedThisOwnerTurn(
                player, CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>());
        });
    }

    /// <summary>Adds the complete, non-combat player state requested by
    /// <c>detail=full</c>. Relic grab-bag contents deliberately stay on
    /// <c>/api/v1/relicbag</c>; joining the endpoints only needs the top-level
    /// constant-time generation value.</summary>
    private static void AddFullRunPlayerDetail(Dictionary<string, object?> entry, Player player)
    {
        var deck = new List<Dictionary<string, object?>>();
        string? deckFailure = TryAction("multiplayer.master_deck", () =>
        {
            int deckIndex = 0;
            foreach (CardModel card in player.Deck.Cards)
            {
                Dictionary<string, object?> info = BuildCardInfo(card);
                info["index"] = deckIndex++;
                deck.Add(info);
            }
        });
        if (deckFailure == null)
            entry["deck"] = deck;
        else
            entry["detail_incomplete"] = "master deck unavailable";

        entry["status"] = BuildPowersState(player.Creature);
        entry["relics"] = BuildRelicsList(player);
        AddPotionsState(entry, player);
    }
}
