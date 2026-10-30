using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
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
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2_MCP;

public static partial class McpMod
{
    /// <summary>
    /// A top-level state dictionary already carrying the build stamp every payload must report.
    ///
    /// Seeded at creation rather than stamped on the way out because the state builders have
    /// ~20 return paths (menu, unknown, error, FTUE); anything added at the end is missed by the
    /// early ones. `state_type` is seeded first so it keeps its leading position when a caller
    /// overwrites it, and "unknown" is the right answer for a path that never sets one.
    /// </summary>
    internal static Dictionary<string, object?> NewStateResult(
        string stateType = "unknown", string kind = "singleplayer_state")
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state_type"] = stateType,
            ["status"] = "ok",
            ["kind"] = kind,
            ["mod_version"] = Version,
            ["mod_commit"] = BuildCommit,
            ["schema_version"] = StateSchemaVersion
        };
    }

    /// <summary>Called through BuildGameState(), which brackets it with the warning collector.</summary>
    private static Dictionary<string, object?> BuildGameStateCore()
    {
        var result = NewStateResult();
        var tree = (Godot.Engine.GetMainLoop()) as SceneTree;

        // Completed async room entries. A harness embarking runs back to back waits for
        // this to advance before touching the run again — see TrackRoomEntries.
        result["rooms_entered"] = RoomsEntered;

        if (tree?.Root != null)
        {
            var ftueState = BuildVisibleFtueState(tree.Root);
            if (ftueState != null)
                return WithStateEnvelope(ftueState, "singleplayer_state");
        }

        if (!RunManager.Instance.IsInProgress)
        {
            result["state_type"] = "menu";

            // Detect which menu screen is active
            if (tree?.Root != null)
            {
                // Custom run setup screen (seed / ascension / modifiers + its own
                // character buttons). It is a submenu pushed over the singleplayer
                // or multiplayer-host submenu, and it is NOT an NCharacterSelectScreen,
                // so it needs its own detection before either of those.
                if (!result.ContainsKey("menu_screen"))
                {
                    var customRun = FindFirst<NCustomRunScreen>(tree.Root);
                    if (customRun != null && IsNodeVisible(customRun))
                    {
                        AddCustomRunMenuState(result, customRun);
                    }
                }

                if (!result.ContainsKey("menu_screen"))
                {
                    // Check for singleplayer submenu (Standard / Daily / Custom)
                    var spSubmenu = FindFirst<NSingleplayerSubmenu>(tree.Root);
                    if (spSubmenu != null && IsNodeVisible(spSubmenu))
                    {
                        result["menu_screen"] = "singleplayer";
                        result["message"] = "Select game mode.";

                        var modeOptions = new List<Dictionary<string, object?>>();
                        var modeFields = new[] { ("_standardButton", "standard"), ("_dailyButton", "daily"), ("_customButton", "custom") };
                        foreach (var (fieldName, label) in modeFields)
                        {
                            TryDo($"menu.singleplayer.options.{label}", () =>
                            {
                                var btn = GetInstanceFieldValue(spSubmenu, fieldName);
                                if (btn is Control ctrl && IsNodeVisible(ctrl))
                                {
                                    var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                    modeOptions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                                    {
                                        ["name"] = label,
                                        ["enabled"] = isEnabled ?? true
                                    });
                                }
                            });
                        }
                        AddMenuOptionIfVisible(modeOptions, spSubmenu, "_backButton", "back");
                        result["options"] = modeOptions;
                    }
                    // Check for multiplayer host submenu (Standard / Daily / Custom for multiplayer)
                    else
                    {
                        var mpHostSubmenu = FindFirst<NMultiplayerHostSubmenu>(tree.Root);
                        if (mpHostSubmenu != null && IsNodeVisible(mpHostSubmenu))
                        {
                            result["menu_screen"] = "multiplayer_host";
                            result["message"] = "Multiplayer host: select game mode.";

                            var modeOptions = new List<Dictionary<string, object?>>();
                            var modeFields = new[] { ("_standardButton", "standard"), ("_dailyButton", "daily"), ("_customButton", "custom") };
                            foreach (var (fieldName, label) in modeFields)
                            {
                                TryDo($"menu.multiplayer_host.options.{label}", () =>
                                {
                                    var btn = GetInstanceFieldValue(mpHostSubmenu, fieldName);
                                    if (btn is Control ctrl && IsNodeVisible(ctrl))
                                    {
                                        var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                        modeOptions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                                        {
                                            ["name"] = label,
                                            ["enabled"] = isEnabled ?? true
                                        });
                                    }
                                });
                            }
                            AddMenuOptionIfVisible(modeOptions, mpHostSubmenu, "_backButton", "back");
                            result["options"] = modeOptions;
                        }
                        else
                        {
                            // Check for multiplayer submenu (Host / Join / Load / Abandon)
                            var mpSubmenu = FindFirst<NMultiplayerSubmenu>(tree.Root);
                            if (mpSubmenu != null && IsNodeVisible(mpSubmenu))
                            {
                                result["menu_screen"] = "multiplayer";
                                result["message"] = "Multiplayer menu.";

                                var mpOptions = new List<Dictionary<string, object?>>();
                                var mpFields = new[] { ("_hostButton", "host"), ("_joinButton", "join"), ("_loadButton", "load"), ("_abandonButton", "abandon") };
                                foreach (var (fieldName, label) in mpFields)
                                {
                                    TryDo($"menu.multiplayer.options.{label}", () =>
                                    {
                                        var btn = GetInstanceFieldValue(mpSubmenu, fieldName);
                                        if (btn is Control ctrl && IsNodeVisible(ctrl))
                                        {
                                            var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                            mpOptions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                                            {
                                                ["name"] = label,
                                                ["enabled"] = isEnabled ?? true
                                            });
                                        }
                                    });
                                }
                                AddMenuOptionIfVisible(mpOptions, mpSubmenu, "_backButton", "back");
                                result["options"] = mpOptions;
                            }
                        }
                    }
                    // Multiplayer Join Friend screen (lives in the same submenu stack as
                    // NMultiplayerSubmenu; pushed when the user clicks "join")
                    if (!result.ContainsKey("menu_screen"))
                    {
                        var joinScreen = FindFirst<NJoinFriendScreen>(tree.Root);
                        if (joinScreen != null && IsNodeVisible(joinScreen))
                        {
                            AddMultiplayerJoinMenuState(result, joinScreen);
                        }
                    }

                    // Multiplayer Load lobby — resume saved MP run. Pushed by NMultiplayerSubmenu
                    // when "load" is clicked (host) or by JoinFlow when joining a save in progress (client).
                    if (!result.ContainsKey("menu_screen"))
                    {
                        var loadLobby = FindFirst<NMultiplayerLoadGameScreen>(tree.Root);
                        if (loadLobby != null && IsNodeVisible(loadLobby))
                        {
                            AddMultiplayerLoadLobbyMenuState(result, loadLobby);
                        }
                    }

                    // Custom mode setup screen (reached via singleplayer/multiplayer-host
                    // "custom"). It replaces the normal character select and adds a seed
                    // field and run modifiers, so detect it before the plain char select.
                    if (!result.ContainsKey("menu_screen"))
                    {
                        var customRun = FindFirst<NCustomRunScreen>(tree.Root);
                        if (customRun != null && IsNodeVisible(customRun))
                            AddCustomRunMenuState(result, customRun);
                    }

                    // Check for character select screen
                    if (!result.ContainsKey("menu_screen"))
                    {
                        var charSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
                        if (charSelect != null && IsNodeVisible(charSelect))
                        {
                            AddCharacterSelectMenuState(result, charSelect);
                        }
                        else
                        {
                            // Check for other screens
                            var timelineScreen = FindFirst<NTimelineScreen>(tree.Root);
                            var compendiumSubmenu = FindFirst<NCompendiumSubmenu>(tree.Root);
                            var settingsScreen = FindFirst<NSettingsScreen>(tree.Root);

                            if (timelineScreen != null && IsNodeVisible(timelineScreen))
                            {
                                result["menu_screen"] = "timeline";
                                result["message"] = "Timeline screen.";
                                result["options"] = new List<Dictionary<string, object?>>
                            {
                                new(StringComparer.Ordinal) { ["name"] = "advance", ["enabled"] = true },
                                new(StringComparer.Ordinal) { ["name"] = "back", ["enabled"] = true }
                            };

                                // Read epochs from ProgressState (stable, not hover-dependent)
                                foreach (var entry in BuildTimelineEpochState())
                                    result[entry.Key] = entry.Value;
                            }
                            else if (compendiumSubmenu != null && IsNodeVisible(compendiumSubmenu))
                            {
                                result["menu_screen"] = "compendium";
                                result["message"] = "Compendium screen.";
                            }
                            else if (settingsScreen != null && IsNodeVisible(settingsScreen))
                            {
                                foreach (var entry in BuildSettingsMenuState(settingsScreen, inRun: false))
                                    result[entry.Key] = entry.Value;
                            }
                            else
                            {
                                var profileScreen = FindFirst<NProfileScreen>(tree.Root);
                                if (profileScreen != null && IsNodeVisible(profileScreen))
                                {
                                    result["menu_screen"] = "profile_select";
                                    result["message"] = "Profile select screen.";
                                    result["current_profile_id"] = SaveManager.Instance?.CurrentProfileId;

                                    var options = new List<Dictionary<string, object?>>();
                                    var buttons = GetInstanceFieldValue(profileScreen, "_profileButtons") as System.Collections.IEnumerable;
                                    if (buttons != null)
                                    {
                                        foreach (var btn in buttons)
                                        {
                                            var btnId = GetInstanceFieldValue(btn, "_profileId");
                                            if (btnId is int id)
                                            {
                                                var enabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                                options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                                                {
                                                    ["name"] = $"profile_{id}",
                                                    ["enabled"] = enabled ?? true
                                                });
                                            }
                                        }
                                    }

                                    var backBtn = GetInstanceFieldValue(profileScreen, "_backButton")
                                        ?? FindFirst<NBackButton>(profileScreen);
                                    if (backBtn is NClickableControl backClickable && IsNodeVisible(backClickable))
                                    {
                                        options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                                        {
                                            ["name"] = "back",
                                            ["enabled"] = backClickable.IsEnabled
                                        });
                                    }

                                    if (options.Count > 0)
                                        result["options"] = options;
                                }
                            }
                            if (!result.ContainsKey("menu_screen"))
                            {
                                result["menu_screen"] = "main";
                                result["message"] = "Main menu.";

                                var mainMenu = FindFirst<NMainMenu>(tree.Root);
                                if (mainMenu != null)
                                {
                                    var options = new List<string>();
                                    var blockedOptions = new List<Dictionary<string, object?>>();
                                    var fields = new[] { "_continueButton", "_abandonRunButton", "_singleplayerButton", "_multiplayerButton", "_compendiumButton", "_timelineButton", "_settingsButton", "_quitButton" };
                                    var labels = new[] { "continue", "abandon_run", "singleplayer", "multiplayer", "compendium", "timeline", "settings", "quit" };
                                    var unrevealedEpochs = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot");
                                    for (int i = 0; i < fields.Length; i++)
                                    {
                                        TryDo($"menu.main.options.{labels[i]}", () =>
                                        {
                                            var btn = GetInstanceFieldValue(mainMenu, fields[i]);
                                            if (btn is NClickableControl clickable &&
                                                clickable.IsEnabled &&
                                                clickable.Visible &&
                                                clickable.IsVisibleInTree())
                                            {
                                                options.Add(labels[i]);
                                            }
                                        });
                                    }
                                    if (options.Count > 0)
                                        result["options"] = options;
                                    if (blockedOptions.Count > 0)
                                        result["blocked_options"] = blockedOptions;

                                    // A finished run leaves its epochs obtained-but-unrevealed,
                                    // and the main menu hides `singleplayer` until they are
                                    // revealed. Say so, and name the action that clears it.
                                    if (unrevealedEpochs.Count > 0)
                                    {
                                        result["pending_epoch_ids"] = unrevealedEpochs;
                                        result["message"] =
                                            $"Main menu. {unrevealedEpochs.Count} epoch(s) obtained but not revealed - "
                                            + "the singleplayer option stays hidden until they are. "
                                            + "Run the 'timeline_reveal_epochs' action (repeat until done) to clear this.";
                                    }
                                }
                            }
                        }
                    }
                }
            } // close if (!result.ContainsKey("menu_screen"))
            else
            {
                result["message"] = "No run in progress.";
            }

            return result;
        }

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            if (tree?.Root != null)
            {
                var activeCharSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
                if (activeCharSelect != null && IsNodeVisible(activeCharSelect))
                {
                    AddCharacterSelectMenuState(result, activeCharSelect);
                    return result;
                }
            }

            result["state_type"] = "unknown";
            return result;
        }

        // Overlays can appear on top of any room (events, rest sites, combat).
        // Rewards/card-reward overlays defer to the map - they may linger on the
        // overlay stack while the map opens after the player clicks proceed.
        var topOverlay = NOverlayStack.Instance?.Peek();
        var currentRoom = runState.CurrentRoom;
        bool mapIsOpen = IsMapScreenOpenOrVisible();
        // The settings screen is pushed on the run's submenu stack, over everything below.
        // It is checked first so the room underneath is not reported as the live screen.
        var settingsState = TryBuildSettingsMenuState(tree?.Root, inRun: true);
        // The pause menu shares that stack, and settings can be pushed on top of the
        // pause menu, so settings has to keep winning when both are up.
        var pauseState = settingsState == null ? TryBuildPauseMenuState(tree?.Root) : null;
        if (settingsState != null)
        {
            foreach (var entry in settingsState)
                result[entry.Key] = entry.Value;
        }
        else if (pauseState != null)
        {
            foreach (var entry in pauseState)
                result[entry.Key] = entry.Value;
        }
        else if (topOverlay is NCardGridSelectionScreen cardSelectScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildCardSelectState(cardSelectScreen);
        }
        else if (topOverlay is NChooseACardSelectionScreen chooseCardScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildChooseCardState(chooseCardScreen);
        }
        else if (topOverlay is NChooseABundleSelectionScreen bundleScreen)
        {
            result["state_type"] = "bundle_select";
            result["bundle_select"] = BuildBundleSelectState(bundleScreen);
        }
        else if (topOverlay is NChooseARelicSelection relicSelectScreen)
        {
            result["state_type"] = "relic_select";
            result["relic_select"] = BuildRelicSelectState(relicSelectScreen);
        }
        else if (topOverlay is NCrystalSphereScreen crystalSphereScreen
                 && (!mapIsOpen || CrystalSphereIsOutstanding(crystalSphereScreen)))
        {
            // Mirror the NRewardsScreen guard below: the Crystal Sphere overlay
            // lingers on the stack after proceed is clicked (only ClearScreens
            // on the next room transition pops it). Once the map opens on top,
            // report "map" so state matches the screen the player can interact
            // with. See issue #73.
            //
            // ...but only once it HAS been answered. `CrystalSphere.UncoverFuture`
            // awaits `PlayMinigame()` before `SetEventFinished`, so a sphere still
            // being played blocks its event room, and masking it reported an
            // un-travellable map with nothing on it - the same deadlock Brain
            // Leech's card reward and Tiny Mailbox's potions produced.
            result["state_type"] = "crystal_sphere";
            result["crystal_sphere"] = BuildCrystalSphereState(crystalSphereScreen);
        }
        else if (topOverlay is NCardRewardSelectionScreen cardRewardScreen
                 && (!mapIsOpen || CardRewardIsOutstanding(cardRewardScreen)))
        {
            // The map deference here is only correct for a card reward that has
            // already been answered - it lingers on the overlay stack while the
            // map opens underneath. A pick the run is still WAITING on must win
            // over the map, exactly as `RewardsSetIsOutstanding` makes it for a
            // rewards set: Brain Leech's RIP awaits `RewardsCmd.OfferCustom`
            // before `SetEventFinished`, so masking this screen reported an
            // un-travellable "map" (`travel_enabled: false`, no `next_options`,
            // because the room is unfinished) with nothing a client could act
            // on. Observed live at act 1 floor 14: `[STALL] map unchanged` with
            // "No map options" for the rest of the run.
            result["state_type"] = "card_reward";
            result["card_reward"] = BuildCardRewardState(cardRewardScreen);
        }
        else if (topOverlay is NRewardsScreen rewardsScreen
                 && (!mapIsOpen || RewardsSetIsOutstanding(rewardsScreen)))
        {
            // The map deference above is only correct for a rewards screen that
            // has already been answered - it lingers on the stack while the map
            // opens underneath. A set the run is still WAITING on must win over
            // the map: Tiny Mailbox offers two potions from inside the rest-site
            // heal (RewardsCmd.OfferCustom), and that heal is awaited before the
            // chosen option leaves the room's option list, so masking the
            // overlay reported "rest_site" with its (still enabled) options and
            // nothing a client could act on - the run cannot proceed until the
            // potions are taken or skipped, so nothing ever changed again.
            result["state_type"] = "rewards";
            result["rewards"] = BuildRewardsState(rewardsScreen);
        }
        else if (topOverlay is NGameOverScreen)
        {
            result["state_type"] = "game_over";
            result["game_over"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["message"] = "Run ended.",
                ["options"] = new List<string> { "main_menu" }
            };
        }
        else if (topOverlay is IOverlayScreen
                 && topOverlay is not NRewardsScreen
                 && topOverlay is not NCardRewardSelectionScreen
                 && topOverlay is not NCrystalSphereScreen)
        {
            // Catch-all for unhandled overlays - prevents soft-locks.
            // Overlays that linger on the stack while the map takes over
            // (rewards, card reward, Crystal Sphere) are excluded so the
            // fallback below reports "map" once mapIsOpen is true.
            result["state_type"] = "overlay";
            result["overlay"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["screen_type"] = topOverlay.GetType().Name,
                ["message"] = $"An overlay ({topOverlay.GetType().Name}) is active. It may require manual interaction in-game."
            };
        }
        else if (currentRoom is RestSiteRoom pendingRest
                 && pendingRest.Options != null
                 && pendingRest.Options.Any(o => o.IsEnabled))
        {
            // Priority over the map fallback below: after choose_map_node travels
            // onto a rest-site node, the game updates the map model but the map
            // SCREEN can linger visible for a while, so IsMapScreenOpenOrVisible()
            // stays true and we'd otherwise report "map" while the rest room is
            // actually loaded underneath (NRestSiteRoom.Instance != null). That
            // makes the bot travel straight past the rest into the boss. An
            // unfinished rest site (still has an enabled option) is reported as
            // rest_site regardless of the lingering map. Once the rest is used the
            // options disable and we fall through to "map" so the next node can be
            // chosen. See handle_rest_site / handle_map in the bot.
            result["state_type"] = "rest_site";
            result["rest_site"] = BuildRestSiteState(pendingRest);
        }
        else if (currentRoom is EventRoom pendingEvent
                 && pendingEvent.CanonicalEvent is not FakeMerchant
                 && EventHasPendingAction())
        {
            // Same lingering-map masking as the rest branch above: after travelling
            // onto an "Unknown"/event node the map screen stays visible while the
            // event room loads underneath (NEventRoom.Instance != null), so the
            // mapIsOpen fallback would report "map" and the bot would travel past
            // the event. Report "event" while the event still has an actionable
            // choice or live dialogue; once it resolves (no pending action) we fall
            // through to "map" so the next node can be chosen (no deadlock).
            result["state_type"] = "event";
            result["event"] = BuildEventState(pendingEvent);
        }
        else if (currentRoom is TreasureRoom
                 && TreasureHasPendingAction())
        {
            // Same lingering-map masking as the rest/event branches above: after
            // travelling onto a treasure node the map stays visible while the
            // treasure room loads underneath, so the mapIsOpen fallback would
            // report "map" and the bot would skip the chest. Report "treasure"
            // while there is a chest still to open or a relic still to pick; once
            // the room is finished (nothing pending) we fall through to "map".
            result["state_type"] = "treasure";
            result["treasure"] = BuildTreasureState();
        }
        else if (currentRoom is MerchantRoom pendingShop
                 && NMerchantRoom.Instance != null
                 && (!IsMapScreenDeliberatelyOpen() || IsRunTransitionInFlight()))
        {
            // Same lingering-map masking as the rest/event/treasure branches: after
            // travelling onto a shop node the map SCREEN stays visible while the
            // merchant room loads underneath, so a mapIsOpen fallback would report
            // "map" and the bot would skip the shop entirely.
            //
            // Outside that the mask still has to come off, because the singleton
            // is not a finished signal: NMerchantRoom.Instance is NRun's current
            // room scene, and proceeding out only calls NMapScreen.Open() — the
            // scene is replaced when the NEXT room is entered, which cannot happen
            // until we travel. Meanwhile ActiveScreenContext hands the map the
            // focus, so NMerchantRoom.OnActiveScreenUpdated disables the
            // ProceedButton and NMerchantInventory.OnActiveScreenUpdated disables
            // its BackButton: the whole merchant UI is inert and "proceed" can only
            // ever answer "No proceed button available or enabled". A bot that
            // trusted the mask sat there posting it forever.
            //
            // The signal for that is the map being DELIBERATELY open, not merely
            // on screen. IsMapScreenOpenOrVisible() also answers true through
            // Close()'s animate-out, which is exactly the arrival window — so
            // gating on it unmasked the shop about 0.75s after arriving, as soon as
            // IsRunTransitionInFlight() went false, and every merchant reported
            // "map" for the rest of the visit. Measured over a full seeded run:
            // three shops entered, "shop" reported for 0.75s each, three
            // "[MAP] room never opened after travel" guard releases, 783 gold
            // unspent at death. NMerchantRoom._Ready only calls SetTravelEnabled;
            // the one merchant path to NMapScreen.Open() is HideScreen on the
            // ProceedButton, so IsOpen means the shop is done and nothing else.
            result["state_type"] = "shop";
            var shopState = BuildShopState(pendingShop, runState);
            // Still exported for a client that wants to leave by travelling rather
            // than by proceeding — the map's next nodes stay travelable from a
            // merchant node whether or not the map screen is up.
            shopState["next_options"] = BuildMapNextOptions();
            result["shop"] = shopState;
        }
        else if (currentRoom is CombatRoom loadingCombat
                 && !CombatManager.Instance.IsInProgress
                 && (CombatManager.Instance.IsStarting
                     || IsMapTravelInFlight()
                     || IsRunTransitionInFlight()))
        {
            // Last of the lingering-map guards, and the one whose absence was a
            // real soft-lock. The combat room is entered but CombatManager has
            // not flipped IsInProgress yet (asset preload, deck shuffle,
            // spawn-ins, the combat-start banner). Without this branch a state
            // read landing in that window either:
            //   * hits the mapIsOpen fallback below and reports "map" - the map
            //     SCREEN is still visible during the transition - so a client
            //     travels a SECOND time, on top of the travel that is still
            //     resolving, which wedges the run; or
            //   * hits the CombatRoom branch below and is labelled "Combat
            //     ended. Waiting for rewards...", the exact opposite of what is
            //     happening, with no "battle" for the client to act on.
            // The three signals overlap deliberately and leave no gap.
            // EnterRoomInternal pushes the combat room onto the room stack
            // BEFORE awaiting its Enter, so from that moment the room is current
            // while SetUpCombat (and therefore IsStarting) is still several
            // awaits away, behind the combat asset preload - that is the window
            // an event "Fight" option lands in, where neither IsStarting nor the
            // map's IsTraveling is set and only the run-transition handle is.
            // It flips to a real battle in about a second, so clients only ever
            // have to wait.
            result["state_type"] = loadingCombat.RoomType.ToString().ToLowerInvariant();
            result["combat_starting"] = true;
            result["message"] = "Combat is starting. Wait for the battle state; do not send actions yet.";
        }
        else if (mapIsOpen)
        {
            result["state_type"] = "map";
            result["map"] = BuildMapState(runState);
            AddBlockedMapDiagnostics(result, currentRoom, topOverlay);
        }
        else if (currentRoom is CombatRoom combatRoom)
        {
            if (CombatManager.Instance.IsInProgress)
            {
                // Check for in-combat hand card selection (e.g., "Select a card to exhaust")
                var playerHand = NPlayerHand.Instance;
                if (playerHand != null && playerHand.IsInCardSelection)
                {
                    result["state_type"] = "hand_select";
                    result["hand_select"] = BuildHandSelectState(playerHand);
                    result["battle"] = BuildBattleState(runState, combatRoom);
                }
                else
                {
                    result["state_type"] = combatRoom.RoomType.ToString().ToLowerInvariant(); // monster, elite, boss
                    result["battle"] = BuildBattleState(runState, combatRoom);
                }
            }
            else
            {
                // After combat ends - reward/card overlays are caught by top-level checks above.
                // Only handle map and the brief transition before rewards appear.
                if (IsMapScreenOpenOrVisible())
                {
                    result["state_type"] = "map";
                    result["map"] = BuildMapState(runState);
                }
                else
                {
                    result["state_type"] = combatRoom.RoomType.ToString().ToLowerInvariant();
                    result["message"] = "Combat ended. Waiting for rewards...";
                }
            }
        }
        else if (currentRoom is EventRoom eventRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else if (eventRoom.CanonicalEvent is FakeMerchant)
            {
                result["state_type"] = "fake_merchant";
                result["fake_merchant"] = BuildFakeMerchantState(eventRoom);
            }
            else
            {
                result["state_type"] = "event";
                result["event"] = BuildEventState(eventRoom);
            }
        }
        else if (currentRoom is MapRoom)
        {
            result["state_type"] = "map";
            result["map"] = BuildMapState(runState);
        }
        else if (currentRoom is MerchantRoom merchantRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                // Auto-open the shopkeeper's inventory if not already open.
                // NMerchantRoom.Inventory (UI node) can be null before the scene is fully ready;
                // OpenInventory() itself accesses Inventory.IsOpen, so guard against null.
                var merchUI = NMerchantRoom.Instance;
                if (!_passiveRead && merchUI?.Inventory != null && !merchUI.Inventory.IsOpen)
                {
                    merchUI.OpenInventory();
                }
                result["state_type"] = "shop";
                result["shop"] = BuildShopState(merchantRoom, runState);
            }
        }
        else if (currentRoom is RestSiteRoom restSiteRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "rest_site";
                result["rest_site"] = BuildRestSiteState(restSiteRoom);
            }
        }
        else if (currentRoom is TreasureRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "treasure";
                result["treasure"] = BuildTreasureState();
            }
        }
        else if (currentRoom == null && tree?.Root != null)
        {
            var activeCharSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
            if (activeCharSelect != null && IsNodeVisible(activeCharSelect))
            {
                AddCharacterSelectMenuState(result, activeCharSelect);
            }
            else
            {
                result["state_type"] = "unknown";
                // This branch only runs when currentRoom is null.
                result["room_type"] = null;
            }
        }
        else
        {
            result["state_type"] = "unknown";
            result["room_type"] = currentRoom?.GetType().Name;
        }

        // Overlay-first detection must not hide the combat snapshot. The Rust
        // planner needs the enemies, turn, history, and RNG streams both for a
        // fresh selection search and to validate reuse of the subtree that
        // opened the overlay.
        if (currentRoom is CombatRoom overlayCombat
            && CombatManager.Instance.IsInProgress
            && result.TryGetValue("state_type", out var overlayStateType)
            && overlayStateType is string overlayType
            && overlayType is "card_select" or "bundle_select")
        {
            result["battle"] = BuildBattleState(runState, overlayCombat);
        }

        // Common run info
        var runInfo = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["player_count"] = runState.Players.Count,
            ["card_multiplayer_constraint"] = ((IRunState)runState).CardMultiplayerConstraint.ToString(),
            ["ascension"] = runState.AscensionLevel
        };
        // Run RNG streams on every screen, not just in combat: map layout, "?"
        // node contents and the encounter order are all rolled outside a battle.
        // See McpMod.RngStreams.cs. battle.rng_streams carries the same block.
        var runRng = BuildRngStreams(runState);
        if (runRng != null)
            runInfo["rng"] = runRng;
        // Events already seen this run. RoomSet.EnsureNextEventIsValid skips
        // past these when serving the next event, so a client replaying the
        // event queue needs them to name the same event the game will.
        runInfo["visited_event_ids"] = runState.VisitedEventIds.Select(id => id.Entry).ToList();
        result["run"] = runInfo;

        // The act's pre-rolled room queues. These decide which fight and which
        // event sit behind each map node, and they are pure run state, so they
        // ride along on every screen rather than only on the map.
        var actInfo = BuildActState(runState);
        if (actInfo != null)
            result["act"] = actInfo;

        // Completed runs on this profile. UnknownMapPointOdds.Roll forces the
        // first three "?" rooms on a brand-new profile, so a client predicting
        // them has to know whether that override is active.
        TryDo(() =>
        {
            result["profile"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["number_of_runs"] = SaveManager.Instance.Progress.NumberOfRuns,
                ["first_run_ironclad_present"] = HasFirstRunIronclad(runState)
            };
        });

        // Fork addition: save/run context so a client can tie a state read to its save files.
        result["current_run"] = BuildActiveRunContext();

        // Always include full player data (relics, potions, deck, etc.) on every screen
        var _player = LocalContext.GetMe(runState);
        if (_player != null)
        {
            result["player"] = BuildPlayerState(_player);
        }

        AddResolvingState(result, result.GetValueOrDefault("state_type")?.ToString());

        return result;
    }

    /// <summary>
    /// Whether the game is still resolving what was last asked of it.
    ///
    /// Everything else in this state is read straight off the live models, so a read
    /// taken while an action is still animating reports pre-resolution numbers: enemy
    /// HP before a multi-hit finished landing, a hand that has not been redrawn yet.
    /// That is the difference between "cannot kill this turn" and a lethal, and there
    /// was no way to tell a settled state from a mid-animation one.
    ///
    /// is_resolving is false only when the action queue is empty and nothing is
    /// blocking player input. resolving_reasons names what is outstanding.
    /// </summary>
    /// <summary>
    /// Screens that exist to take a decision from the player. The game is parked on
    /// them, not working - reporting "still resolving" here deadlocks any caller that
    /// waits for it to clear, because only the caller can clear it.
    /// </summary>
    private static readonly HashSet<string> _playerInputStates = new(StringComparer.Ordinal)
    {
        "card_select", "hand_select", "bundle_select", "relic_select", "card_reward",
        "rewards", "event", "rest_site", "shop", "fake_merchant", "treasure",
        "crystal_sphere", "map", "menu", "game_over"
    };

    private static void AddResolvingState(Dictionary<string, object?> result, string? stateType)
    {
        var reasons = new List<string>();

        TryDo("is_resolving.action_queue", () =>
        {
            var synchronizer = RunManager.Instance.ActionQueueSynchronizer;
            var queueSet = GetInstanceFieldValue(synchronizer, "_actionQueueSet");
            if (queueSet != null && GetPropertyValue(queueSet, "IsEmpty") is bool isEmpty && !isEmpty
                && !IsActionQueueWaitingForPlayer(queueSet))
                reasons.Add("action_queue_not_empty");
        });

        TryDo("is_resolving.combat", () =>
        {
            var combat = CombatManager.Instance;
            if (combat.IsInProgress)
            {
                if (combat.PlayerActionsDisabled) reasons.Add("player_actions_disabled");
                if (combat.IsStarting) reasons.Add("combat_starting");
                if (combat.IsOverOrEnding) reasons.Add("combat_ending");
            }
        });

        TryDo("is_resolving.card_play", () =>
        {
            var hand = NPlayerHand.Instance;
            if (hand != null && hand.InCardPlay)
                reasons.Add("card_still_being_played");
        });

        // A screen that is waiting for the player is not "resolving", whatever the
        // queue says: the action sitting in the queue is the one paused on this very
        // screen, and it cannot progress until the caller answers it.
        if (stateType != null && _playerInputStates.Contains(stateType))
            reasons.Clear();

        // A submitted reward choice is resolving until the public completion signal or the
        // next OptionSelected wait. Merely displaying a choice is still an input boundary.
        if (stateType is "rewards" or "card_reward" && _rewardSession.Entries.Any(e => e.Resolving))
            reasons.Add("reward_selection_resolving");

        result["is_resolving"] = reasons.Count > 0;
        if (reasons.Count > 0)
            result["resolving_reasons"] = reasons;
    }

    /// <summary>
    /// Epoch reveal counts and one row per epoch for the timeline screen.
    /// Reads the save's progress, which is stable rather than hover-dependent, and
    /// returns an empty payload when that save is not loaded.
    /// </summary>
    private static Dictionary<string, object?> BuildTimelineEpochState()
    {
        return TryRead<Dictionary<string, object?>>("menu.timeline.epochs", () =>
        {
            var epochState = new Dictionary<string, object?>(StringComparer.Ordinal);
            var progress = SaveManager.Instance?.Progress;
            if (progress == null)
                return epochState;

            var epochList = new List<Dictionary<string, object?>>();
            var revealedCount = 0;
            var obtainedCount = 0;
            var lockedCount = 0;
            var noSlotCount = 0;
            foreach (var epoch in progress.Epochs)
            {
                var eraName = epoch.Id;
                var state = epoch.State.ToString();
                // Clean up ID to readable name.
                var name = System.Text.RegularExpressions.Regex.Replace(eraName, @"\d+$", "", System.Text.RegularExpressions.RegexOptions.None, RegexTimeout);
                name = System.Text.RegularExpressions.Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ", System.Text.RegularExpressions.RegexOptions.None, RegexTimeout);

                switch (epoch.State)
                {
                    case EpochState.Revealed:
                        revealedCount++;
                        break;
                    case EpochState.Obtained:
                    case EpochState.ObtainedNoSlot:
                        obtainedCount++;
                        break;
                    case EpochState.NotObtained:
                        lockedCount++;
                        break;
                    case EpochState.NoSlot:
                        noSlotCount++;
                        break;
                }

                epochList.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = eraName,
                    ["name"] = name,
                    ["state"] = state,
                    ["obtained"] = epoch.ObtainDate
                });
            }

            epochState["epochs"] = epochList;
            epochState["total_slots"] = epochList.Count;
            epochState["completed_count"] = revealedCount;
            epochState["revealed_count"] = revealedCount;
            epochState["obtained_unrevealed_count"] = obtainedCount;
            epochState["locked_count"] = lockedCount;
            epochState["no_slot_count"] = noSlotCount;
            return epochState;
        }) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// True when the action queue is non-empty only because an action is parked
    /// waiting for a player choice (a card selection, a bundle pick, ...).
    /// </summary>
    private static bool IsActionQueueWaitingForPlayer(object queueSet)
    {
        return TryRead<bool?>("is_resolving.actions_waiting_for_player", () =>
        {
            if (GetInstanceFieldValue(queueSet, "_actionsWaitingForResumption") is not System.Collections.IEnumerable waiting)
                return false;

            return waiting.GetEnumerator().MoveNext();
        }) ?? false;
    }

    /// <summary>
    /// The settings screen lives on a submenu stack (NMainMenuSubmenuStack outside a run,
    /// NRunSubmenuStack inside one), not on the overlay stack and not in the room, so
    /// nothing else in this state builder notices it. Left unreported, the run-time path
    /// describes the room underneath and every action sent against that description is
    /// applied to a screen the player cannot see.
    ///
    /// Returns null when the screen is not up, so callers can fall through unchanged.
    /// A sibling helper can be added here for the pause menu, which shares the stack.
    /// </summary>
    private static Dictionary<string, object?>? TryBuildSettingsMenuState(Node? root, bool inRun)
    {
        if (root == null)
            return null;

        var settingsScreen = FindFirst<NSettingsScreen>(root);
        if (settingsScreen == null || !IsNodeVisible(settingsScreen))
            return null;

        return BuildSettingsMenuState(settingsScreen, inRun);
    }

    /// <summary>
    /// "back" is the only option exposed. Tabs, tickboxes and sliders are deliberately
    /// not modelled - the point here is that the API can leave a screen it can enter.
    /// </summary>
    private static Dictionary<string, object?> BuildSettingsMenuState(NSettingsScreen settingsScreen, bool inRun)
    {
        var backButton = GetSubmenuBackButton(settingsScreen);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state_type"] = "menu",
            ["menu_screen"] = "settings",
            ["in_run"] = inRun,
            ["message"] = inRun
                ? "Settings screen. The run is paused underneath; 'back' returns to it."
                : "Settings screen.",
            ["options"] = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { ["name"] = "back", ["enabled"] = backButton?.IsEnabled ?? true }
            }
        };
    }

    /// <summary>
    /// The back button of any screen on a submenu stack - the one exit every such screen
    /// is guaranteed to have. _backButton is declared on NSubmenu, not on the concrete
    /// screens; GetInstanceFieldValue walks base types, and the tree search is the
    /// fallback for a future layout change.
    /// </summary>
    private static NClickableControl? GetSubmenuBackButton(NSubmenu submenu)
    {
        var clickable = TryRead<NClickableControl?>("menu.settings.back_button",
            () => GetInstanceFieldValue(submenu, "_backButton") as NClickableControl);

        return clickable ?? FindFirst<NBackButton>(submenu);
    }

    /// <summary>
    /// The submenu currently covering the run, or null when nothing is.
    ///
    /// NCapstoneContainer is the authority - NTopBarPauseButton.IsOpen() reads the same
    /// property. A visibility test alone is not enough: NCapstoneSubmenuStack.ShowScreen
    /// pushes the screen before NCapstoneContainer.Open reparents it, so the menu is
    /// live before it is visible in the tree. The node search is the fallback.
    /// </summary>
    internal static NSubmenu? GetOpenRunSubmenu(Node? root)
    {
        // No capstone container outside a run is the normal case, so this read is quiet.
        var capstone = TryReadQuiet(() =>
        {
            if (NCapstoneContainer.Instance?.CurrentCapstoneScreen is not NCapstoneSubmenuStack submenuStack)
                return null;

            var top = submenuStack.Stack?.Peek();
            return top != null && IsLiveNode(top) ? top : null;
        });

        if (capstone != null)
            return capstone;

        if (root == null)
            return null;

        var pauseMenu = FindFirst<NPauseMenu>(root);
        return pauseMenu != null && IsNodeVisible(pauseMenu) ? pauseMenu : null;
    }

    /// <summary>
    /// The pause menu (top-bar gear / Esc) is pushed on the run's submenu stack inside
    /// the capstone container, exactly like the settings screen, so nothing else in this
    /// builder notices it. Left unreported, state describes the room underneath while
    /// the player is looking at a paused menu, and every action is aimed at a screen
    /// that is not on top. See A-5 of the operator-side issue log.
    ///
    /// Anything else pushed on that stack (the in-run compendium and the screens it
    /// opens) is reported generically with a `back` option, so entering one can never
    /// become a screen the API has no way out of.
    ///
    /// Returns null when no such screen is up, so callers fall through unchanged.
    /// </summary>
    private static Dictionary<string, object?>? TryBuildPauseMenuState(Node? root)
    {
        var submenu = GetOpenRunSubmenu(root);
        if (submenu == null)
            return null;

        return submenu is NPauseMenu pauseMenu
            ? BuildPauseMenuState(pauseMenu)
            : BuildRunSubmenuState(submenu);
    }

    /// <summary>
    /// The six NPauseMenu button fields, in the order NPauseMenu.Buttons declares them.
    /// </summary>
    private static readonly (string Field, string Name)[] _pauseMenuButtons =
    {
        ("_resumeButton", "resume"),
        ("_settingsButton", "settings"),
        ("_compendiumButton", "compendium"),
        ("_giveUpButton", "give_up"),
        ("_disconnectButton", "disconnect"),
        ("_saveAndQuitButton", "save_and_quit")
    };

    private static List<Dictionary<string, object?>> BuildPauseMenuOptions(NPauseMenu pauseMenu)
    {
        var options = new List<Dictionary<string, object?>>();
        foreach (var (field, name) in _pauseMenuButtons)
        {
            // Only the button's own Visible flag is read. NPauseMenu._Ready and
            // Initialize set it per game mode (give_up / save_and_quit are hidden
            // for multiplayer clients, disconnect only for them, compendium until
            // it is unlocked), while IsVisibleInTree is still false on the frame
            // the menu is being reparented into the capstone container - which
            // would report a pause menu with no options at all.
            TryDo($"menu.pause.{field}", () =>
            {
                if (GetInstanceFieldValue(pauseMenu, field) is not Control button || !button.Visible)
                    return;

                options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["enabled"] = (button as NClickableControl)?.IsEnabled ?? true
                });
            });
        }
        return options;
    }

    private static Dictionary<string, object?> BuildPauseMenuState(NPauseMenu pauseMenu)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state_type"] = "menu",
            ["menu_screen"] = "pause",
            ["in_run"] = true,
            ["message"] =
                "Run paused. 'resume' closes this menu and returns to the run; 'settings' and "
                + "'compendium' open screens that 'back' leaves again; 'give_up' abandons the run "
                + "permanently (it asks for confirmation first, answered with yes/no); "
                + "'save_and_quit' returns to the main menu, where 'continue' resumes this run.",
            ["options"] = BuildPauseMenuOptions(pauseMenu)
        };
    }

    /// <summary>
    /// Any other screen on the run's submenu stack (compendium and what it opens).
    /// Only `back` is modelled - the point is that the API can leave a screen it can
    /// enter, not that it can drive the compendium.
    /// </summary>
    private static Dictionary<string, object?> BuildRunSubmenuState(NSubmenu submenu)
    {
        var screen = SubmenuScreenName(submenu);
        var backButton = GetSubmenuBackButton(submenu);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state_type"] = "menu",
            ["menu_screen"] = screen,
            ["in_run"] = true,
            ["screen_class"] = submenu.GetType().Name,
            ["message"] = $"{screen} screen, opened from the pause menu. The run is paused "
                          + "underneath; 'back' returns to the previous screen.",
            ["options"] = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { ["name"] = "back", ["enabled"] = backButton?.IsEnabled ?? true }
            }
        };
    }

    /// <summary>
    /// "NCompendiumSubmenu" -> "compendium", "NCardLibrary" -> "card_library". Derived
    /// rather than listed so a screen the game adds later still gets a usable name and a
    /// working `back`, instead of being reported as an unnamed screen with no way out.
    /// </summary>
    private static string SubmenuScreenName(NSubmenu submenu)
    {
        var name = submenu.GetType().Name;
        if (name.Length > 1 && name[0] == 'N' && char.IsUpper(name[1]))
            name = name[1..];
        foreach (var suffix in new[] { "Submenu", "Screen" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                name = name[..^suffix.Length];
        }

        name = System.Text.RegularExpressions.Regex
            .Replace(name, "(?<=[a-z0-9])(?=[A-Z])", "_", System.Text.RegularExpressions.RegexOptions.None, RegexTimeout)
            .ToLowerInvariant();
        return name.Length > 0 ? name : "submenu";
    }

    /// <summary>Describe one character-select button, or null when it carries no
    /// visible character. Shared by the standard character select and the custom
    /// run screen, which build their button rows from the same scene.</summary>
    private static Dictionary<string, object?>? BuildCharacterButtonEntry(NCharacterSelectButton btn)
    {
        return TryRead(() =>
        {
            if (btn.Character is not { } cm || !IsNodeVisible(btn))
                return null;

            var charData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = SafeGetText(() => cm.Title),
                ["id"] = cm.Id.Entry,
                ["locked"] = btn.IsLocked,
                ["hp"] = cm.StartingHp,
                ["gold"] = cm.StartingGold,
                ["energy"] = cm.MaxEnergy,
                ["description"] = SafeGetText(() => cm.CardsModifierDescription),
            };

            var startRelics = new List<Dictionary<string, object?>>();
            foreach (var relic in cm.StartingRelics)
            {
                startRelics.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = SafeGetText(() => relic.Title),
                    ["description"] = SafeGetText(() => relic.DynamicDescription)
                });
            }
            if (startRelics.Count > 0)
                charData["starting_relics"] = startRelics;

            var deckCards = new List<string>();
            foreach (var card in cm.StartingDeck)
                deckCards.Add(SafeGetText(() => card.Title) ?? "?");
            if (deckCards.Count > 0)
                charData["starting_deck"] = deckCards;

            TryDo(() =>
            {
                var allCards = cm.CardPool?.AllCards;
                if (allCards != null)
                    charData["total_cards"] = System.Linq.Enumerable.Count(allCards);
            });

            TryDo(() =>
            {
                var allRelics = cm.RelicPool?.AllRelics;
                if (allRelics != null)
                    charData["total_relics"] = System.Linq.Enumerable.Count(allRelics);
            });

            TryDo(() =>
            {
                var allPotions = cm.PotionPool?.AllPotions;
                if (allPotions != null)
                    charData["total_potions"] = System.Linq.Enumerable.Count(allPotions);
            });

            return charData;
        });
    }

    private static void AddCharacterSelectMenuState(
        Dictionary<string, object?> result,
        NCharacterSelectScreen charSelect)
    {
        result["state_type"] = "menu";
        result["menu_screen"] = "character_select";
        result["message"] = "Select a character.";
        AddStartRunScreenState(result, charSelect, "_embarkButton");
    }

    // Custom mode setup. Structurally a character-select variant (same character
    // buttons, ascension panel, back/unready/lobby) plus a seed field and run
    // modifiers. Confirm is _confirmButton (the plain char select uses _embarkButton).
    private static void AddCustomRunMenuState(
        Dictionary<string, object?> result,
        NCustomRunScreen customRun)
    {
        result["state_type"] = "menu";
        result["menu_screen"] = "custom_run";
        result["message"] = "Custom run setup. Select a character, optionally toggle run modifiers "
                            + "(modifier_<key>) and adjust ascension, then confirm to embark.";
        AddStartRunScreenState(result, customRun, "_confirmButton");
    }

    // Shared body of the two "start a run" screens (NCharacterSelectScreen and
    // NCustomRunScreen). They are separate types but expose the same controls under
    // the same field names — _backButton, _unreadyButton, _ascensionPanel, _lobby —
    // so everything except the confirm button's field name is identical. Fields that
    // only one screen owns (_seedInput, the modifier tickboxes) are emitted only when
    // that screen actually has them.
    private static void AddStartRunScreenState(
        Dictionary<string, object?> result,
        Node screen,
        string confirmField)
    {
        var buttons = FindAll<NCharacterSelectButton>(screen);
        var characters = new List<Dictionary<string, object?>>();
        var options = new List<Dictionary<string, object?>>();
        PopulateCharacterButtons(buttons, options, characters);
        if (characters.Count > 0)
            result["characters"] = characters;

        var lobby = GetInstanceFieldValue(screen, "_lobby") as StartRunLobby;

        AppendSeed(result, screen, lobby);
        var selectedModifiers = AppendRunModifiers(result, options, screen);
        AppendAscensionState(result, options, screen);
        AddStartRunButtons(options, screen, confirmField);

        // MP lobby block — surfaces roster / ready state / ascension when this screen
        // is part of a host or client lobby. SP runs leave the field absent.
        AppendLobbyBlock(result, options, screen, lobby);

        var selected = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["character"] = GetSelectedCharacterId(buttons)
        };
        if (selectedModifiers.Count > 0)
            selected["modifiers"] = selectedModifiers;
        result["selected"] = selected;

        if (options.Count > 0)
            result["options"] = options;
    }

    /// <summary>
    /// Seed — custom run only, gated on the screen owning a seed input. The lobby is what
    /// the run actually uses and both directions flow through it (SetSeed -> SeedChanged
    /// -> _seedInput.Text, and a human submitting the field -> SetSeed), so read it from
    /// there rather than off the LineEdit.
    /// </summary>
    private static void AppendSeed(
        Dictionary<string, object?> result, Node screen, StartRunLobby? lobby)
    {
        if (GetInstanceFieldValue(screen, "_seedInput") != null && !string.IsNullOrWhiteSpace(lobby?.Seed))
            result["seed"] = lobby.Seed;
    }

    /// <summary>
    /// Run modifiers — custom run only. Each is a tickbox; surface it as read info (id,
    /// title, description, ticked) plus a toggle option named "modifier_&lt;key&gt;", which
    /// menu_select flips via ForceClick. `key` is the id except for the per-character
    /// "character cards" modifiers, which all share one id — see GetRunModifierEntries.
    /// </summary>
    private static List<string> AppendRunModifiers(
        Dictionary<string, object?> result,
        List<Dictionary<string, object?>> options,
        Node screen)
    {
        var selectedModifiers = new List<string>();
        var modifiers = new List<Dictionary<string, object?>>();
        var modifierEntries = GetRunModifierEntries(screen);
        foreach (var entry in modifierEntries)
        {
            TryDo(() =>
            {
                bool ticked = entry.Tickbox.IsTicked;
                var modifierState = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = entry.Id,
                    ["key"] = entry.Key,
                    ["option"] = ModifierOptionPrefix + entry.Key,
                    ["title"] = SafeGetText(() => entry.Modifier.Title),
                    ["description"] = SafeGetText(() => entry.Modifier.Description),
                    ["ticked"] = ticked
                };
                if (!string.IsNullOrEmpty(entry.CharacterId))
                    modifierState["character"] = entry.CharacterId;
                modifiers.Add(modifierState);

                options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = ModifierOptionPrefix + entry.Key,
                    ["enabled"] = entry.Tickbox.IsEnabled
                });
                if (ticked)
                    selectedModifiers.Add(entry.Key);
            });
        }
        if (modifiers.Count > 0)
            result["modifiers"] = modifiers;
        return selectedModifiers;
    }

    /// <summary>
    /// Ascension. The panel is shared by both screens and drives the run's ascension level
    /// in SP as well as MP (the MP `lobby` block carries the synced value). The arrows hide
    /// themselves at the ends of the range and for MP clients, so their visibility is what
    /// says whether the level can still be changed.
    /// </summary>
    private static void AppendAscensionState(
        Dictionary<string, object?> result,
        List<Dictionary<string, object?>> options,
        Node screen)
    {
        if (GetInstanceFieldValue(screen, "_ascensionPanel") is not NAscensionPanel ascensionPanel)
            return;

        TryDo(() =>
        {
            // Flat fields, as documented in docs/raw-full.md: `ascension` is the level
            // the run will start at, `max_ascension` the unlocked cap, and
            // `ascension_selectable` says whether the panel is on screen right now.
            result["ascension"] = ascensionPanel.Ascension;
            if (GetInstanceFieldValue(ascensionPanel, "_maxAscension") is int maxAsc)
                result["max_ascension"] = maxAsc;

            bool selectable = IsNodeVisible(ascensionPanel);
            result["ascension_selectable"] = selectable;

            if (selectable)
            {
                AddAscensionArrowOption(options, ascensionPanel, "_rightArrow", "ascension_up");
                AddAscensionArrowOption(options, ascensionPanel, "_leftArrow", "ascension_down");
            }
        });
    }

    private static void AddStartRunButtons(
        List<Dictionary<string, object?>> options, Node screen, string confirmField)
    {
        var confirmBtn = GetInstanceFieldValue(screen, confirmField);
        if (confirmBtn is NClickableControl confirmClickable && IsNodeVisible(confirmClickable))
        {
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "confirm",
                ["enabled"] = confirmClickable.IsEnabled
            });
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "embark",
                ["enabled"] = confirmClickable.IsEnabled
            });
        }

        // _backButton and _unreadyButton are surfaced as distinct options so MP callers
        // can distinguish "leave the lobby" from "retract my ready vote". In SP, only
        // _backButton ever becomes enabled. See NCharacterSelectScreen.OnEmbarkPressed /
        // OnUnreadyPressed for the toggle logic.
        var backBtn = GetInstanceFieldValue(screen, "_backButton");
        if (backBtn is NClickableControl backClickable && IsNodeVisible(backClickable))
        {
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "back",
                ["enabled"] = backClickable.IsEnabled
            });
        }
    }

    /// <summary>
    /// Reports the lobby block when this screen belongs to a host or client lobby, and
    /// adds the "unready" option then.
    /// </summary>
    private static void AppendLobbyBlock(
        Dictionary<string, object?> result,
        List<Dictionary<string, object?>> options,
        Node screen,
        StartRunLobby? lobby)
    {
        bool isMp = false;
        TryDo(() =>
        {
            if (lobby != null && lobby.NetService != null && lobby.NetService.Type.IsMultiplayer())
            {
                isMp = true;
                result["lobby"] = BuildStartRunLobbyState(lobby);
            }
        });

        // _unreadyButton is part of the scene in SP too but never becomes enabled there.
        // Only surface it as an option in MP, where it has a real role.
        if (isMp)
        {
            var unreadyBtn = GetInstanceFieldValue(screen, "_unreadyButton");
            if (unreadyBtn is NClickableControl unreadyClickable && IsNodeVisible(unreadyClickable))
            {
                options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = "unready",
                    ["enabled"] = unreadyClickable.IsEnabled
                });
            }
        }
    }

    // NAscensionPanel hides an arrow when that direction is unavailable (either end of
    // the range, or an MP client that may not change the level), so a hidden arrow means
    // the option should not be advertised at all.
    private static void AddAscensionArrowOption(
        List<Dictionary<string, object?>> options,
        NAscensionPanel panel,
        string arrowField,
        string optionName)
    {
        if (GetInstanceFieldValue(panel, arrowField) is NClickableControl arrow && IsNodeVisible(arrow))
        {
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = optionName,
                ["enabled"] = arrow.IsEnabled
            });
        }
    }

    // Shared by character select and custom-run setup, which both list the same
    // NCharacterSelectButton controls. Appends one option per selectable character
    // and one rich entry per character to the given lists.
    private static void PopulateCharacterButtons(
        System.Collections.Generic.IEnumerable<NCharacterSelectButton> buttons,
        List<Dictionary<string, object?>> options,
        List<Dictionary<string, object?>> characters)
    {
        foreach (var btn in buttons)
        {
            var charData = BuildCharacterButtonEntry(btn);
            if (charData == null)
                continue;
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = charData["id"],
                ["enabled"] = !(bool)(charData["locked"] ?? false)
            });
            characters.Add(charData);
        }
    }

    // Returns the id of the currently selected character button, or null if none
    // is selected yet. Shared by character select and custom-run setup.
    private static string? GetSelectedCharacterId(
        System.Collections.Generic.IEnumerable<NCharacterSelectButton> buttons)
    {
        foreach (var btn in buttons)
        {
            // A button whose character model is not up yet is skipped, not reported.
            var selectedId = TryReadQuiet(() =>
                btn.IsSelected && btn.Character is { } cm ? cm.Id.Entry : null);
            if (selectedId != null)
                return selectedId;
        }
        return null;
    }

    // v0.111: StartRunLobby no longer exposes a public MaxPlayers; read the private
    // backing field (set from the ctor's maxPlayers arg). Falls back to current count.
    private static int GetStartRunLobbyMaxPlayers(StartRunLobby lobby)
    {
        var maxPlayers = TryRead<int?>("lobby.max_players", () =>
        {
            var f = typeof(StartRunLobby).GetField("_maxPlayers",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return f?.GetValue(lobby) as int?;
        });

        return maxPlayers ?? lobby.Players.Count;
    }

    private static Dictionary<string, object?> BuildStartRunLobbyState(StartRunLobby lobby)
    {
        // v0.111 renamed the roster's element type (LobbyPlayer ->
        // StartRunLobbyPlayer) and dropped the public MaxPlayers. Naming either
        // one directly would bake a typeref/memberref this DLL cannot resolve on
        // the main branch — and the rename is worse than a failed call, because
        // the lambdas over the roster put the element type on a compiler-
        // generated closure, which fails the whole assembly's type enumeration
        // and takes the entire mod down at load. Both are read reflectively; the
        // roster's own field names survived the rename.
        var roster = GetStartRunLobbyPlayers(lobby);

        var lobbyState = BuildLobbySummary(lobby, roster);

        var local = GetStartRunLobbyLocalPlayer(lobby, roster);

        // is_local_ready === local player has hit Embark in MP and is now waiting.
        // Mirrors NCharacterSelectScreen._readyAndWaitingContainer.Visible.
        if (local != null)
        {
            lobbyState["is_local_ready"] = local.IsReady;
            lobbyState["local_player_id"] = local.Id.ToString();
        }

        ulong localId = local?.Id ?? 0;
        ulong hostId = lobby.NetService.Type == NetGameType.Host ? localId : 0;
        lobbyState["players"] = roster.Select(p => BuildLobbyPlayer(lobby, p, localId, hostId)).ToList();
        lobbyState["player_count"] = roster.Count;

        if (!string.IsNullOrEmpty(lobby.Seed))
            lobbyState["seed"] = lobby.Seed;

        return lobbyState;
    }

    private static Dictionary<string, object?> BuildLobbySummary(
        StartRunLobby lobby, List<StartRunLobbySeat> roster)
        => new(StringComparer.Ordinal)
        {
            ["type"] = lobby.NetService.Type switch
            {
                NetGameType.Host => "host",
                NetGameType.Client => "client",
                NetGameType.Singleplayer => "singleplayer",
                _ => lobby.NetService.Type.ToString().ToLowerInvariant()
            },
            ["game_mode"] = lobby.GameMode.ToString().ToLowerInvariant(),
            ["max_players"] = GetStartRunLobbyMaxPlayers(lobby),
            ["ascension"] = lobby.Ascension,
            ["max_ascension"] = lobby.MaxAscension,
            ["all_ready"] = roster.Count > 0 && roster.All(p => p.IsReady),
            ["is_about_to_begin"] = SafeIsAboutToBeginGame(lobby)
        };

    private static Dictionary<string, object?> BuildLobbyPlayer(
        StartRunLobby lobby, StartRunLobbySeat p, ulong localId, ulong hostId)
        => new(StringComparer.Ordinal)
        {
            ["id"] = p.Id.ToString(),
            ["slot_id"] = p.SlotId,
            ["is_local"] = p.Id == localId,
            // We can only positively identify the host as "us" when we ARE the host;
            // a client doesn't know which remote id is the host without inspecting
            // the net service. Keep it simple and only flag is_host=true for self
            // when hosting — clients can infer host-ness by player_id when needed.
            ["is_host"] = p.Id == hostId && hostId != 0,
            ["character"] = SafeGetText(() => p.Character?.Title)
                            ?? p.Character?.Id.Entry,
            ["character_id"] = p.Character?.Id.Entry,
            ["is_ready"] = p.IsReady,
            ["platform_name"] = SafeGetPlayerName(lobby.NetService.Platform, p.Id)
        };

    /// <summary>
    /// One character-select lobby seat, read out of whichever struct this game
    /// build uses for the roster.
    /// </summary>
    private sealed class StartRunLobbySeat
    {
        internal ulong Id;
        internal int SlotId;
        internal bool IsReady;
        internal CharacterModel? Character;
    }

    /// <summary>
    /// The character-select lobby roster, in slot order as the game holds it.
    /// </summary>
    private static List<StartRunLobbySeat> GetStartRunLobbyPlayers(StartRunLobby lobby)
    {
        var seats = new List<StartRunLobbySeat>();
        if (SafeGet(() => GetInstanceMemberValue(lobby, "Players")) is not IEnumerable players)
            return seats;

        foreach (var player in players)
        {
            if (player == null)
                continue;
            var seat = ReadStartRunLobbySeat(player);
            if (seat != null)
                seats.Add(seat);
        }
        return seats;
    }

    /// <summary>
    /// The local player's seat. `LocalPlayer` returns the roster struct itself,
    /// so it is read the same reflective way; a build that ever stops exposing
    /// it falls back to matching the roster against our own net id.
    /// </summary>
    private static StartRunLobbySeat? GetStartRunLobbyLocalPlayer(StartRunLobby lobby, List<StartRunLobbySeat> roster)
    {
        var local = SafeGet(() => GetInstanceMemberValue(lobby, "LocalPlayer"));
        if (local != null)
        {
            var seat = ReadStartRunLobbySeat(local);
            // A struct roster returns default(T) rather than null for a missing
            // seat, which reads as id 0 — not a real player.
            if (seat != null && seat.Id != 0)
                return seat;
        }

        var netId = TryReadQuiet<ulong?>(() => lobby.NetService.NetId);
        if (netId == null)
            return null;

        return roster.Find(p => p.Id == netId.Value);
    }

    private static StartRunLobbySeat? ReadStartRunLobbySeat(object player)
    {
        var id = SafeGet(() => GetInstanceMemberValue(player, "id"));
        if (id == null)
            return null;

        var seat = new StartRunLobbySeat
        {
            Character = SafeGet(() => GetInstanceMemberValue(player, "character")) as CharacterModel,
            IsReady = SafeGet(() => GetInstanceMemberValue(player, "isReady")) as bool? ?? false,
            SlotId = SafeGet(() => GetInstanceMemberValue(player, "slotId")) as int? ?? -1,
        };
        var seatId = TryReadQuiet<ulong?>(() => Convert.ToUInt64(id, CultureInfo.InvariantCulture));
        if (seatId == null)
            return null;

        seat.Id = seatId.Value;
        return seat;
    }

    private static bool SafeIsAboutToBeginGame(StartRunLobby lobby)
        => TryRead<bool?>("lobby.is_about_to_begin", () => lobby.IsAboutToBeginGame()) ?? false;

    private static string? SafeGetPlayerName(PlatformType platform, ulong playerId)
        => TryRead("lobby.players.platform_name",
            () => PlatformUtil.GetPlayerName(platform, playerId));

    private static void AddMultiplayerJoinMenuState(
        Dictionary<string, object?> result,
        NJoinFriendScreen joinScreen)
    {
        result["state_type"] = "menu";
        result["menu_screen"] = "multiplayer_join";

        // FastMP: when Steam isn't initialized OR --fastmp is set, OnSubmenuOpened auto-
        // joins localhost:33771 instead of presenting friends. This is a debug/local-dev
        // path. We surface it so callers don't try to hit "refresh" expecting a list.
        bool fastMp = !SteamInitializer.Initialized || CommandLineHelper.HasArg("fastmp");
        result["fast_mp"] = fastMp;

        var loadingFriends = GetInstanceFieldValue(joinScreen, "_loadingFriendsIndicator") as Control;
        var loadingOverlay = GetInstanceFieldValue(joinScreen, "_loadingOverlay") as Control;
        bool loading = (loadingFriends != null && loadingFriends.Visible)
                       || (loadingOverlay != null && loadingOverlay.Visible);
        result["loading"] = loading;

        var noFriendsLabel = GetInstanceFieldValue(joinScreen, "_noFriendsLabel") as Control;
        bool noFriends = noFriendsLabel != null && noFriendsLabel.Visible;
        result["no_friends"] = noFriends;

        var (friends, options) = BuildJoinFriendOptions(joinScreen);
        result["friends"] = friends;

        var refreshBtn = GetInstanceFieldValue(joinScreen, "_refreshButton") as NClickableControl;
        if (refreshBtn != null && IsNodeVisible(refreshBtn))
        {
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "refresh",
                ["enabled"] = refreshBtn.IsEnabled && !loading
            });
        }
        AddMenuOptionIfVisible(options, joinScreen, "_backButton", "back");

        result["message"] = DescribeJoinMenuMessage(fastMp, loading, noFriends);
        result["options"] = options;
    }

    /// <summary>The joinable friends (and their known option names), in UI order.</summary>
    private static (List<Dictionary<string, object?>> Friends, List<Dictionary<string, object?>> Options)
        BuildJoinFriendOptions(NJoinFriendScreen joinScreen)
    {
        var friends = new List<Dictionary<string, object?>>();
        var options = new List<Dictionary<string, object?>>();
        if (GetInstanceFieldValue(joinScreen, "_buttonContainer") is not Control buttonContainer)
            return (friends, options);

        int index = 0;
        foreach (var child in buttonContainer.GetChildren())
        {
            if (child is not NJoinFriendButton friendBtn)
                continue;

            string? name = null;
            TryDo("menu.multiplayer_join.friends.name", () =>
            {
                name = PlatformUtil.GetPlayerName(PlatformUtil.PrimaryPlatform, friendBtn.PlayerId);
            });

            friends.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = index,
                ["name"] = name,
                ["player_id"] = friendBtn.PlayerId.ToString(),
                ["enabled"] = friendBtn.IsEnabled
            });
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = $"join_{index}",
                ["enabled"] = friendBtn.IsEnabled
            });
            index++;
        }
        return (friends, options);
    }

    private static string DescribeJoinMenuMessage(bool fastMp, bool loading, bool noFriends)
    {
        if (fastMp)
        {
            return loading
                ? "FastMP join flow is connecting to localhost:33771..."
                : "FastMP mode (no Steam): join screen auto-connects to localhost:33771.";
        }
        if (loading)
            return "Refreshing friend list...";
        if (noFriends)
            return "No friends with open lobbies. Use 'refresh' to retry, or 'back' to return.";
        return "Pick a friend to join, or 'refresh' to update the list.";
    }

    private static void AddMultiplayerLoadLobbyMenuState(
        Dictionary<string, object?> result,
        NMultiplayerLoadGameScreen loadLobby)
    {
        result["state_type"] = "menu";
        result["menu_screen"] = "multiplayer_load_lobby";

        if (GetInstanceFieldValue(loadLobby, "_runLobby") is LoadRunLobby lobby)
            result["lobby"] = BuildLoadLobbyInfo(lobby);

        result["options"] = BuildLoadLobbyOptions(loadLobby);
        result["message"] = "Multiplayer load lobby. Confirm to ready up; once everyone is connected and ready, the run resumes.";
    }

    private static Dictionary<string, object?> BuildLoadLobbyInfo(LoadRunLobby lobby)
    {
        var info = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = lobby.NetService.Type switch
            {
                NetGameType.Host => "host",
                NetGameType.Client => "client",
                _ => lobby.NetService.Type.ToString().ToLowerInvariant()
            },
            ["game_mode"] = lobby.GameMode.ToString().ToLowerInvariant(),
            ["ascension"] = lobby.Run?.Ascension ?? 0,
            ["act"] = (lobby.Run?.CurrentActIndex ?? 0) + 1,
            ["floor"] = lobby.Run?.VisitedMapCoords?.Count ?? 0
        };

        TryDo("menu.multiplayer_load_lobby.local_player", () =>
        {
            var localPlayer = lobby.Run?.Players?.FirstOrDefault(p => p.NetId == lobby.NetService.NetId);
            if (localPlayer != null)
            {
                info["character_id"] = localPlayer.CharacterId?.Entry;
                info["current_hp"] = localPlayer.CurrentHp;
                info["max_hp"] = localPlayer.MaxHp;
                info["gold"] = localPlayer.Gold;
            }
        });

        var connectedPlayerIds = TryReadQuiet(() => lobby.PlayerIds.ToHashSet())
            ?? new HashSet<ulong>();

        info["expected_player_count"] = lobby.Run?.Players?.Count ?? 0;
        info["connected_player_count"] = connectedPlayerIds.Count;

        bool aboutToBegin = ResolveAboutToBegin(lobby, connectedPlayerIds);
        info["all_ready"] = aboutToBegin;
        info["is_about_to_begin"] = aboutToBegin;

        // Per-player ready/connected breakdown
        info["players"] = BuildLoadLobbyPlayers(lobby, connectedPlayerIds);
        return info;
    }

    /// <summary>
    /// v0.111 restores LoadRunLobby.IsAboutToBeginGame(); prefer the game's own answer and
    /// keep the "every run player is connected and ready" test as a fallback for a build
    /// where the method is missing or throws.
    /// </summary>
    private static bool ResolveAboutToBegin(LoadRunLobby lobby, HashSet<ulong> connectedPlayerIds)
    {
        bool aboutToBegin = false;
        TryDo("menu.multiplayer_load_lobby.is_about_to_begin", () =>
        {
            aboutToBegin = lobby.IsAboutToBeginGame();
        });

        if (!aboutToBegin)
        {
            TryDo(() =>
            {
                var runPlayers = lobby.Run?.Players;
                aboutToBegin = runPlayers != null
                    && runPlayers.Count > 0
                    && runPlayers.All(player => connectedPlayerIds.Contains(player.NetId) && lobby.IsPlayerReady(player.NetId));
            });
        }
        return aboutToBegin;
    }

    private static List<Dictionary<string, object?>> BuildLoadLobbyPlayers(
        LoadRunLobby lobby, HashSet<ulong> connectedPlayerIds)
    {
        var players = new List<Dictionary<string, object?>>();
        TryDo("menu.multiplayer_load_lobby.players", () =>
        {
            if (lobby.Run?.Players == null)
                return;
            foreach (var sp in lobby.Run.Players)
            {
                bool isConnected = connectedPlayerIds.Contains(sp.NetId);
                bool isReady = TryRead<bool?>("menu.multiplayer_load_lobby.players.is_ready",
                    () => lobby.IsPlayerReady(sp.NetId)) ?? false;
                players.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = sp.NetId.ToString(),
                    ["is_local"] = sp.NetId == lobby.NetService.NetId,
                    ["character_id"] = sp.CharacterId?.Entry,
                    ["is_connected"] = isConnected,
                    ["is_ready"] = isReady,
                    ["platform_name"] = SafeGetPlayerName(lobby.NetService.Platform, sp.NetId)
                });
            }
        });
        return players;
    }

    private static List<Dictionary<string, object?>> BuildLoadLobbyOptions(NMultiplayerLoadGameScreen loadLobby)
    {
        var options = new List<Dictionary<string, object?>>();
        AddOptionIfEnabled(options, loadLobby, "_confirmButton", "confirm", "embark");
        AddOptionIfEnabled(options, loadLobby, "_backButton", "back");
        AddOptionIfEnabled(options, loadLobby, "_unreadyButton", "unready");
        return options;
    }

    /// <summary>
    /// Adds one option per given name, all sharing the field's enabled state and its
    /// visibility.
    /// </summary>
    private static void AddOptionIfEnabled(
        List<Dictionary<string, object?>> options,
        object screen,
        string field,
        params string[] optionNames)
    {
        if (GetInstanceFieldValue(screen, field) is not NClickableControl control || !IsNodeVisible(control))
            return;
        foreach (var name in optionNames)
        {
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["enabled"] = control.IsEnabled
            });
        }
    }

    private static Dictionary<string, object?> BuildBattleState(RunState runState, CombatRoom combatRoom)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        var battle = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (combatState == null)
        {
            battle["error"] = "Combat state unavailable";
            return battle;
        }

        RefreshCardUidRegistry(combatState);
        var localPlayer = LocalContext.GetMe(runState);
        battle["special_card_rewards"] = localPlayer != null && combatRoom.ExtraRewards.TryGetValue(localPlayer, out var extraRewards)
            ? extraRewards.OfType<SpecialCardReward>().Select(BuildSpecialCardReward).ToList()
            : new List<Dictionary<string, object?>>();
        battle["next_creature_id"] = NextCreatureId(combatState);

        battle["round"] = combatState.RoundNumber;
        battle["turn"] = combatState.CurrentSide.ToString().ToLowerInvariant();
        battle["is_play_phase"] = IsCombatPlayPhase();
        ApplyBattleReadiness(battle);

        battle["enemies"] = BuildEnemyList(combatState);
        battle["allies"] = BuildBattleAllies(combatState);
        AppendBattleExtras(battle, runState, combatState);

        return battle;
    }

    /// <summary>
    /// Whether the game will accept an action right now: turn phase, the action queue and
    /// the hand UI all have to agree, and a scripted moment can still refuse input.
    /// </summary>
    private static void ApplyBattleReadiness(Dictionary<string, object?> battle)
    {
        // Scripted combat moments disable player input while turn/is_play_phase still
        // look actionable; clients must wait on this instead of hammering errors.
        bool actionsDisabled = false;
        TryDo(() =>
        {
            actionsDisabled = CombatManager.Instance.PlayerActionsDisabled;
            battle["actions_disabled"] = actionsDisabled;
        });

        // Fork addition: name the first blocker so a controller can see why it cannot end the turn.
        var endTurnBlockedReason = GetEndTurnBlockedReason();
        battle["can_end_turn"] = endTurnBlockedReason == null;
        battle["end_turn_blocked_reason"] = endTurnBlockedReason;

        // Pre-flight readiness so a controller can decide to act without provoking an error.
        var runManager = RunManager.Instance;
        bool executorIdle = runManager.ActionExecutor?.CurrentlyRunningAction == null;
        bool queueEmpty = runManager.ActionQueueSet?.IsEmpty ?? false;
        var handUi = NCombatRoom.Instance?.Ui?.Hand;
        battle["action_running"] = !executorIdle;
        battle["action_queue_empty"] = queueEmpty;
        battle["ready_for_action"] = IsCombatPlayPhase()
            && executorIdle && queueEmpty
            && !actionsDisabled
            && handUi != null && !handUi.InCardPlay
            && handUi.CurrentMode == NPlayerHand.Mode.Play;
    }

    /// <summary>
    /// The player's side of the board. Only `enemies` was reported, so Osty was invisible
    /// to anything reading this: a Necrobinder capture could not see the pet at all. Built
    /// with the same shape as an enemy so a consumer can treat them alike.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildBattleAllies(CombatState combatState)
    {
        var allies = new List<Dictionary<string, object?>>();
        foreach (var creature in combatState.Allies)
        {
            // The player's own creature is already reported under `player`; what is new
            // here is the pets, which nothing else exposes.
            if (creature.IsAlive && !creature.IsPlayer)
                allies.Add(BuildEnemyState(creature));
        }
        return allies;
    }

    /// <summary>
    /// The encounter, RNG and card-history blocks, each of which the game can refuse to
    /// produce while combat is tearing down.
    /// </summary>
    private static void AppendBattleExtras(
        Dictionary<string, object?> battle, RunState runState, CombatState combatState)
    {
        // The encounter's slot list, in declaration order. `EncounterModel
        // .GetNextSlot` is `Slots.FirstOrDefault(s => Enemies.All(c => c.SlotName
        // != s), "")`, so a consumer holding this plus each enemy's own slot can
        // reproduce where a summoned creature lands — and, more importantly,
        // when the board is full and a move that summons produces nothing at
        // all. Without it a simulated Living Fog would keep bloating forever.
        TryDo(() =>
        {
            var slots = combatState.Encounter?.Slots;
            if (slots != null && slots.Count > 0)
                battle["encounter_slots"] = new List<string>(slots);
        });

        // Run RNG stream coordinates (seed/counter per stream) so the planner
        // can resolve shuffle/target randomness exactly. See McpMod.RngStreams.cs.
        var rngStreams = BuildRngStreams(runState);
        if (rngStreams != null)
            battle["rng_streams"] = rngStreams;

        // Gold Axe and other combat-wide counters include every player's card
        // plays. Singleplayer now exposes the same history shape as MP.
        TryDo(() => battle["card_plays"] = BuildCardPlaysState(combatState));
        TryDo(() => battle["card_history"] = BuildCardHistoryState(combatState));
        /* both are unavailable while combat is tearing down */
    }

    /// <summary>
    /// Whether one of this player's card or potion effects is still resolving.
    /// </summary>
    /// <remarks>
    /// A polled client sees whatever frame it lands on, so it cannot tell a
    /// finished action from one the game is halfway through. This is the game's
    /// own answer to that, and it is the union of the two windows because
    /// neither covers the other:
    ///
    /// CombatManager.IsExecutingCardOrPotionEffect wraps the effect BODY --
    /// CardModel.OnPlay and PotionModel.OnUse, nested auto-plays included -- and
    /// is the only signal that covers potions at all, since nothing about a
    /// potion touches a card pile.
    ///
    /// The Play pile covers what surrounds that body. OnPlayWrapper puts the
    /// card there before the first execution and files it to its result pile
    /// only after the last one, so it spans the repeats of a Burst or Echo Form
    /// play and the pile-move animation on either side. That animation is why
    /// this is not academic at Instant speed: AppendPileLerpTween gives a card
    /// entering Play a hard-coded 0.25s scale tween that FastMode does not
    /// scale, and AddDuringManualCardPlay awaits it for Power cards.
    ///
    /// True while a selection overlay is up as well, since the play that opened
    /// it genuinely has not finished. Callers that gate on this must therefore
    /// exclude the overlay states, or they will wait for a screen that is
    /// waiting for them; HandleGetState does it by settling only plain combat
    /// state types.
    /// </remarks>
    private static bool PlayerIsResolvingEffect(Player player)
    {
        return TryRead<bool?>(() =>
        {
            if (CombatManager.Instance.IsExecutingCardOrPotionEffect(player))
                return true;
            return player.PlayerCombatState?.PlayPile.Cards.Count > 0;
        }) ?? false;
    }

    private static readonly FieldInfo NextCreatureIdField = typeof(CombatState)
        .GetField("_nextCreatureId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(CombatState).FullName, "_nextCreatureId");

    private static uint NextCreatureId(object combatState) =>
        NextCreatureIdField.GetValue(combatState) is uint value
            ? value
            : throw new InvalidOperationException("CombatState next creature identity is unavailable");

    private static readonly FieldInfo OwnerEffectDepthField = typeof(CombatManager)
        .GetField("_cardOrPotionEffectDepth", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(CombatManager).FullName, "_cardOrPotionEffectDepth");

    private static int OwnerEffectDepth(Player player)
    {
        var depths = OwnerEffectDepthField.GetValue(CombatManager.Instance) as Dictionary<Player, int>
            ?? throw new InvalidOperationException("CombatManager owner effect depths are unavailable");
        var depth = depths.TryGetValue(player, out var value) ? value : 0;
        if (depth < 0) throw new InvalidOperationException("CombatManager owner effect depth is negative");
        return depth;
    }

    private static string? GetEndTurnBlockedReason()
    {
        if (!CombatManager.Instance.IsInProgress)
            return "NotInCombat";
        if (!IsCombatPlayPhase())
            return "NotInPlayPhase";
        if (CombatManager.Instance.PlayerActionsDisabled)
            return "PlayerActionsDisabled";

        var hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand != null && hand.InCardPlay)
            return "CardInPlay";
        if (hand != null && hand.CurrentMode != NPlayerHand.Mode.Play)
            return "HandSelectionMode";

        return null;
    }

    private static Dictionary<string, object?> BuildPlayerState(Player player)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        var creature = player.Creature;
        var combatState = player.PlayerCombatState;

        state["character"] = SafeGetText(() => player.Character.Title);
        state["character_id"] = player.Character.Id.Entry;
        state["player_id"] = player.NetId.ToString();
        state["generation_pool"] = BuildGenerationPool(player);
        state["combat_id"] = creature.CombatId;
        state["hp"] = creature.CurrentHp;
        state["effect_depth"] = OwnerEffectDepth(player);
        state["turn_phase"] = combatState?.Phase.ToString() ?? "None";
        state["block_gains_this_turn"] = BuildOwnerBlockGains(player);
        state["max_hp"] = creature.MaxHp;
        state["block"] = creature.Block;

        // Player-scoped RNG (Rewards / Shops / Transformations) + the odds
        // accumulators those rolls are compared against, so card/potion/shop
        // rewards are predictable. See McpMod.RngStreams.cs.
        var playerRng = BuildPlayerRngStreams(player);
        if (playerRng != null)
            state["rng"] = playerRng;

        // The run deck (with its master-deck `index`) is emitted further down by
        // BuildDeckCardList, which reads the same deck through the player model.
        // PlayerCombatState can linger after combat while on map/rest/shop. Energy/MaxEnergy getters
        // run hooks (e.g. Hook.ModifyMaxEnergy) that null-ref without a live combat - only serialize
        // combat fields when a fight is actually in progress.
        bool inCombat = false;
        if (combatState != null && CombatManager.Instance.IsInProgress)
        {
            inCombat = true;
            state["energy"] = combatState.Energy;
            state["max_energy"] = combatState.MaxEnergy;

            // This required history has a different predicate and lifetime
            // from Spite's actual HP loss during the current side's turn.
            state["damage_not_fully_blocked_this_owner_turn"] = DamageNotFullyBlockedThisOwnerTurn(
                player, CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>());

            // Card models query combat history directly for these conditions.
            // Export the owner-scoped values so the native forward model can
            // seed an exact mid-turn search instead of inferring from HP/piles.
            TryDo(() =>
            {
                var history = CombatManager.Instance.History.Entries;
                state["lost_hp_this_turn"] = history.OfType<DamageReceivedEntry>().Any(entry =>
                    entry.HappenedThisTurn(creature.CombatState)
                    && entry.Receiver == creature
                    && entry.Result.UnblockedDamage > 0);
                state["cards_created_this_combat"] = history.OfType<CardGeneratedEntry>().Count(entry =>
                    entry.Creator == player);
                state["lightning_channeled_this_combat"] = history.OfType<OrbChanneledEntry>().Count(entry =>
                    entry.Actor.Player == player && entry.Orb is LightningOrb);
            });
            // History can disappear while combat tears down. The Rust
            // builder treats omitted counters as zero on those inert states.

            // Counts every turn THIS player takes, extra turns included (battle.round
            // only ticks on a normal side switch). Clients use it as the "a new player
            // turn began" edge, which they cannot get from is_play_phase alone: with
            // Instant Mode the enemy phase can start and finish between two polls.
            state["turn_number"] = combatState.TurnNumber;

            // Stars (The Regent's resource, conditionally shown)
            if (player.Character.ShouldAlwaysShowStarCounter || combatState.Stars > 0)
            {
                state["stars"] = combatState.Stars;
            }

            // Hand. "upgrade_preview" rides along only while something in reach can upgrade
            // cards in hand this turn (D-13): otherwise it is dead weight on every combat state.
            var hand = new List<Dictionary<string, object?>>();
            bool previewHandUpgrades = CanUpgradeHandThisTurn(player, combatState.Hand.Cards);
            int cardIndex = 0;
            foreach (var card in combatState.Hand.Cards)
            {
                var cardState = BuildCardState(card, cardIndex, player);
                if (previewHandUpgrades) AttachUpgradePreview(cardState, card);
                hand.Add(cardState);
                cardIndex++;
            }
            state["hand"] = hand;

            // Pile counts
            state["draw_pile_count"] = combatState.DrawPile.Cards.Count;
            state["discard_pile_count"] = combatState.DiscardPile.Cards.Count;
            state["exhaust_pile_count"] = combatState.ExhaustPile.Cards.Count;
            state["play_pile_count"] = combatState.PlayPile.Cards.Count;
            state["is_resolving"] = PlayerIsResolvingEffect(player);

            // Turn-history counters for cards whose effects depend on what already
            // happened this turn (Finisher counts attacks; Forgotten Ritual checks
            // exhausts). Mirrors the game's own queries in those card models.
            TryDo(() =>
            {
                var history = CombatManager.Instance.History;
                var combatStateShared = creature.CombatState;
                state["cards_started_this_turn"] = history.CardPlaysStarted.Count(en =>
                    en.HappenedThisTurn(combatStateShared) && en.CardPlay.Card.Owner == player);
                state["skills_started_this_turn"] = history.CardPlaysStarted.Count(en =>
                    en.HappenedThisTurn(combatStateShared) && en.CardPlay.Card.Owner == player
                    && en.CardPlay.Card.Type == CardType.Skill);
                state["card_series_started_this_turn"] = history.CardPlaysStarted.Count(en =>
                    en.HappenedThisTurn(combatStateShared) && en.CardPlay.Card.Owner == player && en.CardPlay.IsFirstInSeries);
                state["zero_cost_attacks_started_this_turn"] = history.CardPlaysStarted.Count(en =>
                    en.HappenedThisTurn(combatStateShared) && en.CardPlay.Card.Owner == player
                    && en.CardPlay.Card.Type == CardType.Attack && en.CardPlay.Resources.EnergyValue == 0);
                state["attacks_played_this_turn"] = history.CardPlaysFinished.Count(en =>
                    en.HappenedThisTurn(combatStateShared)
                    && en.CardPlay.Card.Type == CardType.Attack
                    && en.CardPlay.Card.Owner == player);
                state["energy_spent_this_turn"] = history.Entries.OfType<EnergySpentEntry>()
                    .Where(en => en.HappenedThisTurn(combatStateShared) && en.Actor.Player == player)
                    .Sum(en => en.Amount);
                state["exhausted_this_turn"] = history.Entries.OfType<CardExhaustedEntry>().Any(en =>
                    en.HappenedThisTurn(combatStateShared) && en.Card.Owner == player);
                state["cards_drawn_this_turn"] = history.Entries.OfType<CardDrawnEntry>().Count(en =>
                    en.HappenedThisTurn(combatStateShared) && en.Card.Owner == player && !en.FromHandDraw);
                state["cards_drawn_this_combat"] = history.Entries.OfType<CardDrawnEntry>().Count(en =>
                    en.Card.Owner == player);
                // Stars GAINED this turn (Radiate's hit count). The positive
                // StarsModifiedEntry sum, which is neither the current Star total
                // nor the Stars spent - a turn can gain and spend the same Star.
                state["stars_gained_this_turn"] = history.Entries.OfType<StarsModifiedEntry>()
                    .Where(en => en.HappenedThisTurn(combatStateShared) && en.Amount > 0 && en.Actor == creature)
                    .Sum(en => en.Amount);
                // Times this player took unblocked damage THIS COMBAT (Tear
                // Asunder's hit count). A count of events, not HP lost, and a
                // fully blocked hit does not count.
                state["unblocked_hits_taken_this_combat"] = history.Entries.OfType<DamageReceivedEntry>()
                    .Count(en => en.Receiver == creature && en.Result.UnblockedDamage > 0);
                state["doom_applied_this_turn"] = history.Entries.OfType<PowerReceivedEntry>().Any(en =>
                    en.HappenedThisTurn(combatStateShared)
                    && en.Applier == creature
                    && string.Equals(en.Power.GetType().Name, "DoomPower", StringComparison.Ordinal));
                // Whether the end-of-turn flush will run at all: a hook can
                // suppress it, in which case the WHOLE hand is kept and no card
                // left in it should read as discarded.
                if (combatStateShared != null)
                    state["hand_will_flush"] = Hook.ShouldFlush(combatStateShared, player);
            });
            // History unavailable mid-transition - omit rather than fail the
            // whole state build; the bot treats missing keys as 0/false.

            // Pile contents. Draw pile is serialized in true order (index 0 = next draw).
            // Note: the order is only valid until the next shuffle event (empty-pile
            // reshuffle or an effect that shuffles cards into the draw pile).
            state["draw_pile"] = BuildPileCardList(combatState.DrawPile.Cards, PileType.Draw);
            state["discard_pile"] = BuildPileCardList(combatState.DiscardPile.Cards, PileType.Discard);
            state["exhaust_pile"] = BuildPileCardList(combatState.ExhaustPile.Cards, PileType.Exhaust);
            state["play_pile"] = BuildPileCardList(combatState.PlayPile.Cards, PileType.Play);

            // Alchemize uses this exact ordered unlocked domain before its
            // rarity and item rolls on CombatPotionGeneration.
            TryDo(() =>
            {
                state["combat_potion_generation_pool"] = player.Character.PotionPool
                    .GetUnlockedPotions(player.UnlockState)
                    .Concat(ModelDb.PotionPool<SharedPotionPool>().GetUnlockedPotions(player.UnlockState))
                    .Where(potion => potion.CanBeGeneratedInCombat)
                    .Select(BuildPotionInfo)
                    .ToList();
            });
            /* an unavailable pool must not break combat state */
            // Card-generation and transformation domains are static for a run and
            // are served by GET /api/v1/cardpools instead; rebuilding them here
            // cost a full pool enumeration per distinct deck card, every poll.

            // FORK ADDITION — true pile order (index 0 = top of pile, drawn next), with
            // raw entry ids rather than localized titles. `draw_pile` above is sorted by
            // rarity/id to match the in-game display, so it cannot be used to compare
            // card *sequences* against the emulator. Used by the draw-pile differential
            // test; keep both, since existing consumers rely on the sorted view.
            state["draw_pile_ordered"] = BuildOrderedPileList(combatState.DrawPile.Cards);
            state["discard_pile_ordered"] = BuildOrderedPileList(combatState.DiscardPile.Cards);
            state["hand_ordered"] = BuildOrderedPileList(combatState.Hand.Cards);
            // The exhaust pile was missing from this list while `exhaust_pile` and
            // `exhaust_pile_count` both reported it, so a capture recorded an empty
            // ordered exhaust however many cards were really in it. Cards that read the
            // pile's SIZE -- Ashen Strike scales its damage off it -- would have been
            // captured against a board the fixture could not rebuild.
            state["exhaust_pile_ordered"] = BuildOrderedPileList(combatState.ExhaustPile.Cards);

            // Orbs
            AddOrbsState(state, player);

            // Pets (Osty for Necrobinder)
            var pets = BuildPetsState(player);
            if (pets.Count > 0)
            {
                state["pets"] = pets;
            }
        }

        state["gold"] = player.Gold;

        var deck = BuildDeckCardList(player);
        state["deck_count"] = deck.Count;
        if (deck.Count > 0)
            state["deck"] = deck;

        // Powers (status effects)
        state["status"] = BuildPowersState(creature);

        // Relics
        state["relics"] = BuildRelicsList(player);

        // Potions
        AddPotionsState(state, player);

        AddDeckState(state, player, inCombat);

        return state;
    }

    // Cached reflection handles for ActModel._rooms and the RoomSet fields.
    // RoomSet's lists are protected, but they are the only place the act's room
    // ORDER lives, and re-deriving it would mean replaying GenerateRooms.
    private static FieldInfo? _actRoomsField;
    private static readonly Dictionary<string, FieldInfo?> _roomSetFields = new(StringComparer.Ordinal);

    /// <summary>The act's pre-rolled encounter and event queues plus their
    /// consumed counters, or null when the run has not finished act setup.</summary>
    /// <remarks>
    /// PullNextEncounter is just <c>list[visited % count]</c>, so these two
    /// numbers name the exact fight behind every Monster and Elite node on the
    /// map. Nothing here mutates: EnsureNextEventIsValid would bump
    /// eventsVisited, so the raw list is exported and the skip is replayed
    /// client-side instead.
    /// </remarks>
    private static Dictionary<string, object?>? BuildActState(RunState runState)
        // A run that has not finished act setup has no rooms yet - a normal state,
        // not a break, so the read is quiet.
        => TryReadQuiet(() => BuildActStateCore(runState));

    private static Dictionary<string, object?>? BuildActStateCore(RunState runState)
    {
        var act = runState.Act;
        _actRoomsField ??= typeof(ActModel).GetField(
            "_rooms", BindingFlags.Instance | BindingFlags.NonPublic);
        var rooms = _actRoomsField?.GetValue(act);
        if (rooms == null)
            return null;

        var normal = ReadRoomSetIds(rooms, "normalEncounters");
        var elite = ReadRoomSetIds(rooms, "eliteEncounters");
        var events = ReadRoomSetIds(rooms, "events");
        if (normal == null || elite == null || events == null)
            return null;

        var encounters = BuildActEncounters(act, rooms, normal, elite, events);

        bool isMultiplayer = runState.Players.Count > 1;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = runState.CurrentActIndex,
            ["id"] = act.Id.Entry,
            ["rooms_total"] = act.GetNumberOfRooms(isMultiplayer),
            ["floors_total"] = act.GetNumberOfFloors(isMultiplayer),
            ["encounters"] = encounters
        };
    }

    /// <summary>
    /// Locates one of the private room-set fields on the act's room holder, remembering the
    /// lookup because this runs once per act state read.
    /// </summary>
    private static FieldInfo? RoomSetField(object rooms, string field)
    {
        if (!_roomSetFields.TryGetValue(field, out var info))
        {
            info = rooms.GetType().GetField(
                field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _roomSetFields[field] = info;
        }
        return info;
    }

    private static List<string>? ReadRoomSetIds(object rooms, string field)
    {
        if (RoomSetField(rooms, field)?.GetValue(rooms) is not System.Collections.IEnumerable list)
            return null;
        var ids = new List<string>();
        foreach (var item in list)
        {
            var id = item?.GetType().GetProperty("Id")?.GetValue(item);
            var entry = id?.GetType().GetProperty("Entry")?.GetValue(id) as string;
            if (entry != null)
                ids.Add(entry);
        }
        return ids;
    }

    private static int ReadRoomSetVisited(object rooms, string field)
        => RoomSetField(rooms, field)?.GetValue(rooms) is int value ? value : 0;

    private static Dictionary<string, object?> BuildActEncounters(
        ActModel act,
        object rooms,
        List<string> normal,
        List<string> elite,
        List<string> events)
    {
        var encounters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["normal"] = normal,
            ["normal_visited"] = ReadRoomSetVisited(rooms, "normalEncountersVisited"),
            ["elite"] = elite,
            ["elite_visited"] = ReadRoomSetVisited(rooms, "eliteEncountersVisited"),
            ["events"] = events,
            ["events_visited"] = ReadRoomSetVisited(rooms, "eventsVisited"),
            ["boss_visited"] = ReadRoomSetVisited(rooms, "bossEncountersVisited")
        };
        var bossEncounter = TryReadQuiet(() => act.BossEncounter);
        var secondBossEncounter = act.SecondBossEncounter;
        var bossId = bossEncounter?.Id?.Entry;
        var secondBossId = secondBossEncounter?.Id?.Entry;
        if (!string.IsNullOrWhiteSpace(bossId))
            encounters["boss"] = bossId;
        if (!string.IsNullOrWhiteSpace(secondBossId))
            encounters["second_boss"] = secondBossId;
        return encounters;
    }

    /// <summary>Per map point visited this act: what it was, and which rooms and
    /// models it actually resolved into.</summary>
    /// <remarks>
    /// This is the only record of what a "?" turned into and which encounter was
    /// fought there, so it is both the ground truth for validating a client's
    /// "?" predictions and the input to RunManager.BuildRoomTypeBlacklist.
    /// </remarks>
    private static List<Dictionary<string, object?>> BuildMapPointHistory(RunState runState)
    {
        var history = new List<Dictionary<string, object?>>();
        var acts = runState.MapPointHistory;
        if (acts.Count == 0)
            return history;
        foreach (var entry in acts[acts.Count - 1])
        {
            var rooms = new List<Dictionary<string, object?>>();
            foreach (var room in entry.Rooms)
            {
                rooms.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["room_type"] = room.RoomType.ToString(),
                    ["model_id"] = room.ModelId?.Entry,
                    ["monster_ids"] = room.MonsterIds.Select(m => m.Entry).ToList()
                });
            }
            history.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["map_point_type"] = entry.MapPointType.ToString(),
                ["rooms"] = rooms
            });
        }
        return history;
    }

    // Serializes a player's relics. Shared by the local player (BuildPlayerState)
    // and the teammate reveal (BuildAllPlayersState).
    private static List<Dictionary<string, object?>> BuildRelicsList(Player player)
    {
        var relics = new List<Dictionary<string, object?>>();
        foreach (var relic in player.Relics)
        {
            // Golden Compass hides its counter in the UI but the act it was
            // picked up in decides whether every "?" this act is an Event,
            // so a client predicting them needs it.
            int? counter;
            if (relic is GoldenCompass compass)
                counter = compass.GoldenPathAct;
            else if (relic.ShowCounter)
                counter = relic.DisplayAmount;
            else
                counter = null;
            relics.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = relic.Id.Entry,
                ["name"] = SafeGetText(() => relic.Title),
                ["description"] = SafeGetText(() => relic.DynamicDescription),
                ["counter"] = counter,
                ["used_up"] = relic.IsUsedUp,
                ["melted"] = relic.IsMelted,
                ["status"] = relic.Status.ToString(),
                ["is_wax"] = relic.IsWax,
                ["vars"] = relic.DynamicVars.ToDictionary(
                    pair => pair.Key,
                    pair => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["base"] = pair.Value.BaseValue,
                        ["enchanted"] = pair.Value.EnchantedValue,
                        ["preview"] = pair.Value.PreviewValue,
                        ["rendered"] = pair.Value.ToString(CultureInfo.InvariantCulture)
                    }, StringComparer.Ordinal),
                ["runtime"] = BuildRelicRuntimeState(relic),
                ["rarity"] = relic.Rarity.ToString(),
                ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
            });
        }
        return relics;
    }

    private sealed class CardObjectIdentity
    {
        public readonly long Value = System.Threading.Interlocked.Increment(ref _nextCardObjectIdentity);
    }

    private static long _nextCardObjectIdentity;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardModel, CardObjectIdentity> CardObjectIdentities = new();

    private static long GetCardObjectIdentity(CardModel card) => CardObjectIdentities.GetValue(card, _ => new CardObjectIdentity()).Value;

    private sealed class CardPlayIdentity
    {
        public readonly long Value = System.Threading.Interlocked.Increment(ref _nextCardPlayIdentity);
    }

    private static long _nextCardPlayIdentity;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardPlay, CardPlayIdentity> CardPlayIdentities = new();

    private static long GetCardPlayIdentity(CardPlay play) => CardPlayIdentities.GetValue(play, _ => new CardPlayIdentity()).Value;

    private static List<long?> BuildOwnerBlockGains(Player player)
    {
        var state = player.Creature.CombatState;
        if (state == null)
            return new List<long?>();
        return CombatManager.Instance.History.Entries.OfType<BlockGainedEntry>()
            .Where(entry => entry.Actor == player.Creature && entry.HappenedThisTurn(state) && entry.Props.IsCardOrMonsterMove())
            .Select(entry => entry.CardPlay == null ? (long?)null : GetCardPlayIdentity(entry.CardPlay)).ToList();
    }

    private static Dictionary<string, object?> BuildRelicRuntimeState(RelicModel relic)
    {
        var runtime = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (relic is MegaCrit.Sts2.Core.Models.Relics.PaelsTooth tooth)
        {
            // Render fresh copies as they will return. UpgradeInternal changes
            // only this copy and runs no global hooks, deck additions, or RNG.
            var returns = new List<Dictionary<string, object?>>();
            foreach (var saved in tooth.SerializableCards)
            {
                var card = CardModel.FromSerializable(saved);
                card.Owner = tooth.Owner;
                if (card.IsUpgradable)
                {
                    card.UpgradeInternal();
                    card.FinalizeUpgradeInternal();
                }
                var returned = BuildCardInfo(card, PileType.Deck);
                returned["stored_upgrade_level"] = saved.CurrentUpgradeLevel;
                returns.Add(returned);
            }
            runtime["return_cards"] = returns;
        }
        if (relic is MegaCrit.Sts2.Core.Models.Relics.FurCoat furCoat)
        {
            var currentMapPoint = furCoat.Owner.RunState.CurrentMapPoint;
            runtime["active_combat"] = currentMapPoint != null
                && furCoat.GetMarkedCoords()?.Contains(currentMapPoint.coord) == true;
        }
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // Concrete and intermediate relic classes carry saved counters and
        // combat-only latches that are not represented by DisplayAmount. The
        // planner re-plans mid-combat, so it needs those fields to avoid
        // replaying one-shot hooks or restarting persistent cycles.
        for (Type? type = relic.GetType(); type != null && type != typeof(RelicModel); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || field.IsLiteral)
                    continue;
                // The normalized return_cards above replaces the opaque save
                // objects, retaining list order and exact rendered card values.
                if (relic is MegaCrit.Sts2.Core.Models.Relics.PaelsTooth && string.Equals(field.Name, "_serializableCards", StringComparison.Ordinal))
                    continue;
                runtime[field.Name] = SerializePowerRuntimeValue(field.GetValue(relic), seen, 0);
            }
        }
        return runtime;
    }

    // Writes "potions"/"max_potion_slots" into `state`. Shared by the local
    // player (BuildPlayerState) and the teammate reveal (BuildAllPlayersState).
    private static void AddPotionsState(Dictionary<string, object?> state, Player player)
    {
        var potions = new List<Dictionary<string, object?>>();
        int slotIndex = 0;
        foreach (var potion in player.PotionSlots)
        {
            if (potion != null)
            {
                var info = BuildPotionInfo(potion);
                var validTargets = BuildPotionTargetRefs(potion, player);
                var blockedReason = GetPotionUseBlockedReason(potion, validTargets);
                info["slot"] = slotIndex;
                info["can_use"] = blockedReason == null;
                info["use_blocked_reason"] = blockedReason;
                info["can_discard"] = !potion.IsQueued;
                info["discard_blocked_reason"] = potion.IsQueued ? "AlreadyQueued" : null;
                info["requires_target"] = potion.TargetType == TargetType.AnyEnemy;
                info["valid_targets"] = validTargets;
                potions.Add(info);
            }
            slotIndex++;
        }
        state["potions"] = potions;
        state["max_potion_slots"] = player.MaxPotionCount;
    }

    private static Dictionary<string, object?> BuildPotionInfo(PotionModel potion)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = potion.Id.Entry,
            ["name"] = SafeGetText(() => potion.Title),
            ["description"] = SafeGetText(() => potion.DynamicDescription),
            ["rarity"] = potion.Rarity.ToString(),
            ["pool"] = SafeGet(() => potion.Pool.Id.Entry),
            ["epoch"] = GetPotionEpoch(potion),
            ["usage"] = potion.Usage.ToString(),
            ["can_be_generated_in_combat"] = potion.CanBeGeneratedInCombat,
            ["can_use_in_combat"] = potion.Usage == PotionUsage.CombatOnly || potion.Usage == PotionUsage.AnyTime,
            ["target_type"] = potion.TargetType.ToString(),
            ["keywords"] = BuildHoverTips(potion.ExtraHoverTips)
        };
    }

    private static string? GetPotionEpoch(PotionModel potion)
    {
        var id = potion.Id;
        if (Potion1Epoch.Potions.Any(candidate => candidate.Id == id)) return "POTION1_EPOCH";
        if (Potion2Epoch.Potions.Any(candidate => candidate.Id == id)) return "POTION2_EPOCH";
        if (Ironclad4Epoch.Potions.Any(candidate => candidate.Id == id)) return "IRONCLAD4_EPOCH";
        if (Silent4Epoch.Potions.Any(candidate => candidate.Id == id)) return "SILENT4_EPOCH";
        if (Regent4Epoch.Potions.Any(candidate => candidate.Id == id)) return "REGENT4_EPOCH";
        if (Necrobinder4Epoch.Potions.Any(candidate => candidate.Id == id)) return "NECROBINDER4_EPOCH";
        if (Defect4Epoch.Potions.Any(candidate => candidate.Id == id)) return "DEFECT4_EPOCH";
        return null;
    }

    /// <summary>
    /// The master deck, which every out-of-combat decision is made against: a card reward,
    /// a smith, a shop removal and a transform event all asked the caller to choose without
    /// ever showing what it already owns.
    ///
    /// Combat clones the deck into the draw pile (Player.PopulateCombatState), so Player.Deck
    /// stays intact and its count is meaningful on every screen. The card list is only sent
    /// outside combat: in a fight the hand plus the three piles already enumerate every card,
    /// and a second full copy would roughly double the payload of the largest state there is.
    /// </summary>
    private static void AddDeckState(Dictionary<string, object?> state, Player player, bool inCombat)
    {
        // Player.Deck is created with the Player, but a run in character select has not been
        // through PopulateStartingDeck yet, so an empty deck is a normal state, not an error.
        var deckCards = player.Deck?.Cards;
        if (deckCards == null)
            return;

        state["deck_count"] = deckCards.Count;
        if (inCombat)
            return;

        var deck = new List<Dictionary<string, object?>>();
        int deckIndex = 0;
        foreach (var card in deckCards)
        {
            var cardInfo = BuildCardInfo(card, PileType.Deck);
            cardInfo["index"] = deckIndex;
            deck.Add(cardInfo);
            deckIndex++;
        }
        state["deck"] = deck;
    }

    private static string? GetPotionUseBlockedReason(
        PotionModel potion,
        List<Dictionary<string, object?>> validTargets)
    {
        if (potion.IsQueued)
            return "AlreadyQueued";
        if (potion.Owner.Creature.IsDead)
            return "PlayerDead";
        if (!potion.PassesCustomUsabilityCheck)
            return "CustomUsabilityCheckFailed";

        var inCombat = CombatManager.Instance.IsInProgress;
        if (potion.Usage == PotionUsage.CombatOnly)
        {
            if (!inCombat)
                return "CombatOnly";
            if (!IsCombatPlayPhase())
                return "NotInPlayPhase";
        }
        else if (potion.Usage == PotionUsage.Automatic)
        {
            return "Automatic";
        }

        if (inCombat && CombatManager.Instance.PlayerActionsDisabled)
            return "PlayerActionsDisabled";

        if (potion.TargetType == TargetType.AnyEnemy && !inCombat)
            return "EnemyTargetRequiresCombat";

        if (potion.TargetType == TargetType.AnyEnemy && validTargets.Count == 0)
            return "NoValidTargets";

        return null;
    }

    private static List<Dictionary<string, object?>> BuildPotionTargetRefs(PotionModel potion, Player player)
    {
        var targets = new List<Dictionary<string, object?>>();
        if (potion.TargetType != TargetType.AnyEnemy)
            return targets;

        if (!CombatManager.Instance.IsInProgress)
            return targets;

        var combatState = player.Creature.CombatState;
        return BuildEnemyTargetRefs(combatState);
    }

    private static List<Dictionary<string, object?>> BuildEnemyTargetRefs(ICombatState? combatState)
    {
        var targets = new List<Dictionary<string, object?>>();
        if (combatState == null)
            return targets;

        var entityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var creature in combatState.Enemies)
        {
            if (!creature.IsAlive)
                continue;

            var baseId = creature.Monster?.Id.Entry ?? "unknown";
            if (!entityCounts.TryGetValue(baseId, out var count))
                count = 0;
            entityCounts[baseId] = count + 1;

            targets.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["entity_id"] = $"{baseId}_{count}",
                ["combat_id"] = creature.CombatId,
                ["name"] = SafeGetText(() => creature.Monster?.Title)
            });
        }

        return targets;
    }

    private static string GetCostDisplay(CardModel card)
        => card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString(CultureInfo.InvariantCulture);

    // Cost display alone loses ordering, intrinsic upgrades, and independent
    // expiration flags. Reflection is required because this source list is private.
    private static readonly FieldInfo EnergyLocalModifiersField =
        typeof(CardEnergyCost).GetField("_localModifiers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(CardEnergyCost).FullName, "_localModifiers");

    private static Dictionary<string, object?> BuildEnergyCostState(CardModel card)
    {
        var energy = card.EnergyCost;
        if (EnergyLocalModifiersField.GetValue(energy) is not List<LocalCostModifier> modifiers)
            throw new InvalidOperationException("CardEnergyCost._localModifiers has an unsupported shape.");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonical"] = energy.Canonical,
            ["base"] = energy.GetWithModifiers(CostModifiers.None),
            ["costs_x"] = energy.CostsX,
            ["captured_x"] = energy.CostsX ? energy.CapturedXValue : (int?)null,
            ["modifiers"] = modifiers.Select(modifier => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["amount"] = modifier.Amount,
                ["kind"] = modifier.Type.ToString(),
                ["expiration"] = (int)modifier.Expiration,
                ["reduce_only"] = modifier.IsReduceOnly
            }).ToArray()
        };
    }

    private static readonly FieldInfo StarCostModifiersField =
        typeof(CardModel).GetField("_temporaryStarCosts", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(CardModel).FullName, "_temporaryStarCosts");

    private static Dictionary<string, object?> BuildStarCostState(CardModel card)
    {
        if (StarCostModifiersField.GetValue(card) is not List<TemporaryCardCost> modifiers)
            throw new InvalidOperationException("CardModel._temporaryStarCosts has an unsupported shape.");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["canonical"] = card.CanonicalStarCost,
            ["base"] = card.BaseStarCost,
            ["costs_x"] = card.HasStarCostX,
            ["last_spent"] = card.LastStarsSpent,
            ["modifiers"] = modifiers.Select(modifier => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cost"] = modifier.Cost,
                ["clears_when_turn_ends"] = modifier.ClearsWhenTurnEnds,
                ["clears_when_played"] = modifier.ClearsWhenCardIsPlayed
            }).ToArray()
        };
    }

    private static string? GetStarCostDisplay(CardModel card)
    {
        if (card.HasStarCostX) return "X";
        if (card.CurrentStarCost >= 0) return card.GetStarCostWithModifiers().ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static readonly FieldInfo GlassOrbPassiveValueField = typeof(GlassOrb)
        .GetField("_passiveVal", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(GlassOrb).FullName, "_passiveVal");

    // Writes "orbs"/"orb_slots"/"orb_empty_slots" into `state` for a player's
    // orb queue (Defect). Shared by the local player (BuildPlayerState) and the
    // teammate reveal (BuildAllPlayersState) so orb serialization stays in sync.
    private static void AddOrbsState(Dictionary<string, object?> state, Player player)
    {
        state["base_orb_slots"] = player.Character.BaseOrbSlotCount;
        var combatState = player.PlayerCombatState;
        var orbQueue = combatState?.OrbQueue;
        if (orbQueue == null)
            return;

        var orbs = new List<Dictionary<string, object?>>();
        foreach (var orb in orbQueue.Orbs)
        {
            // Populate SmartDescription placeholders with Focus-modified values,
            // mirroring OrbModel.HoverTips getter (OrbModel.cs:92-94)
            string? description = SafeGetText(() =>
            {
                var desc = orb.SmartDescription;
                desc.Add("energyPrefix", orb.Owner.Character.CardPool.Title);
                desc.Add("Passive", orb.PassiveVal);
                desc.Add("Evoke", orb.EvokeVal);
                return desc;
            });
            var info = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = orb.Id.Entry,
                ["name"] = SafeGetText(() => orb.Title),
                ["description"] = description,
                ["passive_val"] = orb.PassiveVal,
                ["evoke_val"] = orb.EvokeVal,
                ["keywords"] = BuildHoverTips(orb.HoverTips)
            };
            if (orb is GlassOrb)
            {
                // Focus clamps PassiveVal at zero, so its preview cannot
                // recover the orb's stored value when Focus later changes.
                info["base_passive_val"] = GlassOrbPassiveValueField.GetValue(orb) is decimal value
                    ? value
                    : throw new InvalidOperationException("GlassOrb stored passive value is unavailable");
            }
            orbs.Add(info);
        }
        state["orbs"] = orbs;
        state["orb_slots"] = orbQueue.Capacity;
        state["orb_empty_slots"] = orbQueue.Capacity - orbQueue.Orbs.Count;
    }

    /// <summary>
    /// Builds the common card display fields shared across all card serialization contexts.
    /// Callers merge context-specific fields (e.g. index, can_play, target_type) on top.
    /// </summary>
    private static Dictionary<string, object?> BuildCardInfo(CardModel card, PileType pile = PileType.None)
    {
        var info = BuildCardBaseInfo(card, pile);
        AppendCardDeckIdentity(info, card);
        AppendCardRuntimeState(info, card);
        return info;
    }

    /// <summary>The display fields every card payload carries, whatever context asked for it.</summary>
    private static Dictionary<string, object?> BuildCardBaseInfo(CardModel card, PileType pile)
    {
        var upgradedPreview = SafeBuildUpgradedCardPreview(card);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = card.Id.Entry,
            ["name"] = SafeGetText(() => card.Title),
            ["type"] = card.Type.ToString(),
            ["cost"] = GetCostDisplay(card),
            ["energy_cost_state"] = BuildEnergyCostState(card),
            ["star_cost"] = GetStarCostDisplay(card),
            ["star_cost_state"] = BuildStarCostState(card),
            ["description"] = SafeGetCardDescription(card, pile),
            ["rarity"] = card.Rarity.ToString(),
            ["enchantment"] = card.Enchantment?.GetType().Name,
            ["enchantment_state"] = BuildEnchantmentState(card),
            ["affliction"] = card.Affliction?.Id.Entry,
            ["is_upgraded"] = card.IsUpgraded,
            ["is_upgradable"] = card.IsUpgradable,
            ["upgrade_level"] = card.CurrentUpgradeLevel,
            ["current_upgrade_level"] = card.CurrentUpgradeLevel,
            ["max_upgrade_level"] = card.MaxUpgradeLevel,
            ["upgrade_preview_type"] = card.UpgradePreviewType.ToString(),
            ["upgrade_preview_cost"] = upgradedPreview != null ? GetCostDisplay(upgradedPreview) : null,
            ["upgrade_preview_star_cost"] = upgradedPreview != null ? GetStarCostDisplay(upgradedPreview) : null,
            ["upgrade_preview_description"] = SafeGetCardUpgradePreviewDescription(card, pile),
            ["base_replay_count"] = card.BaseReplayCount,
            ["exhaust_on_next_play"] = card.ExhaustOnNextPlay,
            ["tags"] = card.Tags.Select(tag => tag.ToString()).ToArray(),
            ["keywords"] = BuildHoverTips(card.HoverTips),
            // Machine-readable CardKeyword names beside the localized hover tips
            // above. `keywords` is display text and cannot be matched against
            // reliably; this is what a client tests for Sly/Ethereal/Retain.
            ["keyword_ids"] = BuildKeywordIds(card),
            ["has_retain_keyword"] = card.Keywords.Contains(CardKeyword.Retain),
            ["has_single_turn_retain"] = GetSingleTurnRetain(card),
            ["should_retain_this_turn"] = card.ShouldRetainThisTurn,
            ["has_sly_keyword"] = card.Keywords.Contains(CardKeyword.Sly),
            ["has_single_turn_sly"] = GetSingleTurnSly(card),
            ["is_sly_this_turn"] = card.IsSlyThisTurn
        };
    }

    private static void AppendCardDeckIdentity(Dictionary<string, object?> info, CardModel card)
    {
        // NetDeckCard addresses the current deck order. A combat card can
        // retain a DeckVersion pointer after that parent left the deck.
        var deckCard = card.Pile?.Type == PileType.Deck ? card : card.DeckVersion;
        info["has_deck_version"] = card.DeckVersion != null;
        info["deck_card_index"] = deckCard?.Pile?.Type == PileType.Deck
            ? NetDeckCard.FromModel(deckCard).DeckIndex
            : null;
    }

    /// <summary>Fields that only some cards, or only mutable copies, carry.</summary>
    private static void AppendCardRuntimeState(Dictionary<string, object?> info, CardModel card)
    {
        if (card.IsMutable)
            info["card_object_id"] = GetCardObjectIdentity(card);
        // Only present, and only false, when this build stripped the game's power
        // and relic baking out of the numbers above. A powered payload omits the key
        // entirely, which is the shape every existing consumer already assumes.
        if (UnpoweredValues)
            info["text_baked"] = false;
        var damageVar = BuildDamageVarState(card);
        if (damageVar != null)
            info["damage_var"] = damageVar;
        // Separate the card's own HP-loss variable from Corrupted's appended text.
        if (card.Enchantment is MegaCrit.Sts2.Core.Models.Enchantments.Corrupted)
            info["hp_loss_var"] = card.DynamicVars.ContainsKey("HpLoss")
                ? card.DynamicVars.HpLoss.BaseValue.ToString(CultureInfo.InvariantCulture)
                : null;
        if (card is MadScience madScience)
        {
            info["mad_science_type"] = madScience.TinkerTimeType.ToString();
            info["mad_science_rider"] = madScience.TinkerTimeRider.ToString();
        }
        if (card is GeneticAlgorithm geneticAlgorithm)
        {
            info["current_block"] = geneticAlgorithm.CurrentBlock;
            info["increased_block"] = geneticAlgorithm.IncreasedBlock;
        }
        if (card is Guilty guilty)
            info["combats_seen"] = guilty.CombatsSeen;
        // The multiplayer synchronizer already assigns every mutable combat
        // card a stable 16-bit identity. Export the same identity so an MCTS
        // selection can distinguish physical copies with equal card IDs.
        TryDo(() =>
        {
            if (card.Pile?.IsCombatPile == true)
                info["combat_card_id"] = NetCombatCard.FromModel(card).CombatCardIndex;
        });
        /* immutable/deck cards intentionally have no combat identity */
    }

    private static Dictionary<string, object?>? BuildEnchantmentState(CardModel card)
    {
        if (card.Enchantment == null) return null;
        // Match description rendering, including the enchantment's own DynamicVars.
        using UnpoweredCardVars? scope = UnpoweredValues ? UnpoweredCardVars.Apply(card) : null;
        var text = card.Enchantment.DynamicExtraCardText?.GetFormattedText();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = card.Enchantment.Amount,
            ["status"] = card.Enchantment.Status.ToString(),
            ["extra_text"] = text == null ? null : CleanCardDescription(text)
        };
    }

    // Canonical values are simulation inputs; description formatting can truncate them.
    private static Dictionary<string, object?>? BuildDamageVarState(CardModel card)
    {
        decimal damage;
        decimal? extra = null;
        ValueProp props;
        if (card.DynamicVars.ContainsKey("CalculatedDamage"))
        {
            damage = card.DynamicVars.CalculationBase.BaseValue;
            extra = card.DynamicVars.ExtraDamage.BaseValue;
            props = card.DynamicVars.CalculatedDamage.Props;
        }
        else if (card.DynamicVars.ContainsKey("Damage"))
        {
            damage = card.DynamicVars.Damage.BaseValue;
            props = card.DynamicVars.Damage.Props;
        }
        else if (card.DynamicVars.ContainsKey("OstyDamage"))
        {
            damage = card.DynamicVars.OstyDamage.BaseValue;
            props = card.DynamicVars.OstyDamage.Props;
        }
        else return null;
        var additive = card.Enchantment?.EnchantDamageAdditive(damage, props) ?? 0m;
        var multiplier = card.Enchantment?.EnchantDamageMultiplicative(damage + additive, props) ?? 1m;
        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["base"] = damage.ToString(CultureInfo.InvariantCulture),
            ["enchantment_additive"] = additive.ToString(CultureInfo.InvariantCulture),
            ["enchantment_multiplier"] = multiplier.ToString(CultureInfo.InvariantCulture)
        };
        if (extra.HasValue)
            state["extra"] = extra.Value.ToString(CultureInfo.InvariantCulture);
        if (card.Enchantment is MegaCrit.Sts2.Core.Models.Enchantments.Momentum momentum)
            state["after_play"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "Momentum",
                ["amount"] = momentum.Amount.ToString(CultureInfo.InvariantCulture)
            };
        else if (card.Enchantment is MegaCrit.Sts2.Core.Models.Enchantments.Vigorous vigorous
                 && vigorous.Status == MegaCrit.Sts2.Core.Entities.Enchantments.EnchantmentStatus.Normal)
            state["after_play"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "Vigorous" };
        else if (card.Enchantment is MegaCrit.Sts2.Core.Models.Enchantments.Corrupted)
            state["after_play"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "Corrupted" };
        return state;
    }

    private static readonly FieldInfo SingleTurnRetainField =
        typeof(CardModel).GetField("_hasSingleTurnRetain", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(CardModel).FullName, "_hasSingleTurnRetain");

    private static readonly FieldInfo SingleTurnSlyField =
        typeof(CardModel).GetField("_hasSingleTurnSly", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(CardModel).FullName, "_hasSingleTurnSly");

    private static bool GetSingleTurnSly(CardModel card)
    {
        if (SingleTurnSlyField.GetValue(card) is not bool temporarySly)
            throw new InvalidOperationException("CardModel._hasSingleTurnSly has an unsupported shape.");
        return temporarySly;
    }

    private static bool GetSingleTurnRetain(CardModel card)
    {
        // A positive predicate cannot reveal which part survives cleanup.
        // Missing or unsupported private state must refuse publication.
        if (SingleTurnRetainField.GetValue(card) is not bool temporaryRetain)
            throw new InvalidOperationException("CardModel._hasSingleTurnRetain has an unsupported shape.");
        return temporaryRetain;
    }

    /// <summary>
    /// The card as it would read after one upgrade, or null when it cannot be upgraded
    /// or the preview clone failed. Built from the real upgraded clone, so the numbers
    /// are the game's own rather than something derived from the card text.
    /// </summary>
    private static Dictionary<string, object?>? BuildUpgradePreviewInfo(CardModel card)
    {
        var preview = SafeBuildUpgradedCardPreview(card);
        if (preview == null) return null;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = SafeGetText(() => preview.Title),
            ["cost"] = GetCostDisplay(preview),
            ["star_cost"] = GetStarCostDisplay(preview),
            ["description"] = SafeGetCardDescription(preview),
            ["keywords"] = BuildHoverTips(preview.HoverTips)
        };
    }

    /// <summary>
    /// Adds "upgrade_preview" to a card entry on the screens where an upgrade or a pick
    /// is being decided. Cards already at their last upgrade level are skipped:
    /// IsUpgraded alone is not the test, because a multi-level card at level 1 of 2 is
    /// upgraded and still has an upgrade left.
    /// </summary>
    private static void AttachUpgradePreview(Dictionary<string, object?> cardInfo, CardModel card)
    {
        if (!card.IsUpgradable || card.CurrentUpgradeLevel >= card.MaxUpgradeLevel) return;

        var preview = BuildUpgradePreviewInfo(card);
        if (preview != null) cardInfo["upgrade_preview"] = preview;
    }

    /// <summary>
    /// Card and potion ids that upgrade cards already in hand, mid-combat: Armaments
    /// (one or all of the hand), Apotheosis (every card) and Blessing of the Forge (the
    /// whole hand). Cards that create upgraded copies (Begone, Primal Force...) are not
    /// here: the copies arrive already upgraded and read correctly without a preview.
    /// </summary>
    private static readonly HashSet<string> HandUpgradeCardIds = new(StringComparer.Ordinal) { "ARMAMENTS", "APOTHEOSIS" };
    private static readonly HashSet<string> HandUpgradePotionIds = new(StringComparer.Ordinal) { "BLESSING_OF_THE_FORGE" };

    private static bool CanUpgradeHandThisTurn(Player player, IEnumerable<CardModel> hand)
    {
        return TryRead<bool?>("hand.upgrade_preview", () =>
        {
            if (player.PotionSlots.Any(potion => potion != null && HandUpgradePotionIds.Contains(potion.Id.Entry)))
                return true;
            return hand.Any(card => HandUpgradeCardIds.Contains(card.Id.Entry));
        }) ?? false;
    }

    private static Dictionary<string, object?> BuildCardState(CardModel card, int index, Player? player = null)
    {
        card.CanPlay(out var unplayableReason, out _);

        var state = BuildCardInfo(card);
        state["index"] = index;
        state["uid"] = GetStableCardUid(card);
        state["description"] = SafeGetCardDescription(card); // hand cards use default pile
        state["target_type"] = card.TargetType.ToString();
        state["requires_target"] = card.TargetType == TargetType.AnyEnemy;
        state["valid_targets"] = card.TargetType == TargetType.AnyEnemy
            ? BuildEnemyTargetRefs(player?.Creature?.CombatState ?? card.Owner?.Creature?.CombatState)
            : new List<Dictionary<string, object?>>();
        state["can_play"] = unplayableReason == UnplayableReason.None;
        state["unplayable_reason"] = unplayableReason != UnplayableReason.None ? unplayableReason.ToString() : null;

        var vsTargets = BuildCardTargetPreviews(card);
        if (vsTargets.Count > 0)
            state["vs_targets"] = vsTargets;
        return state;
    }

    /// <summary>
    /// What this card actually does to each living enemy right now.
    ///
    /// The number printed on a card has an inconsistent set of modifiers baked in:
    /// Strength and Pen Nib show up, Vulnerable and Weak sometimes do and sometimes
    /// don't, because the game only folds target-side modifiers in while the card is
    /// hovered over that target. So a caller reading the card text had to guess which
    /// multipliers were already applied — 60 expected, 43 dealt.
    ///
    /// This runs the same preview the UI runs on hover (CardModel.UpdateDynamicVarPreview,
    /// whose CalculatedDamageVar goes through Hook.ModifyDamage with the target) once per
    /// enemy, reports the resolved damage, hit count and target-specific description, then
    /// clears the preview so the live card is left exactly as it was found.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildCardTargetPreviews(CardModel card)
    {
        var previews = new List<Dictionary<string, object?>>();

        if (card.TargetType is not (TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy))
            return previews;

        var combatState = TryRead<ICombatState?>("card.vs_targets.combat_state",
            () => card.Owner?.Creature?.CombatState);
        if (combatState == null)
            return previews;

        try
        {
            foreach (var enemy in combatState.Enemies)
            {
                if (!enemy.IsAlive) continue;

                TryDo("card.vs_targets", () => previews.Add(BuildCardTargetPreview(card, enemy)));
            }
        }
        finally
        {
            // Always hand the card back the way it was found.
            TryDo("card.vs_targets.clear_preview", () => { card.DynamicVars.ClearPreview(); });
            TryDo("card.vs_targets.restore_preview", () => { card.UpdateDynamicVarPreview(CardPreviewMode.Normal, null, card.DynamicVars); });
        }

        return previews;
    }

    /// <summary>
    /// Every dynamic var on this card, by name, at its previewed value.
    ///
    /// Reads PreviewValue, not IntValue: IntValue is <c>(int)BaseValue</c>, i.e. the
    /// printed number before any modifier. The preview run just before this is what
    /// folds in Strength, the target's Vulnerable and everything else ModifyDamage
    /// applies, and it writes its result to PreviewValue.
    /// </summary>
    private static Dictionary<string, object?> ReadDynamicVarValues(CardModel card)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        TryDo("card.values", () =>
        {
            foreach (var (name, dynamicVar) in card.DynamicVars)
            {
                int? value = SafeGetInt(() => (int)System.Math.Round(dynamicVar.PreviewValue,
                    System.MidpointRounding.ToZero));
                value ??= SafeGetInt(() => dynamicVar.IntValue);
                if (value != null)
                    values[name] = value;
                else
                    Warn($"card.values.{name}", "neither PreviewValue nor IntValue could be read");
            }
        });
        return values;
    }

    /// <summary>
    /// Powers that cap how much HP a creature can lose, per hit. Damage modifiers do not
    /// see these — they are applied when the damage lands — so a 18-damage card against
    /// Slippery takes the enemy down by 1, and reporting 18 sends the caller into a
    /// fight it cannot win. Keyed by power type because that survives an id rename.
    /// </summary>
    private static readonly Dictionary<string, string> _perHitDamageCaps = new(StringComparer.Ordinal)
    {
        ["SlipperyPower"] = "next hit reduced to 1",
        ["IntangiblePower"] = "all damage reduced to 1",
        ["HardToKillPower"] = "all damage reduced to the power's amount"
    };

    private const string HardenedShellPowerName = "HardenedShellPower";

    /// <summary>
    /// Describes any cap on what this target can actually lose from one hit, and the
    /// resulting effective damage. Returns null when nothing caps it.
    /// </summary>
    private static Dictionary<string, object?>? BuildDamageCap(Creature target, int damage, int hits)
    {
        return TryRead<Dictionary<string, object?>?>("card.damage_cap", () =>
        {
            foreach (var power in target.Powers)
            {
                string typeName = power.GetType().Name;

                if (_perHitDamageCaps.TryGetValue(typeName, out var note))
                {
                    int perHit = string.Equals(typeName, "HardToKillPower", StringComparison.Ordinal) ? System.Math.Max(power.Amount, 0) : 1;
                    int capped = System.Math.Min(damage, perHit);
                    int remaining = string.Equals(typeName, "SlipperyPower", StringComparison.Ordinal) ? System.Math.Max(power.Amount, 0) : int.MaxValue;
                    int cappedHits = System.Math.Min(hits, remaining);

                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["capped_by"] = SafeGetText(() => power.Title) ?? typeName,
                        ["power_id"] = SafeGetText(() => power.Id.Entry),
                        ["per_hit_max"] = perHit,
                        ["note"] = note,
                        ["effective_damage"] = capped * cappedHits + System.Math.Max(hits - cappedHits, 0) * damage
                    };
                }

                if (string.Equals(typeName, HardenedShellPowerName, StringComparison.Ordinal))
                {
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["capped_by"] = SafeGetText(() => power.Title) ?? typeName,
                        ["power_id"] = SafeGetText(() => power.Id.Entry),
                        ["per_turn_max"] = power.Amount,
                        ["note"] = "total HP lost this turn is capped, not per hit"
                    };
                }
            }

            return null;
        });
    }

    private static int? PickDynamicVar(Dictionary<string, object?> values, params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && value is int i)
                return i;
        }
        return null;
    }

    private static string? SafeGetTargetedCardDescription(CardModel card, Creature target)
        => TryRead("card.vs_targets.description",
            () => StripRichTextTags(card.GetDescriptionForPile(PileType.Hand, target)).Replace("\n", " "));

    private static void AddPreviewCardsFromContainer(
        Godot.Control? container,
        List<Dictionary<string, object?>> previewCards)
    {
        if (container?.Visible != true)
            return;

        var cardHolders = FindAllSortedByPosition<NCardHolder>(container);
        if (cardHolders.Count > 0)
        {
            foreach (var card in cardHolders.Select(holder => holder.CardModel).OfType<CardModel>())
            {
                var cardInfo = BuildCardInfo(card);
                cardInfo["index"] = previewCards.Count;
                previewCards.Add(cardInfo);
            }
            return;
        }

        foreach (var card in FindAll<NPreviewCardHolder>(container).Select(holder => holder.CardModel).OfType<CardModel>())
        {
            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = previewCards.Count;
            previewCards.Add(cardInfo);
        }
    }

    private static List<Dictionary<string, object?>> BuildPileCardList(IEnumerable<CardModel> cards, PileType pile)
    {
        var list = new List<Dictionary<string, object?>>();
        var index = 0;
        foreach (var card in cards)
        {
            var info = BuildCardInfo(card, pile);
            info["index"] = index++;
            info["target_type"] = card.TargetType.ToString();
            list.Add(info);
        }
        return list;
    }

    // FORK ADDITION — see the draw_pile_ordered call site. Raw entry ids in true pile
    // order, deliberately minimal so the readout is stable and cheap to diff.
    private static List<Dictionary<string, object?>> BuildOrderedPileList(IEnumerable<CardModel> cards)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var card in cards)
        {
            list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = card.Id.Entry,
                ["is_upgraded"] = card.IsUpgraded
            });

        }
        return list;
    }

    // entity_id registry
    //
    // entity_ids used to be generated by counting the *alive* enemies each time state
    // was built (and again, independently, when resolving a target). That made the same
    // string point at a different creature as soon as one died between the read and the
    // action, so "attack EXOSKELETON_1" silently hit the wrong bug or failed outright.
    //
    // Ids are now assigned once per creature, on first sight, and keyed by the stable
    // CombatId. A creature keeps its id for the whole fight, even after its neighbours die.
    private static readonly Dictionary<uint, string> _entityIdByCombatId = new();
    private static readonly Dictionary<string, int> _entityIdNextIndex = new(StringComparer.Ordinal);
    private static ICombatState? _entityIdCombatToken;

    // card uid registry
    //
    // card_index is a position in the hand, so playing one card renumbers every card
    // behind it. A plan built from one state read ("play 4, then 2, then 0") therefore
    // has to be recomputed after every single play, and getting the arithmetic wrong
    // plays the wrong card without any error.
    //
    // Each card instance gets a uid on first sight that stays with it for the whole
    // combat, wherever it moves between hand, draw, discard and exhaust.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardModel, string> _cardUids = new();
    private static readonly Dictionary<string, int> _cardUidNextIndex = new(StringComparer.Ordinal);
    private static object? _cardUidCombatToken;

    /// <summary>
    /// Returns this card instance's uid, assigning one on first sight.
    /// Format is "<card_id>#<n>", counting instances of that card in the order first seen.
    /// </summary>
    internal static string GetStableCardUid(CardModel card)
    {
        if (_cardUids.TryGetValue(card, out var existing))
            return existing;

        string baseId = SafeGetText(() => card.Id.Entry) ?? "unknown";
        if (!_cardUidNextIndex.TryGetValue(baseId, out int next))
            next = 0;
        _cardUidNextIndex[baseId] = next + 1;

        string uid = $"{baseId}#{next}";
        _cardUids.Add(card, uid);
        return uid;
    }

    /// <summary>Clears card uids when a new combat starts.</summary>
    internal static void RefreshCardUidRegistry(ICombatState combatState)
    {
        if (ReferenceEquals(_cardUidCombatToken, combatState))
            return;

        _cardUidCombatToken = combatState;
        _cardUids.Clear();
        _cardUidNextIndex.Clear();
    }

    /// <summary>
    /// Assigns stable entity_ids for every enemy in the given combat, clearing the
    /// registry when a new combat starts. Call before serializing enemies.
    /// </summary>
    internal static void RefreshEntityIdRegistry(ICombatState combatState)
    {
        if (!ReferenceEquals(_entityIdCombatToken, combatState))
        {
            _entityIdCombatToken = combatState;
            _entityIdByCombatId.Clear();
            _entityIdNextIndex.Clear();
        }

        foreach (var creature in combatState.Enemies)
            _ = GetStableEntityId(creature);
    }

    /// <summary>
    /// Returns this creature's entity_id, assigning one on first sight.
    /// The suffix counts creatures of the same model in the order they were first seen,
    /// so it never shifts when another enemy dies.
    /// </summary>
    internal static string GetStableEntityId(Creature creature)
    {
        string baseId = creature.Monster?.Id.Entry ?? "unknown";

        uint? combatId = creature.CombatId;
        if (combatId == null)
            return $"{baseId}_0";

        if (_entityIdByCombatId.TryGetValue(combatId.Value, out var existing))
            return existing;

        if (!_entityIdNextIndex.TryGetValue(baseId, out int next))
            next = 0;
        _entityIdNextIndex[baseId] = next + 1;

        string entityId = $"{baseId}_{next}";
        _entityIdByCombatId[combatId.Value] = entityId;
        return entityId;
    }

    /// <summary>
    /// Every enemy the fight still contains, alive or waiting to come back.
    ///
    /// A killed enemy normally leaves ICombatState.Enemies, so listing the survivors used
    /// to be the same thing as listing the list. A Decimillipede segment breaks that: its
    /// ReattachPower answers false to ShouldCreatureBeRemovedFromCombatAfterDeath, so the
    /// corpse stays in the fight and heals itself back two enemy turns later. Dropping it
    /// hid the revive entirely - the caller spent those turns hitting the other segments
    /// without knowing one was about to stand back up.
    ///
    /// Alive enemies are reported as before. Dead ones are reported with alive:false only
    /// when the game itself refuses to remove them; a corpse that is merely still being
    /// unwound from the list (CreatureCmd defers removal while its monster is mid-move) is
    /// left out, because it is leaving.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildEnemyList(CombatState combatState)
    {
        var enemies = new List<Dictionary<string, object?>>();
        RefreshEntityIdRegistry(combatState);
        // Enemies can appear twice in the list while the combat state is being rebuilt
        // (splits and spawns), which printed every line of that enemy twice in markdown.
        var seenCombatIds = new HashSet<uint>();
        foreach (var creature in combatState.Enemies)
        {
            if (!creature.IsAlive && !StaysInCombatWhileDead(combatState, creature))
                continue;
            if (creature.CombatId is uint id && !seenCombatIds.Add(id))
                continue;
            enemies.Add(BuildEnemyState(creature));
        }
        return enemies;
    }

    /// <summary>
    /// Whether the game is deliberately keeping this dead creature in the fight.
    ///
    /// CreatureCmd.KillWithoutCheckingWinCondition removes a dead enemy from
    /// ICombatState.Enemies only when Hook.ShouldCreatureBeRemovedFromCombatAfterDeath
    /// agrees, and every power that wants to outlive its owner (ReattachPower,
    /// DieForYouPower, IllusionPower...) votes no there. Asking the same predicate is
    /// therefore the game's own answer, and a sharper test than "dead but still listed":
    /// removal is also deferred for a monster that is mid-move, so the list briefly holds
    /// corpses that are on their way out.
    /// </summary>
    private static bool StaysInCombatWhileDead(ICombatState combatState, Creature creature)
        => TryRead<bool?>("enemy.stays_in_combat_while_dead",
            () => !Hook.ShouldCreatureBeRemovedFromCombatAfterDeath(combatState, creature)) ?? false;

    private static List<Dictionary<string, object?>> BuildDeckCardList(Player player)
    {
        var list = new List<Dictionary<string, object?>>();
        var index = 0;
        foreach (var card in GetPlayerDeckCards(player))
        {
            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index++;
            list.Add(cardInfo);
        }
        return list;
    }

    private static IEnumerable<CardModel> GetPlayerDeckCards(Player player)
    {
        var deck = GetMemberValue(player, "Deck");
        if (deck == null)
            yield break;

        var cards = GetMemberValue(deck, "Cards") ?? deck;
        if (cards is not System.Collections.IEnumerable enumerable)
            yield break;

        foreach (var item in enumerable)
        {
            if (item is CardModel card)
                yield return card;
        }
    }

    private static object? GetMemberValue(object source, string memberName)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        var type = source.GetType();
        var property = TryReadQuiet(() => type.GetProperty(memberName, Flags));
        if (property != null)
            return TryReadQuiet(() => property.GetValue(source));

        var field = TryReadQuiet(() => type.GetField(memberName, Flags));
        return field != null ? TryReadQuiet(() => field.GetValue(source)) : null;
    }

    private static bool? GetBoolMemberValue(object source, string memberName)
    {
        var value = GetMemberValue(source, memberName);
        return value is bool boolValue ? boolValue : null;
    }

    private static void AddCardHolderState(Dictionary<string, object?> cardInfo, Control holder)
    {
        var isSelected = GetBoolMemberValue(holder, "IsSelected")
                         ?? GetBoolMemberValue(holder, "_isSelected");

        cardInfo["is_selected"] = isSelected ?? false;
        cardInfo["is_visible"] = IsNodeVisible(holder);
        cardInfo["can_select"] = IsCardHolderSelectable(holder);
    }

    private static bool IsCardHolderSelectable(Control holder)
    {
        var isEnabled = GetBoolMemberValue(holder, "IsEnabled");
        return (isEnabled ?? true) && IsNodeVisible(holder);
    }

    private static Dictionary<string, object?> BuildEnemyState(Creature creature)
    {
        var monster = creature.Monster;
        string entityId = GetStableEntityId(creature);
        bool alive = creature.IsAlive;

        // Attack history is only meaningful relative to the local player, and an
        // enemy can be exported outside a run (compendium, combat teardown), so the
        // reference is optional and every block below guards on it.
        var localCreature = ResolveLocalCreature();

        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entity_id"] = entityId,
            ["combat_id"] = creature.CombatId,
            // Which encounter slot this creature occupies. Fixed for its whole
            // life, and what `GetNextSlot` tests against when a move summons.
            ["slot_name"] = creature.SlotName,
            ["name"] = SafeGetText(() => monster?.Title),
            // Explicit on every enemy, alive or not: a caller that wants only living
            // targets can filter on one field instead of inferring death from hp == 0.
            ["alive"] = alive,
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["hp_infinite"] = creature.HpDisplay.IsInfinite(),
            // False only for a corpse the fight is holding on to (see the
            // enemy loop in BuildBattleState). Every other exported creature is
            // alive, so a client that ignores this reads exactly what it read
            // before.
            ["is_performing_move"] = monster?.IsPerformingMove ?? false,
            ["block"] = creature.Block,
            ["is_alive"] = creature.IsAlive,
            ["is_visible"] = creature.IsAlive,
            ["can_target"] = creature.IsAlive,
            ["can_select"] = creature.IsAlive,
            ["status"] = BuildPowersState(creature)
        };
        AppendCreatureDamageHistory(state, creature, localCreature);

        if (!alive)
        {
            var revive = BuildReviveState(creature);
            if (revive != null)
                state["revive"] = revive;
        }

        AppendEnemyMoveInfo(state, monster, creature);
        return state;
    }

    private static Creature? ResolveLocalCreature()
    {
        Creature? localCreature = null;
        TryDo("enemy.local_creature", () =>
        {
            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState != null)
                localCreature = LocalContext.GetMe(runState)?.Creature;
        });
        return localCreature;
    }

    /// <summary>
    /// How often this creature has been hit by powered attacks this turn, split by who
    /// dealt them: the local player, the local player's side, and its allies.
    /// </summary>
    private static void AppendCreatureDamageHistory(
        Dictionary<string, object?> state, Creature creature, Creature? localCreature)
    {
        if (localCreature == null)
            return;
        TryDo(() =>
        {
            var history = CombatManager.Instance.History;
            state["local_powered_hits_this_turn"] = history.Entries.OfType<DamageReceivedEntry>().Count(en =>
                en.HappenedThisTurn(creature.CombatState)
                && en.Receiver == creature
                && en.Dealer == localCreature
                && en.Result.Props.IsPoweredAttack());
            state["owner_side_powered_hits_this_turn"] = history.Entries.OfType<DamageReceivedEntry>().Count(en =>
                en.HappenedThisTurn(creature.CombatState)
                && en.Receiver == creature
                && en.Dealer != null
                && (en.Dealer == localCreature || en.Dealer.PetOwner?.Creature == localCreature)
                && en.Result.Props.IsPoweredAttack());
            state["ally_powered_hits_this_turn"] = history.Entries.OfType<DamageReceivedEntry>().Count(en =>
                en.HappenedThisTurn(creature.CombatState)
                && en.Receiver == creature
                && en.Dealer != null
                && en.Dealer != localCreature
                && en.Dealer.Side == localCreature.Side
                && en.Result.Props.IsPoweredAttack());
        });
    }

    /// <summary>
    /// What this creature is about to do: its intents and the effect scan behind them,
    /// the monster's design stats, its whole move graph, and its own RNG stream.
    /// </summary>
    private static void AppendEnemyMoveInfo(
        Dictionary<string, object?> state,
        MonsterModel? monster,
        Creature creature)
    {
        // Intents
        if (monster?.NextMove is MoveState moveState)
        {
            state["intents"] = BuildIntentList(moveState.Intents, creature);
            // A stunned creature performs no logic on its stunned turn
            // (CreatureCmd.Stun); report it here so a client does not treat the
            // enemy as a live threat.
            state["stunned"] = moveState.Intents.Any(intent => intent is StunIntent);
            state["move_id"] = moveState.Id;
            // What the move actually does, read from its lambda's IL (see
            // McpMod.MoveEffects.cs): which powers it applies to whom and how
            // much, block gained, the status card class it shuffles in.
            var moveFx = GetMoveEffects(monster, moveState);
            if (moveFx != null)
                state["move_effects"] = moveFx;
        }

        // Numeric design stats declared on the concrete monster class
        // (SeaKickDamage, BubbleBlock, BubbleStr, IncantationAmount, ...).
        // Property names are the move id in CamelCase + an effect suffix, so
        // consumers can resolve what a Buff/Defend/Heal move actually does.
        if (monster != null)
        {
            var stats = BuildMonsterStats(monster);
            if (stats != null)
                state["model_stats"] = stats;
        }

        // Complete move graph plus its live cursor/history and the shared
        // MonsterAi RNG position. Consumers can advance this for any horizon.
        var intentMachine = BuildIntentMachine(creature);
        if (intentMachine != null)
            state["intent_machine"] = intentMachine;

        // This creature's own monster-local RNG stream, assigned at creation
        // from the run seed + map coordinate. See McpMod.RngStreams.cs.
        var monsterRng = BuildMonsterRng(creature);
        if (monsterRng != null)
            state["rng"] = monsterRng;
    }

    /// <summary>
    /// What a dead-but-still-present enemy is waiting for, when it is waiting to come back.
    ///
    /// The one case in the game today is the Decimillipede: killing a segment runs
    /// ReattachPower.AfterDeath, which pushes the monster straight into its DEAD_MOVE and
    /// leaves the corpse in the fight; DEAD_MOVE's follow-up is REATTACH_MOVE, which calls
    /// ReattachPower.DoReattach and heals the segment back up by the power's Amount (25).
    /// One move is performed per enemy turn, so the segment is back two enemy turns after
    /// it fell, and the API said nothing about it.
    ///
    /// Returns null for a corpse with no heal move ahead of it (DieForYouPower and friends
    /// also keep their owner in the fight, but it stays dead) - "revive" present means
    /// "this is coming back", never merely "this is dead".
    /// </summary>
    private static Dictionary<string, object?>? BuildReviveState(Creature creature)
    {
        var power = FindPowerKeepingCorpseInCombat(creature);

        // ReattachPower.DoReattach bails out when every other segment is dead too - that
        // is the kill, and the whole Decimillipede fades out. Saying "back in 2 turns"
        // while the fight is ending would be exactly backwards, so check the same thing
        // the power checks: another living holder of the same power.
        if (power != null && !HasLivingPeerWithPower(creature, power))
            return null;

        int? turns = TurnsUntilRevive(creature);
        if (turns == null)
            return null;

        var revive = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["in_turns"] = turns
        };

        if (power != null)
        {
            revive["power_id"] = SafeGetText(() => power.Id.Entry);
            revive["power_name"] = SafeGetText(() => power.Title);
            // ReattachPower heals by its own Amount, so its stack count is the HP the
            // segment comes back with. Reported as null rather than guessed for a power
            // whose amount is not a heal.
            int? amount = SafeGetInt(() => power.Amount);
            revive["hp"] = amount > 0 ? amount : null;
        }

        return revive;
    }

    /// <summary>
    /// Whether another living enemy carries the same power - ReattachPower's
    /// "are all my other segments dead?" test, asked without naming the power.
    /// </summary>
    private static bool HasLivingPeerWithPower(Creature creature, PowerModel power)
    {
        return TryRead<bool?>("enemy.revive.peers", () =>
        {
            var combatState = creature.CombatState;
            if (combatState == null)
                return true;

            foreach (var other in combatState.Enemies)
            {
                if (ReferenceEquals(other, creature) || !other.IsAlive)
                    continue;
                if (other.HasPower(power.Id))
                    return true;
            }
            return false;
        }) ?? true;
    }

    /// <summary>
    /// Enemy turns until this dead creature heals itself back up, read off the monster's
    /// own move state machine: the moves are performed one per enemy turn along the
    /// MoveState.FollowUpState chain, so the position of the heal move in that chain is
    /// the answer. 2 while the segment still has to sit through DEAD_MOVE, 1 once
    /// REATTACH_MOVE is the move it will perform next.
    ///
    /// Nothing exposes a countdown directly - ReattachPower's only bookkeeping is a
    /// private "isReviving" flag on its internal data, with no turn counter at all - so
    /// the chain is the reading, not a second source that could be cross-checked.
    /// Null when no heal move is reachable: the walk stops at the first state that is not
    /// a plain MoveState (a RandomBranchState has not picked its next move yet) and after
    /// a few steps, so a heal further away than that is reported as unknown rather than
    /// guessed.
    /// </summary>
    private static int? TurnsUntilRevive(Creature creature)
    {
        const int maxLookahead = 4;
        return TryRead<int?>("enemy.revive.in_turns", () =>
        {
            MonsterState? state = creature.Monster?.NextMove;
            for (int turns = 1; turns <= maxLookahead; turns++)
            {
                if (state is not MoveState move)
                    return null;
                if (move.Intents.Any(intent => intent.IntentType == IntentType.Heal))
                    return turns;
                state = move.FollowUpState;
            }
            return null;
        });
    }

    /// <summary>
    /// The power that is keeping this corpse in the fight, i.e. the one that answered no
    /// to ShouldCreatureBeRemovedFromCombatAfterDeath. Null when the creature is being
    /// held by something other than one of its own powers.
    /// </summary>
    private static PowerModel? FindPowerKeepingCorpseInCombat(Creature creature)
    {
        foreach (var power in creature.Powers)
        {
            var keepsCorpse = TryRead<bool?>("enemy.revive.power",
                () => !power.ShouldCreatureBeRemovedFromCombatAfterDeath(creature));
            if (keepsCorpse == true)
                return power;
        }
        return null;
    }

    private static readonly Dictionary<Type, PropertyInfo[]> _monsterStatProps = new();

    private static Dictionary<string, object?>? BuildMonsterStats(MonsterModel monster)
    {
        // No readable stats is a normal shape for a move-less or state-dependent
        // monster, so this whole read is quiet.
        return TryReadQuiet<Dictionary<string, object?>?>(() =>
        {
            var t = monster.GetType();
            if (!_monsterStatProps.TryGetValue(t, out var props))
            {
                // NonPublic too: move-stat getters (SeaKickDamage, BubbleBlock,
                // ...) are typically protected — only the move definitions
                // read them.
                props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                                        | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(p => p.PropertyType == typeof(int) && p.CanRead
                                && p.GetIndexParameters().Length == 0)
                    .ToArray();
                _monsterStatProps[t] = props;
            }
            if (props.Length == 0)
                return null;
            var stats = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in props)
            {
                // A getter may depend on combat state that is not up during this read.
                var value = TryReadQuiet<int?>(() => (int)p.GetValue(monster)!);
                if (value != null)
                    stats[p.Name] = value.Value;
            }
            return stats.Count > 0 ? stats : null;
        });
    }

    // AbstractIntent.IntentLabelFormat is protected, so reach it once by reflection
    // and cache it. There is no public unpowered label renderer to call instead.
    private static readonly PropertyInfo? _intentLabelFormat =
        typeof(AbstractIntent).GetProperty(
            "IntentLabelFormat",
            BindingFlags.Instance
                | BindingFlags.NonPublic
                | BindingFlags.Public);

    /// <summary>
    /// Reproduces SingleAttackIntent.GetIntentLabel / MultiAttackIntent.GetIntentLabel
    /// against the raw DamageCalc, i.e. without the Hook.ModifyDamage pass those go
    /// through. Returns null when the label cannot be rebuilt, in which case the
    /// caller keeps the game's own (powered) label and does NOT claim it is unbaked -
    /// a wrong claim is worse for a consumer than a baked number it expects.
    /// </summary>
    private static string? UnpoweredAttackLabel(AttackIntent attack)
    {
        return TryRead(() =>
        {
            var calc = attack.DamageCalc;
            if (calc == null || _intentLabelFormat == null)
                return null;
            if (_intentLabelFormat.GetValue(attack) is not LocString format)
                return null;

            format.Add("Damage", Math.Max(0, (int)calc()));
            format.Add("Repeat", attack.Repeats);
            return format.GetFormattedText();
        });
    }

    private static List<Dictionary<string, object?>> BuildIntentList(
        IEnumerable<AbstractIntent> moveIntents, Creature creature)
    {
        var intents = new List<Dictionary<string, object?>>();
        foreach (var intent in moveIntents)
        {
            var intentData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = intent.IntentType.ToString(),
                // The concrete class is always available and never throws, so
                // clients can recognise intents whose label or type is unhelpful.
                ["intent_class"] = intent.GetType().Name,
                // A stunned creature performs no logic on its stunned turn
                // (CreatureCmd.Stun); nothing else in the payload says so.
                ["is_stun"] = intent is StunIntent
            };
            AppendIntentNumbers(intentData, intent);
            AppendIntentLabel(intentData, intent, creature);
            intents.Add(intentData);
        }
        return intents;
    }

    /// <summary>
    /// Structured effect numbers straight off the intent object - exact, no label parsing
    /// needed downstream. The damage calc can fail outside combat.
    /// </summary>
    private static void AppendIntentNumbers(Dictionary<string, object?> intentData, AbstractIntent intent)
        => TryDo(() =>
        {
            if (intent is AttackIntent atk)
            {
                var calc = atk.DamageCalc;
                if (calc != null)
                    intentData["damage"] = (int)calc();
                intentData["hits"] = Math.Max(1, atk.Repeats);
                // Powers of the attacker's own that the damage calculation
                // reads, so a consumer knows which of its in-sim changes
                // move this number (see PowersScalingDamage).
                var scaling = PowersScalingDamage(calc);
                if (scaling != null)
                    intentData["scales_with"] = scaling;
            }
            else if (intent is StatusIntent status)
            {
                intentData["card_count"] = status.CardCount;
            }
        });

    /// <summary>The rendered intent text, which some intent types cannot produce.</summary>
    private static void AppendIntentLabel(
        Dictionary<string, object?> intentData, AbstractIntent intent, Creature creature)
        => TryDo(() =>
        {
            var targets = creature.CombatState?.PlayerCreatures;
            if (targets == null)
                return;
            // AttackIntent.GetSingleDamage runs the label's number through
            // Hook.ModifyDamage, which bakes in attacker Strength/Weak, target
            // Vulnerable and the Back Attack x1.5. In unpowered mode rebuild the
            // label from the raw DamageCalc instead. Nothing else bakes: the
            // Defend/Debuff/Buff labels carry no numbers at all.
            string? label = UnpoweredValues && intent is AttackIntent attack
                ? UnpoweredAttackLabel(attack)
                : null;
            if (label != null)
                intentData["unbaked"] = true;
            else
                label = intent.GetIntentLabel(targets, creature).GetFormattedText();
            intentData["label"] = StripRichTextTags(label);

            var hoverTip = intent.GetHoverTip(targets, creature);
            if (hoverTip.Title != null)
                intentData["title"] = StripRichTextTags(hoverTip.Title);
            if (hoverTip.Description != null)
                intentData["description"] = StripRichTextTags(hoverTip.Description);
        });

    private static void CollectSeenRooms(
        System.Collections.Generic.IReadOnlyList<MegaCrit.Sts2.Core.Runs.History.MapPointHistoryEntry> entries,
        List<Dictionary<string, object?>> monsters,
        List<Dictionary<string, object?>> elites,
        List<Dictionary<string, object?>> events)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry?.Rooms == null)
                continue;

            // One map point can hold several rooms (an event that turns into a fight), so
            // walk them all and classify by RoomType - the map point's own type would lose
            // what a `?` resolved into.
            bool viaEvent = entry.Rooms.Count > 1;
            foreach (var room in entry.Rooms)
            {
                var target = SeenRoomTarget(room.RoomType, monsters, elites, events);
                if (target == null)
                    continue;
                target.Add(BuildSeenRoomInfo(room, i + 1, viaEvent));
            }
        }
    }

    private static List<Dictionary<string, object?>>? SeenRoomTarget(
        RoomType roomType,
        List<Dictionary<string, object?>> monsters,
        List<Dictionary<string, object?>> elites,
        List<Dictionary<string, object?>> events)
        => roomType switch
        {
            RoomType.Monster => monsters,
            RoomType.Elite => elites,
            RoomType.Event => events,
            _ => null            // Boss is already in map.boss; Shop/Treasure/RestSite carry nothing
        };

    private static Dictionary<string, object?> BuildSeenRoomInfo(
        MegaCrit.Sts2.Core.Runs.History.MapPointRoomHistoryEntry room,
        int floor,
        bool viaEvent)
    {
        var info = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["floor"] = floor,           // 1-based within the act
            ["id"] = SafeGetText(() => room.ModelId?.Entry)
        };

        if (room.ModelId != null)
        {
            info["name"] = room.RoomType == RoomType.Event
                ? SafeGetText(() => SaveUtil.EventOrDeprecated(room.ModelId).Title)
                : SafeGetText(() => SaveUtil.EncounterOrDeprecated(room.ModelId).Title);
        }

        // The monsters the encounter actually resolved to - random line-ups differ
        // between two rooms with the same encounter id.
        if (room.MonsterIds != null && room.MonsterIds.Count > 0)
        {
            info["monsters"] = room.MonsterIds
                .Select(id => SafeGetText(() => SaveUtil.MonsterOrDeprecated(id).Title)
                              ?? SafeGetText(() => id.Entry) ?? "?")
                .ToList();
        }

        if (viaEvent && room.RoomType != RoomType.Event)
            info["via_event"] = true;

        return info;
    }

    // True while an event room is loaded and still needs the player: either live
    // dialogue to advance (ancient events) or at least one option that hasn't been
    // chosen yet. Used to report "event" over a lingering-visible map, without
    // deadlocking once the event has fully resolved (no buttons / all chosen).
    private static bool EventHasPendingAction()
    {
        var uiRoom = NEventRoom.Instance;
        if (uiRoom == null)
            return false;

        // Ancient-event dialogue phase (may precede any option buttons).
        var ancientLayout = FindFirst<NAncientEventLayout>(uiRoom);
        if (ancientLayout != null)
        {
            var hitbox = ancientLayout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
            if (hitbox != null && hitbox.Visible && hitbox.IsEnabled)
                return true;
        }

        // Any option still choosable (not already resolved).
        return FindAll<NEventOptionButton>(uiRoom).Any(b => !b.Option.WasChosen);
    }

    private static Dictionary<string, object?> BuildEventState(EventRoom eventRoom)
    {
        var eventModel = eventRoom.LocalMutableEvent ?? eventRoom.CanonicalEvent;
        bool isAncient = eventModel is AncientEventModel;
        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_id"] = eventModel.Id.Entry,
            ["event_name"] = SafeGetText(() => eventModel.Title),
            ["is_ancient"] = isAncient
        };
        AppendEventContext(state, eventRoom, eventModel, isAncient);
        return state;
    }

    /// <summary>
    /// The parts of an event that are not its option list: the Linger damage, the
    /// dialogue phase, the body text, and whether a transition makes options refuse.
    /// </summary>
    private static void AppendEventContext(
        Dictionary<string, object?> state, EventRoom eventRoom, EventModel eventModel, bool isAncient)
    {
        // Repeated Linger pages reuse the option key while damage keeps rising.
        // Read the local mutable copy: CanonicalEvent always has base damage.
        if (string.Equals(eventModel.Id.Entry, "ABYSSAL_BATHS", StringComparison.Ordinal) && eventRoom.LocalMutableEvent is { } mutableBaths)
            state["next_damage"] = mutableBaths.DynamicVars.Damage.BaseValue;

        // Check dialogue state for ancients
        bool inDialogue = false;
        var uiRoom = NEventRoom.Instance;
        if (isAncient && uiRoom != null)
        {
            var ancientLayout = FindFirst<NAncientEventLayout>(uiRoom);
            if (ancientLayout != null)
            {
                var hitbox = ancientLayout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
                inDialogue = hitbox != null && hitbox.Visible && hitbox.IsEnabled;
            }
        }
        state["in_dialogue"] = inDialogue;

        // Event body text
        state["body"] = SafeGetText(() => eventModel.Description);

        // A run transition in flight makes ExecuteChooseEventOption refuse EVERY
        // option, so the options must not be advertised as clickable during it -
        // otherwise a client re-picks the same option on every poll and gets one
        // API error per tick until the transition lands. The window is not just
        // the "option body is still resolving" case the action gate was written
        // for: it also covers entry into the event room itself, and that window
        // is wide open with Instant Mode, which draws the interactive event
        // immediately while the game's own room fade still runs (RoomFadeIn only
        // special-cases Fast, so Instant falls into the 0.8s branch).
        // Deliberately the same call the action uses, so the state can never
        // advertise an option the action would then refuse.
        bool transitionInFlight = IsRunTransitionInFlight();
        state["transition_in_flight"] = transitionInFlight;
        // The two halves of IsMapScreenOpenOrVisible(), separately, because the
        // difference between them decides whether a room behind a lingering map
        // is masked or reported as "map" — and when that goes wrong the symptom
        // is a room the client never sees at all, with nothing in the state
        // saying why. Close() clears IsOpen and only then animates out, so
        // screen_open false with screen_visible true IS the arrival window.
        state["screen_open"] = NMapScreen.Instance?.IsOpen == true;
        state["screen_visible"] = NMapScreen.Instance != null && IsNodeVisible(NMapScreen.Instance);

        state["options"] = BuildEventOptions(uiRoom, transitionInFlight);
    }

    private static List<Dictionary<string, object?>> BuildEventOptions(
        NEventRoom? uiRoom, bool transitionInFlight)
    {
        var options = new List<Dictionary<string, object?>>();
        if (uiRoom == null)
            return options;

        var buttons = FindAll<NEventOptionButton>(uiRoom)
            .Where(button => button.Visible && button.IsVisibleInTree())
            .ToList();
        int index = 0;
        foreach (var button in buttons)
        {
            // Choosing a non-shared option clears the option container
            // (RemoveChildSafely + QueueFreeSafely), so a poll landing on
            // that frame can meet a button that is already gone. Skip it
            // rather than letting ObjectDisposedException fail the whole
            // state build - the index still advances so it keeps lining up
            // with what choose_event_option sees.
            TryDo(() => options.Add(BuildEventOption(button, index, transitionInFlight)));
            index++;
        }
        return options;
    }

    private static Dictionary<string, object?> BuildEventOption(
        NEventOptionButton button, int index, bool transitionInFlight)
    {
        var opt = button.Option;
        var optData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = index,
            // The option's stable, language-independent id
            // ("SELF_HELP_BOOK.pages.INITIAL.options.READ_THE_BACK").
            // Title and description are localized display text, so
            // matching on them would break on a reworded or
            // translated option; this does not.
            ["option_key"] = opt.TextKey,
            ["title"] = SafeGetText(() => opt.Title),
            ["description"] = SafeGetText(() => opt.Description),
            ["is_locked"] = opt.IsLocked,
            ["is_proceed"] = opt.IsProceed,
            ["was_chosen"] = opt.WasChosen,
            // False while a choice is executing (DisableOptionButtons)
            // or while a room transition is resolving.
            // choose_event_option rejects the option in both windows,
            // so clients must not re-pick it.
            ["is_enabled"] = button.IsEnabled && !transitionInFlight,
            ["is_visible"] = true,
            ["can_choose"] = button.IsEnabled && !opt.IsLocked
        };
        if (opt.Relic != null)
        {
            optData["relic_id"] = opt.Relic.Id.Entry;
            optData["relic_name"] = SafeGetText(() => opt.Relic.Title);
            optData["relic_description"] = SafeGetText(() => opt.Relic.DynamicDescription);
            optData["relic_rarity"] = opt.Relic.Rarity.ToString();
            optData["relic_keywords"] = BuildHoverTips(opt.Relic.HoverTipsExcludingRelic);
        }
        optData["keywords"] = BuildHoverTips(opt.HoverTips);
        return optData;
    }

    private static Dictionary<string, object?> BuildFakeMerchantState(EventRoom eventRoom)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        // LocalMutableEvent holds the per-player mutable copy with populated inventory;
        // CanonicalEvent is the shared template which may not have it.
        var fakeMerchant = (FakeMerchant)(eventRoom.LocalMutableEvent ?? eventRoom.CanonicalEvent);

        state["event_id"] = fakeMerchant.Id.Entry;
        state["event_name"] = SafeGetText(() => fakeMerchant.Title);
        state["started_fight"] = fakeMerchant.StartedFight;

        var fakeMerchantNode = NEventRoom.Instance is { } uiRoom
            ? FindFirst<NFakeMerchant>(uiRoom)
            : null;

        if (fakeMerchant.StartedFight)
        {
            // After the foul potion fight, merchant is gone - just show proceed
            state["shop"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["items"] = new List<Dictionary<string, object?>>(),
                ["inventory_open"] = false,
                ["can_proceed"] = true
            };
            state["message"] = "The fake merchant has been defeated. Proceed to map.";
            return state;
        }

        AutoOpenFakeMerchantInventory(fakeMerchantNode);
        state["shop"] = BuildFakeMerchantShop(fakeMerchant, fakeMerchantNode);
        return state;
    }

    /// <summary>
    /// ForceClick the merchant button so the proper signal chain runs (disables proceed,
    /// wires the InventoryClosed callback), unless the caller asked for a passive read.
    /// </summary>
    private static void AutoOpenFakeMerchantInventory(NFakeMerchant? fakeMerchantNode)
    {
        if (fakeMerchantNode == null)
            return;
        var inventoryUI = FindFirst<NMerchantInventory>(fakeMerchantNode);
        if (inventoryUI == null || inventoryUI.IsOpen)
            return;
        var merchantButton = fakeMerchantNode.MerchantButton;
        if (!_passiveRead && merchantButton != null && merchantButton.Visible && merchantButton.IsEnabled)
            merchantButton.ForceClick();
    }

    private static Dictionary<string, object?> BuildFakeMerchantShop(
        FakeMerchant fakeMerchant, NFakeMerchant? fakeMerchantNode)
    {
        // Build shop inventory from the FakeMerchant model
        var shopState = BuildFakeMerchantShopItems(fakeMerchant.Inventory);
        bool isOpen = fakeMerchantNode != null
            && FindFirst<NMerchantInventory>(fakeMerchantNode)?.IsOpen == true;
        shopState["inventory_open"] = isOpen;

        if (fakeMerchantNode == null)
        {
            shopState["inventory_open"] = false;
            shopState["can_close_inventory"] = false;
            shopState["can_proceed"] = false;
        }
        else
        {
            var inventoryUI = FindFirst<NMerchantInventory>(fakeMerchantNode);
            var proceedButton = FindFirst<NProceedButton>(fakeMerchantNode);
            var inventoryOpen = inventoryUI?.IsOpen == true;
            var canCloseInventory = inventoryOpen
                && IsControlVisibleOrActionable(FindFirst<NBackButton>(fakeMerchantNode));
            shopState["inventory_open"] = inventoryOpen;
            shopState["can_close_inventory"] = canCloseInventory;
            shopState["can_proceed"] = IsControlVisibleOrActionable(proceedButton) || canCloseInventory;
        }
        shopState["available_actions"] = BuildShopAvailableActions(isOpen);
        return shopState;
    }

    private static Dictionary<string, object?> BuildFakeMerchantShopItems(MerchantInventory? inventory)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (inventory == null)
        {
            state["items"] = new List<Dictionary<string, object?>>();
            state["error"] = "Fake merchant inventory is not ready yet; retry in a moment.";
            return state;
        }

        var items = new List<Dictionary<string, object?>>();
        int index = 0;

        // FakeMerchant only sells relics (no cards, potions, or card removal)
        foreach (var entry in inventory.RelicEntries)
        {
            var item = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = index,
                ["category"] = "relic",
                ["price"] = entry.Cost,
                ["is_stocked"] = entry.IsStocked,
                ["can_afford"] = entry.EnoughGold,
                ["can_purchase"] = entry.IsStocked && entry.EnoughGold
            };
            if (entry.Model is { } relic)
            {
                item["relic_id"] = relic.Id.Entry;
                item["relic_name"] = SafeGetText(() => relic.Title);
                item["relic_description"] = SafeGetText(() => relic.DynamicDescription);
                item["relic_rarity"] = relic.Rarity.ToString();
                item["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic);
            }
            items.Add(item);
            index++;
        }

        state["items"] = items;
        return state;
    }

    private static Dictionary<string, object?> BuildRestSiteState(RestSiteRoom restSiteRoom)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);

        var uiRoom = NRestSiteRoom.Instance;
        var options = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var opt in restSiteRoom.Options)
        {
            // RestSiteOption.IsEnabled is a static property of the option (HEAL
            // is unconditionally true); the room's button carries the live gate,
            // which DisableOptions() takes down for as long as a chosen option is
            // resolving - and a heal that offers rewards (Tiny Mailbox's potions)
            // stays in the option list for that whole window. Report the stricter
            // of the two so no client reads an option as actionable while another
            // is mid-flight. The buttons are rebuilt, enabled, once it finishes;
            // while the room UI has not built them yet, the model stands alone.
            var button = uiRoom?.GetButtonForOption(opt);
            bool enabled = opt.IsEnabled && (button?.IsEnabled ?? true);
            bool hasButton = uiRoom != null && FindRestSiteButton(uiRoom, opt) != null;
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = index,
                ["id"] = opt.OptionId,
                ["name"] = SafeGetText(() => opt.Title),
                ["description"] = SafeGetText(() => opt.Description),
                ["is_enabled"] = enabled,
                // Fork-compatible aliases for the same gate.
                ["is_visible"] = hasButton,
                ["can_choose"] = enabled,
                // A model option with no button is exactly the case where counting
                // buttons positionally would have pressed the wrong option.
                ["has_button"] = hasButton
            });
            index++;
        }
        state["options"] = options;

        state["room_open"] = uiRoom != null;
        var proceedButton = uiRoom?.ProceedButton;
        state["can_proceed"] = IsControlVisibleOrActionable(proceedButton);

        return state;
    }

    /// <summary>
    /// True while a GET /api/v1/singleplayer?passive=1 is building the state: the screens that
    /// normally nudge the UI while being read (merchant inventory, fake merchant, treasure chest)
    /// leave it alone. Only touched on the main thread (inside RunOnMainThreadBlocking).
    /// </summary>
    private static bool _passiveRead;

    private static Dictionary<string, object?> BuildShopState(MerchantRoom merchantRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        bool isOpen = NMerchantRoom.Instance?.Inventory?.IsOpen ?? false;
        state["is_open"] = isOpen;
        state["available_actions"] = BuildShopAvailableActions(isOpen);

        var inventory = GetMerchantInventory(merchantRoom);
        if (inventory == null)
        {
            state["items"] = new List<Dictionary<string, object?>>();
            state["inventory_open"] = NMerchantRoom.Instance?.Inventory?.IsOpen ?? false;
            state["can_proceed"] = IsControlVisibleOrActionable(NMerchantRoom.Instance?.ProceedButton);
            state["error"] =
                "Shop inventory is not ready yet (null). Often happens right after entering the merchant from the map; retry in a moment.";
            return state;
        }

        var items = new List<Dictionary<string, object?>>();
        AppendShopCards(items, inventory);
        AppendShopRelics(items, inventory);
        AppendShopPotions(items, inventory);
        AppendShopCardRemoval(items, inventory);
        state["items"] = items;

        AppendSellablePotions(state, runState);
        AppendShopControls(state);
        return state;
    }

    /// <summary>One shop row: price and stock, which every category carries.</summary>
    private static Dictionary<string, object?> BuildShopRow(
        int index, string category, int price, bool isStocked, bool canAfford)
        => new(StringComparer.Ordinal)
        {
            ["index"] = index,
            ["category"] = category,
            ["price"] = price,
            ["is_stocked"] = isStocked,
            ["can_afford"] = canAfford,
            ["can_purchase"] = isStocked && canAfford
        };

    private static void AppendShopCards(
        List<Dictionary<string, object?>> items, MerchantInventory inventory)
    {
        foreach (var entry in inventory.CardEntries)
        {
            var item = BuildShopRow(items.Count, "card", entry.Cost, entry.IsStocked, entry.EnoughGold);
            item["on_sale"] = entry.IsOnSale;
            if (entry.CreationResult?.Card is { } card)
            {
                var cardInfo = BuildCardInfo(card);
                item["card_id"] = cardInfo["id"];
                item["card_name"] = cardInfo["name"];
                item["card_type"] = cardInfo["type"];
                item["card_cost"] = cardInfo["cost"];
                item["card_star_cost"] = cardInfo["star_cost"];
                item["card_rarity"] = cardInfo["rarity"];
                item["card_description"] = cardInfo["description"];
                item["card_is_upgraded"] = cardInfo["is_upgraded"];
                item["card_is_upgradable"] = cardInfo["is_upgradable"];
                item["card_current_upgrade_level"] = cardInfo["current_upgrade_level"];
                item["card_max_upgrade_level"] = cardInfo["max_upgrade_level"];
                item["card_upgrade_preview_type"] = cardInfo["upgrade_preview_type"];
                item["card_upgrade_preview_cost"] = cardInfo["upgrade_preview_cost"];
                item["card_upgrade_preview_star_cost"] = cardInfo["upgrade_preview_star_cost"];
                item["card_upgrade_preview_description"] = cardInfo["upgrade_preview_description"];
                item["keywords"] = cardInfo["keywords"];
                item["is_upgraded"] = cardInfo["is_upgraded"];
                item["enchantment"] = cardInfo["enchantment"];
                item["enchantment_state"] = cardInfo["enchantment_state"];
            }
            items.Add(item);
        }
    }

    private static void AppendShopRelics(
        List<Dictionary<string, object?>> items, MerchantInventory inventory)
    {
        foreach (var entry in inventory.RelicEntries)
        {
            var item = BuildShopRow(items.Count, "relic", entry.Cost, entry.IsStocked, entry.EnoughGold);
            if (entry.Model is { } relic)
            {
                item["relic_id"] = relic.Id.Entry;
                item["relic_name"] = SafeGetText(() => relic.Title);
                item["relic_description"] = SafeGetText(() => relic.DynamicDescription);
                item["relic_rarity"] = relic.Rarity.ToString();
                item["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic);
            }
            items.Add(item);
        }
    }

    private static void AppendShopPotions(
        List<Dictionary<string, object?>> items, MerchantInventory inventory)
    {
        foreach (var entry in inventory.PotionEntries)
        {
            var item = BuildShopRow(items.Count, "potion", entry.Cost, entry.IsStocked, entry.EnoughGold);
            if (entry.Model is { } potion)
            {
                item["potion_id"] = potion.Id.Entry;
                item["potion_name"] = SafeGetText(() => potion.Title);
                item["potion_description"] = SafeGetText(() => potion.DynamicDescription);
                item["potion_rarity"] = potion.Rarity.ToString();
                item["potion_target_type"] = potion.TargetType.ToString();
                item["potion_usage"] = potion.Usage.ToString();
                item["keywords"] = BuildHoverTips(potion.ExtraHoverTips);
            }
            items.Add(item);
        }
    }

    private static void AppendShopCardRemoval(
        List<Dictionary<string, object?>> items, MerchantInventory inventory)
    {
        if (inventory.CardRemovalEntry is not { } removal)
            return;
        items.Add(BuildShopRow(
            items.Count, "card_removal", removal.Cost, removal.IsStocked, removal.EnoughGold));
    }

    /// <summary>
    /// Potions the merchant will buy (Foul Potion sells for gold instead of exploding).
    /// There was no sign anywhere in state that this was even possible.
    /// </summary>
    private static void AppendSellablePotions(Dictionary<string, object?> state, RunState runState)
    {
        var localPlayer = LocalContext.GetMe(runState);
        if (localPlayer == null)
            return;

        var sellable = new List<Dictionary<string, object?>>();
        for (int slot = 0; slot < localPlayer.PotionSlots.Count; slot++)
        {
            var held = localPlayer.GetPotionAtSlotIndex(slot);
            if (held == null || !CanSellPotionToMerchant(localPlayer, held))
                continue;
            sellable.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slot"] = slot,
                ["potion_id"] = held.Id.Entry,
                ["potion_name"] = SafeGetText(() => held.Title),
                ["action"] = "sell_potion"
            });
        }
        if (sellable.Count > 0)
            state["sellable_potions"] = sellable;
    }

    private static void AppendShopControls(Dictionary<string, object?> state)
    {
        var merchantRoomNode = NMerchantRoom.Instance;
        var inventoryOpen = merchantRoomNode?.Inventory?.IsOpen == true;
        var backButton = merchantRoomNode != null ? FindFirst<NBackButton>(merchantRoomNode) : null;
        var canCloseInventory = inventoryOpen && IsControlVisibleOrActionable(backButton);
        state["inventory_open"] = inventoryOpen;
        state["can_close_inventory"] = canCloseInventory;

        var proceedButton = merchantRoomNode?.ProceedButton;
        state["can_proceed"] = IsControlVisibleOrActionable(proceedButton) || canCloseInventory;
    }

    private static List<Dictionary<string, object?>> BuildShopAvailableActions(bool isOpen)
    {
        return new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal)
            {
                ["action"] = "shop_purchase",
                ["parameter"] = "index",
                ["description"] = "Purchase an item from the shop items list."
            },
            new(StringComparer.Ordinal)
            {
                ["action"] = "proceed",
                ["description"] = isOpen
                    ? "Close the shop inventory. Call proceed again after it closes to leave the room."
                    : "Leave the shop room and return to the map."
            }
        };
    }

    // The travelable next-node options read from the live map UI. Shared by
    // BuildMapState and the masked-shop state (the bot leaves a masked shop by
    // travelling to the next node, since the merchant ProceedButton isn't enabled
    // while the map lingers over the shop).
    private static List<Dictionary<string, object?>> BuildMapNextOptions(
        IReadOnlyDictionary<(int, int), List<string>>? marks = null)
    {
        var nextOptions = new List<Dictionary<string, object?>>();
        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null)
            return nextOptions;

        var travelable = GetTravelableMapPoints(mapScreen);

        foreach (var (pt, index) in travelable.Select((nmp, index) => (pt: nmp.Point, index)))
        {
            var option = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = index,
                ["col"] = pt.coord.col,
                ["row"] = pt.coord.row,
                ["type"] = pt.PointType.ToString(),
                ["is_visible"] = true,
                ["can_travel"] = true
            };
            AddMapNodeMarks(option, pt, marks);

            // 1-level lookahead
            var children = pt.Children
                .OrderBy(c => c.coord.col)
                .Select(c => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["col"] = c.coord.col,
                    ["row"] = c.coord.row,
                    ["type"] = c.PointType.ToString()
                }).ToList();
            if (children.Count > 0)
                option["leads_to"] = children;

            nextOptions.Add(option);
        }
        return nextOptions;
    }

    private static Dictionary<string, object?> BuildMapState(RunState runState)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        var map = runState.Map;
        var marks = CollectMapMarks(runState);
        AppendMapMarks(state, marks);
        AppendMapPosition(state, map, runState.VisitedMapCoords);
        // `visited` only carries the map-point TYPE, so point_history is the only
        // place a "?" reports the room it became.
        state["point_history"] = BuildMapPointHistory(runState);
        AppendMapTravelState(state, marks);

        // Map-point layout includes per-run jitter. Expose the live visual
        // anchors in the same normalized coordinate space accepted by map_draw.
        var drawingNodePositions = BuildMapDrawingNodePositions(NMapScreen.Instance);
        var nodeSet = BuildMapNodeSet(runState, map, marks, drawingNodePositions);
        state["nodes"] = nodeSet.Nodes;

        var encountersSeen = BuildEncountersSeen(runState);
        if (encountersSeen.Count > 0)
            state["encounters_seen"] = encountersSeen;
        AppendMapBossSummary(state, map, nodeSet);
        return state;
    }

    private static void AppendMapMarks(
        Dictionary<string, object?> state, Dictionary<(int, int), List<string>> marks)
    {
        if (marks.Count == 0)
            return;
        state["marked_nodes"] = marks
            .OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2)
            .Select(kv => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["col"] = kv.Key.Item1,
                ["row"] = kv.Key.Item2,
                ["marked_by"] = kv.Value
            })
            .ToList();
    }

    /// <summary>Where the player is and everywhere they have been this act.</summary>
    private static void AppendMapPosition(
        Dictionary<string, object?> state, ActMap map, IReadOnlyList<MapCoord> visitedCoords)
    {
        // Current position
        if (visitedCoords.Count > 0)
        {
            var cur = visitedCoords[visitedCoords.Count - 1];
            state["current_position"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["col"] = cur.col,
                ["row"] = cur.row,
                ["type"] = map.GetPoint(cur)?.PointType.ToString()
            };
        }

        // Visited path
        var visited = new List<Dictionary<string, object?>>();
        foreach (var coord in visitedCoords)
        {
            visited.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["col"] = coord.col,
                ["row"] = coord.row,
                ["type"] = map.GetPoint(coord)?.PointType.ToString()
            });
        }
        state["visited"] = visited;
    }

    /// <summary>
    /// The travel options and the flags that explain an empty list. next_options is
    /// empty whenever the current room is unfinished or a travel is already resolving,
    /// so the three flags below say WHY rather than leaving a client guessing.
    /// </summary>
    private static void AppendMapTravelState(
        Dictionary<string, object?> state, Dictionary<(int, int), List<string>> marks)
    {
        state["next_options"] = BuildMapNextOptions(marks);
        var mapScreen = NMapScreen.Instance;
        state["travel_enabled"] = mapScreen != null && mapScreen.IsTravelEnabled;
        state["travel_in_flight"] = IsMapTravelInFlight();
        // Third reason for an empty next_options: an act change is still
        // resolving behind this map. See GetTravelableMapPoints.
        state["transition_in_flight"] = IsRunTransitionInFlight();
        state["drawing_coordinate_space"] = BuildMapDrawingCoordinateSpace();
    }

    /// <summary>The full map, plus the boss identity each boss node needs.</summary>
    private sealed record MapNodeSet(
        List<Dictionary<string, object?>> Nodes,
        string? PrimaryBossId,
        string? PrimaryBossName,
        Dictionary<string, object?>? SecondBoss);

    /// <summary>
    /// All nodes organized for planning: the start, the grid, and both bosses. Boss
    /// identity comes from the live act's EncounterModel - BossEncounter throws if the
    /// act hasn't finished setup yet, so guard the access.
    /// </summary>
    private static MapNodeSet BuildMapNodeSet(
        RunState runState,
        ActMap map,
        Dictionary<(int, int), List<string>> marks,
        Dictionary<(int col, int row), Vector2> drawingNodePositions)
    {
        // Full map - all nodes organized for planning
        var nodes = new List<Dictionary<string, object?>>();

        // Starting point
        var start = map.StartingMapPoint;
        nodes.Add(BuildMapNode(start, marks, drawingNodePositions));

        // Grid nodes
        foreach (var pt in map.GetAllMapPoints())
            nodes.Add(BuildMapNode(pt, marks, drawingNodePositions));

        EncounterModel? bossEncounter = null;
        TryDo("map.boss", () => { bossEncounter = runState.Act.BossEncounter; });
        var secondBossEncounter = runState.Act.SecondBossEncounter;

        var primaryBossId = bossEncounter?.Id?.Entry;
        var primaryBossName = SafeGetText(() => bossEncounter?.Title);
        var bossNode = BuildMapNode(map.BossMapPoint, marks, drawingNodePositions);
        AddBossIdentity(bossNode, primaryBossId, primaryBossName);
        nodes.Add(bossNode);

        Dictionary<string, object?>? secondBoss = null;
        if (map.SecondBossMapPoint != null)
        {
            var secondBossId = secondBossEncounter?.Id?.Entry;
            var secondBossName = SafeGetText(() => secondBossEncounter?.Title);
            var secondBossNode = BuildMapNode(map.SecondBossMapPoint, marks, drawingNodePositions);
            AddBossIdentity(secondBossNode, secondBossId, secondBossName);
            nodes.Add(secondBossNode);
            secondBoss = BuildBossInfo(map.SecondBossMapPoint, secondBossId, secondBossName);
        }

        return new MapNodeSet(nodes, primaryBossId, primaryBossName, secondBoss);
    }

    private static void AppendMapBossSummary(
        Dictionary<string, object?> state, ActMap map, MapNodeSet nodeSet)
    {
        var primaryBoss = BuildBossInfo(map.BossMapPoint, nodeSet.PrimaryBossId, nodeSet.PrimaryBossName);
        state["boss"] = primaryBoss;
        state["bosses"] = nodeSet.SecondBoss != null
            ? new List<Dictionary<string, object?>> { primaryBoss, nodeSet.SecondBoss }
            : new List<Dictionary<string, object?>> { primaryBoss };
    }

    /// <summary>
    /// What this act has already served. The game keeps its own record of every map point
    /// entered (<see cref="RunState.MapPointHistory"/>), and shows the same thing to the
    /// player: hovering a travelled node on the map opens NMapPointHistoryHoverTip, which
    /// prints the encounter's title. So this is information the human already has.
    ///
    /// This deliberately replaces the old `next_encounters` field, which peeked at the act's
    /// pre-rolled RoomSet and reported which monster / elite / event would come NEXT. The game
    /// does not reveal that until the room is entered, so reporting it let an agent route
    /// around matchups no human could see. Removing it is why schema_version went to 3.
    ///
    /// We report what was fought, never what remains: listing the unused pool would let a
    /// caller deduce the next encounter by elimination, which is the same leak by another name.
    /// `elite_cycle` is the exception and is safe - it is a count, derivable by any player from
    /// (a) what they have fought and (b) how many elites the act has, and it encodes the rule
    /// the user cares about: an elite already fought does not return until the pool wraps.
    /// </summary>
    private static Dictionary<string, object?> BuildEncountersSeen(RunState runState)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        TryDo("map.encounters_seen", () => BuildEncountersSeenCore(runState, result));
        return result;
    }

    private static void BuildEncountersSeenCore(RunState runState, Dictionary<string, object?> result)
    {
        var history = runState.MapPointHistory;
        int act = runState.CurrentActIndex;
        if (history == null || act < 0 || act >= history.Count)
            return;

        var monsters = new List<Dictionary<string, object?>>();
        var elites = new List<Dictionary<string, object?>>();
        var events = new List<Dictionary<string, object?>>();
        CollectSeenRooms(history[act], monsters, elites, events);

        if (monsters.Count > 0) result["monster"] = monsters;
        if (elites.Count > 0) result["elite"] = elites;
        if (events.Count > 0) result["event"] = events;
        if (result.Count > 0)
            result["act"] = act + 1;

        var cycle = EliteCycle(runState, elites.Count);
        if (cycle > 0)
            result["elite_cycle"] = cycle;
    }

    /// <summary>
    /// How many times this act's elite pool has been exhausted. 0 while every elite fought so
    /// far is a first encounter, 1 once the pool has wrapped once, and so on.
    ///
    /// The act builds its elite list by drawing the whole pool without replacement and refilling
    /// when it empties, so "visited / pool size" is exactly the number of completed passes.
    /// Prefers the game's own counter and falls back to counting this act's elite rooms.
    /// Only the count is reported - never the pool's contents.
    /// </summary>
    private static object? GetPropertyValue(object target, string propertyName)
        => TryRead($"{target.GetType().Name}.{propertyName}",
            () => target.GetType().GetProperty(propertyName)?.GetValue(target));

    private static int EliteCycle(RunState runState, int elitesFoughtThisAct)
    {
        var poolSize = TryRead<int?>("map.encounters_seen.elite_cycle",
            () => runState.Act.AllEliteEncounters?.Count()) ?? 0;
        if (poolSize <= 0)
            return 0;

        int visited = elitesFoughtThisAct;
        var rooms = GetInstanceFieldValue(runState.Act, "_rooms");
        if (rooms != null && GetInstanceFieldValue(rooms, "eliteEncountersVisited") is int counted)
            visited = counted;

        return visited / poolSize;
    }


    // The caller's name stands in for a field name here: this helper has no idea which
    // part of the state it is feeding, and "bool: NullReferenceException" alone names nothing.
    private static bool? SafeGetBool(Func<bool> getter,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        return TryRead<bool?>($"bool in {caller}", () => getter());
    }

    private static Dictionary<string, object?> BuildBossInfo(MapPoint pt, string? bossId, string? bossName)
    {
        var boss = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["col"] = pt.coord.col,
            ["row"] = pt.coord.row
        };
        AddBossIdentity(boss, bossId, bossName);
        return boss;
    }

    private static void AddBossIdentity(Dictionary<string, object?> target, string? bossId, string? bossName)
    {
        if (string.IsNullOrWhiteSpace(bossId))
            return;

        target["id"] = bossId;
        if (!string.IsNullOrWhiteSpace(bossName))
            target["name"] = bossName;
    }

    private static Dictionary<string, object?> BuildMapNode(
        MapPoint pt,
        IReadOnlyDictionary<(int, int), List<string>>? marks = null,
        Dictionary<(int col, int row), Vector2>? drawingNodePositions = null)
    {
        var node = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["col"] = pt.coord.col,
            ["row"] = pt.coord.row,
            ["type"] = pt.PointType.ToString(),
            ["children"] = pt.Children
                .OrderBy(c => c.coord.col)
                .Select(c => new List<int> { c.coord.col, c.coord.row })
                .ToList()
        };

        AddMapNodeMarks(node, pt, marks);

        if (drawingNodePositions?.TryGetValue((pt.coord.col, pt.coord.row), out var drawPosition) == true)
            node["draw_position"] = BuildMapDrawingPoint(drawPosition);
        return node;
    }

    /// <summary>
    /// Attaches whatever has marked this node. Relics such as Fur Coat mark rooms when
    /// picked up (marked combats spawn enemies at 1 HP), and events attach quests to
    /// nodes — neither was visible anywhere in state or in the save, so the whole point
    /// of the relic (route towards the free fights) could not be acted on.
    /// </summary>
    private static void AddMapNodeMarks(
        Dictionary<string, object?> node,
        MapPoint pt,
        IReadOnlyDictionary<(int, int), List<string>>? marks)
    {
        if (marks != null && marks.TryGetValue((pt.coord.col, pt.coord.row), out var markedBy))
            node["marked_by"] = new List<string>(markedBy);

        TryDo("map.nodes.quests", () =>
        {
            var quests = pt.Quests;
            if (quests != null && quests.Count > 0)
            {
                node["quests"] = quests
                    .Select(q => SafeGetText(() => q.Id.Entry) ?? "unknown")
                    .ToList();
            }
        });

        if (pt.CanBeModified)
            node["can_be_modified"] = true;
    }

    /// <summary>
    /// Collects map coordinates marked by the local player's relics.
    /// Relics advertise their marks through a public GetMarkedCoords(); the lookup is
    /// duck-typed so any relic that grows one is picked up without further changes here.
    /// </summary>
    private static Dictionary<(int, int), List<string>> CollectMapMarks(RunState runState)
    {
        var marks = new Dictionary<(int, int), List<string>>();
        TryDo("map.nodes.marked_by", () => CollectMapMarksCore(runState, marks));
        return marks;
    }

    private static void CollectMapMarksCore(
        RunState runState,
        Dictionary<(int, int), List<string>> marks)
    {
        var player = LocalContext.GetMe(runState);
        if (player == null)
            return;

        int actIndex = runState.CurrentActIndex;
        foreach (var relic in player.Relics)
        {
            var method = relic.GetType().GetMethod("GetMarkedCoords", System.Type.EmptyTypes);
            if (method == null)
                continue;

            // A relic that marked rooms in an earlier act must not paint this act's map.
            var actProperty = relic.GetType().GetProperty("FurCoatActIndex")
                ?? relic.GetType().GetProperty("MarkedActIndex");
            if (actProperty?.GetValue(relic) is int markedAct && markedAct != actIndex)
                continue;

            if (method.Invoke(relic, null) is not System.Collections.IEnumerable coords)
                continue;

            string relicId = SafeGetText(() => relic.Id.Entry) ?? relic.GetType().Name;
            foreach (var coordObj in coords)
            {
                if (coordObj is not MapCoord coord)
                    continue;
                var key = (coord.col, coord.row);
                if (!marks.TryGetValue(key, out var list))
                    marks[key] = list = new List<string>();
                if (!list.Contains(relicId))
                    list.Add(relicId);
            }
        }
    }

    private static FieldInfo? _rewardsSetField;
    private static FieldInfo? _crystalSphereEntityField;
    private static FieldInfo? _crystalSphereCompletionField;
    private static FieldInfo? _cardRewardCompletionField;
    private static FieldInfo? _rewardsIsTerminalField;

    /// <summary>
    /// True while the run is still blocked on this rewards screen's set.
    /// </summary>
    /// <remarks>
    /// Lets the state builder tell a LIVE rewards screen from one that merely
    /// lingers on the overlay stack after the player proceeded (the reason the
    /// rewards branch otherwise defers to a visible map screen). The
    /// synchronizer is the authority: a set is marked completed only once every
    /// reward has been taken or the whole set was skipped, and until then
    /// whatever awaited <c>RewardsSet.Offer</c> - a combat room end, or the
    /// rest-site heal Tiny Mailbox hangs its potions off - cannot continue.
    ///
    /// A TERMINAL screen is excluded, because for it "outstanding" does not mean
    /// "blocking" and the two conditions deadlock: proceeding from a post-combat
    /// screen runs <c>RunManager.ProceedFromTerminalRewardsScreen</c>, which only
    /// OPENS THE MAP - the set is not completed until the run leaves the room,
    /// which needs a map click. So a set holding any declined reward (a potion
    /// refused into a full belt, a skipped card) stays outstanding, masks the map
    /// that would release it, and - since <c>AfterOverlayHidden</c> disables the
    /// proceed button once the map opens on top - leaves a screen with nothing
    /// claimable and can_proceed false, forever. Only a non-terminal set (a
    /// mid-room OfferCustom) is one the run genuinely waits on.
    ///
    /// Both fields are private on the screen; if a game update renames either we
    /// report false, which is exactly the pre-fix behaviour (defer to the map).
    /// Only the top-of-stack screen is inspected, so a card-reward selection
    /// opened FROM a live set is not covered - no known relic or event offers a
    /// card reward this way.
    /// </remarks>
    private static bool RewardsSetIsOutstanding(NRewardsScreen screen)
    {
        return TryRead<bool?>(() =>
        {
            _rewardsIsTerminalField ??= typeof(NRewardsScreen)
                .GetField("_isTerminal", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_rewardsIsTerminalField?.GetValue(screen) is not bool isTerminal || isTerminal)
                return false;

            _rewardsSetField ??= typeof(NRewardsScreen)
                .GetField("_rewardsSet", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_rewardsSetField?.GetValue(screen) is not RewardsSet set)
                return false;
            return !RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set);
        }) ?? false;
    }

    /// <summary>
    /// Names what is blocking a map that cannot be travelled.
    /// </summary>
    /// <remarks>
    /// The map fallback is the only branch reachable while the run is provably
    /// stuck, and it had no guard of its own: every "is this screen actionable"
    /// test above it is per-screen, so a screen nobody thought to guard falls
    /// through to a map with `travel_enabled: false` and no `next_options` -
    /// which is honest about the map and says nothing about the room that is
    /// still awaiting an answer. Three separate stalls reached the client that
    /// way (Tiny Mailbox's potions, Brain Leech's card reward, and the Crystal
    /// Sphere), each costing an investigation to attribute.
    ///
    /// This does not fix the masking - only the missing per-screen guard can -
    /// but it turns the next occurrence from a silent hang into a state that
    /// names the room and the overlay responsible. An empty `next_options` with
    /// nothing in flight means `GetTravelableMapPoints` found nothing because
    /// the current room is unfinished, so anything here is a real blocker.
    /// </remarks>
    private static void AddBlockedMapDiagnostics(
        Dictionary<string, object?> result,
        AbstractRoom? currentRoom,
        object? topOverlay)
    {
        // Diagnostics must never be able to break a state read.
        TryDo("map.blocked_diagnostics", () =>
        {
            if (result["map"] is not Dictionary<string, object?> map)
                return;
            if (map["travel_enabled"] is not false)
                return;
            if (map["travel_in_flight"] is true || map["transition_in_flight"] is true)
                return;

            map["blocked"] = true;
            map["blocked_room"] = currentRoom?.GetType().Name;
            map["blocked_overlay"] = topOverlay?.GetType().Name;
            map["blocked_reason"] =
                "The map is not travelable and nothing is in flight, so the current room has not "
                + "finished. Some screen it is awaiting is not being reported; `blocked_overlay` "
                + "names the overlay on top, if any.";
        });
    }

    /// <summary>
    /// Whether a Crystal Sphere minigame is still being played.
    /// </summary>
    /// <remarks>
    /// The third of the "answered yet?" checks, and the same rule as the other
    /// two: `CrystalSphere.UncoverFuture` and `PaymentPlan` both await
    /// `PlayMinigame()` before `SetEventFinished`, and that await IS the
    /// minigame's own `_completionSource`. The screen holds the minigame in
    /// `_entity`, so the signal is two private fields down; any reflection
    /// failure answers false and leaves the mask as it behaved before.
    /// </remarks>
    private static bool CrystalSphereIsOutstanding(NCrystalSphereScreen screen)
    {
        return TryRead<bool?>(() =>
        {
            _crystalSphereEntityField ??= typeof(NCrystalSphereScreen)
                .GetField("_entity", BindingFlags.NonPublic | BindingFlags.Instance);
            var minigame = _crystalSphereEntityField?.GetValue(screen);
            if (minigame == null)
                return false;
            _crystalSphereCompletionField ??= minigame.GetType()
                .GetField("_completionSource", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_crystalSphereCompletionField?.GetValue(minigame) is not TaskCompletionSource completion)
                return false;
            return !completion.Task.IsCompleted;
        }) ?? false;
    }

    /// <summary>
    /// Whether a card reward screen is still waiting for the run to pick.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="RewardsSetIsOutstanding" />, and it exists for the
    /// same reason: the screen lingers on the overlay stack after it is answered,
    /// so the map is allowed to win THEN - but a pick the run is blocked on must
    /// win over the map, or the state reports a map that cannot be travelled.
    ///
    /// `_completionSource` is the await the screen resolves: non-null and
    /// incomplete means nobody has chosen yet. Any reflection failure answers
    /// false, which leaves the mask exactly as it behaved before.
    /// </remarks>
    private static bool CardRewardIsOutstanding(NCardRewardSelectionScreen screen)
    {
        return TryRead<bool?>(() =>
        {
            _cardRewardCompletionField ??= typeof(NCardRewardSelectionScreen)
                .GetField("_completionSource", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_cardRewardCompletionField?.GetValue(screen) is not TaskCompletionSource<int?> completion)
                return false;
            return !completion.Task.IsCompleted;
        }) ?? false;
    }

    private static readonly FieldInfo? SpecialRewardCardField = typeof(SpecialCardReward).GetField("_card", BindingFlags.NonPublic | BindingFlags.Instance);

    private static Dictionary<string, object?> BuildSpecialCardReward(SpecialCardReward reward)
    {
        var card = SpecialRewardCardField?.GetValue(reward) as CardModel
            ?? throw new InvalidOperationException("A populated SpecialCardReward must expose its exact card");
        var saved = reward.ToSerializable();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "special_card",
            ["card_reference"] = GetCardObjectIdentity(card),
            ["card"] = BuildCardInfo(card),
            ["encounter_source"] = saved.CustomDescriptionEncounterSourceId.Entry
        };
    }

    private static Dictionary<string, object?> BuildRewardsState(NRewardsScreen rewardsScreen)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Reward items
        var rewardButtons = GetRewardButtons(rewardsScreen);
        state["screen_revision"] = _rewardSession.Revision;
        if (!_rewardTrackingAvailable) Warn("rewards.selection_state", "UI tracking unavailable; missing observations are unknown");
        var items = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var button in rewardButtons)
            items.Add(BuildRewardItem(button.Reward!, index++));
        state["items"] = items;
        // Completed rewards stay out of legacy items. This session-local history retains only
        // identities and observed UI outcomes, not hidden reward contents.
        state["selection_history"] = _rewardSession.Entries.Select(e =>
            e.Describe(_rewardSession.Claimable.Contains(e))).ToList();

        // Proceed button
        var proceedButton = FindFirst<NProceedButton>(rewardsScreen);
        state["can_proceed"] = IsControlVisibleOrActionable(proceedButton);

        return state;
    }

    private static Dictionary<string, object?> BuildRewardItem(Reward reward, int index)
    {
        var item = _rewardSession.Find(reward)!.Describe(claimable: true);
        item["index"] = index;
        item["type"] = GetRewardTypeName(reward);
        item["description"] = SafeGetText(() => reward.Description);
        item["is_visible"] = true;
        item["can_claim"] = true;
        AppendRewardDetails(item, reward);
        return item;
    }

    /// <summary>The fields that depend on what kind of reward this is.</summary>
    private static void AppendRewardDetails(Dictionary<string, object?> item, Reward reward)
    {
        if (reward is SpecialCardReward specialCardReward)
        {
            foreach (var pair in BuildSpecialCardReward(specialCardReward))
                item[pair.Key] = pair.Value;
            return;
        }
        if (reward is GoldReward goldReward)
        {
            item["gold_amount"] = goldReward.Amount;
            return;
        }
        if (reward is PotionReward { Potion: { } potion })
        {
            item["potion_id"] = potion.Id.Entry;
            item["potion_name"] = SafeGetText(() => potion.Title);
            item["potion_description"] = SafeGetText(() => potion.DynamicDescription);
            item["potion_rarity"] = potion.Rarity.ToString();
            item["potion_target_type"] = potion.TargetType.ToString();
            item["potion_usage"] = potion.Usage.ToString();
            item["keywords"] = BuildHoverTips(potion.ExtraHoverTips);
            return;
        }
        if (reward is RelicReward relicReward && GetRelicRewardModel(relicReward) is { } relic)
        {
            item["relic_id"] = relic.Id.Entry;
            item["relic_name"] = SafeGetText(() => relic.Title);
            item["relic_description"] = SafeGetText(() => relic.DynamicDescription);
            item["relic_rarity"] = relic.Rarity.ToString();
            item["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic);
        }
    }

    private static Dictionary<string, object?> BuildCardTargetPreview(CardModel card, Creature enemy)
    {
        card.UpdateDynamicVarPreview(CardPreviewMode.Normal, enemy, card.DynamicVars);

        var preview = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["target"] = GetStableEntityId(enemy),
            ["combat_id"] = enemy.CombatId
        };

        // Var names differ per card (Damage, CalculatedDamage, ExtraDamage...),
        // and the typed accessors throw when a card has no var of that name,
        // so read whatever the card actually carries.
        var values = ReadDynamicVarValues(card);
        int? damage = PickDynamicVar(values, "CalculatedDamage", "Damage");
        if (damage != null)
            preview["damage"] = damage;

        int? hits = PickDynamicVar(values, "Repeat");
        if (hits != null && hits > 1)
        {
            preview["hits"] = hits;
            if (damage != null)
                preview["total_damage"] = damage * hits;
        }

        int? block = PickDynamicVar(values, "CalculatedBlock", "Block");
        if (block != null)
            preview["block"] = block;

        if (damage != null)
        {
            var cap = BuildDamageCap(enemy, damage.Value, hits ?? 1);
            if (cap != null)
                preview["damage_cap"] = cap;
        }

        if (values.Count > 0)
            preview["values"] = values;

        preview["description"] = SafeGetTargetedCardDescription(card, enemy);
        return preview;
    }

    private static RelicModel? GetRelicRewardModel(RelicReward reward)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        var type = reward.GetType();
        foreach (var property in type.GetProperties(Flags))
        {
            if (!typeof(RelicModel).IsAssignableFrom(property.PropertyType))
                continue;

            // A member that throws is skipped; only a successful read answers.
            RelicModel? relic = null;
            if (TryDo("rewards.relic_reward.property",
                    () => relic = property.GetValue(reward) as RelicModel))
                return relic;
        }

        foreach (var field in type.GetFields(Flags))
        {
            if (!typeof(RelicModel).IsAssignableFrom(field.FieldType))
                continue;

            RelicModel? relic = null;
            if (TryDo("rewards.relic_reward.field",
                    () => relic = field.GetValue(reward) as RelicModel))
                return relic;
        }

        return null;
    }

    private static Dictionary<string, object?> BuildCardRewardState(NCardRewardSelectionScreen cardScreen)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        var parent = GetCardRewardParent(cardScreen);
        state["parent_reward_id"] = parent?.Id;
        state["selection_state"] = parent?.SelectionState ?? "unknown";

        var cardHolders = FindAllSortedByPosition<NCardHolder>(cardScreen)
            .Where(holder => holder.CardModel != null && holder.Visible && holder.IsVisibleInTree())
            .ToList();
        var cards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index;
            // The upgraded form is part of what a reward card is worth, so it belongs in
            // the choice rather than being discovered later at a smith.
            AttachUpgradePreview(cardInfo, card);
            AddCardHolderState(cardInfo, holder);
            cards.Add(cardInfo);
            index++;
        }
        state["cards"] = cards;

        // Alternatives are the row of buttons beside the cards. "Skip" is only one of
        // them: relics add their own (Pael's Wing adds SACRIFICE, a reroll relic adds
        // REROLL), and skipping is NOT the same as taking one of those — Pael's Wing
        // counts sacrifices, and skip_card_reward does not count. State used to report
        // a single can_skip boolean, so the other options were invisible from the API
        // and the relic's whole function was unreachable.
        var alternatives = BuildCardRewardAlternatives(cardScreen);
        state["alternatives"] = alternatives;
        state["can_skip"] = alternatives.Any(a =>
            string.Equals(a.GetValueOrDefault("option_id")?.ToString(), "Skip", StringComparison.OrdinalIgnoreCase));

        return state;
    }

    /// <summary>
    /// The card reward's alternative options, in the order their buttons appear.
    /// Ids come from the screen's own CardRewardAlternative list where readable, so an
    /// option added by a relic this code has never heard of still shows up.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildCardRewardAlternatives(NCardRewardSelectionScreen cardScreen)
    {
        var result = new List<Dictionary<string, object?>>();
        var buttons = FindAll<NCardRewardAlternativeButton>(cardScreen);

        var optionIds = new List<string>();
        TryDo("card_reward.alternatives.option_id", () =>
        {
            if (GetInstanceFieldValue(cardScreen, "_extraOptions") is System.Collections.IEnumerable options)
            {
                foreach (var option in options)
                {
                    var id = GetPropertyValue(option, "OptionId")?.ToString();
                    optionIds.Add(id ?? "");
                }
            }
        });

        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            string? label = SafeGetText(() => GetInstanceFieldValue(button, "_optionName"));
            string optionId = i < optionIds.Count && !string.IsNullOrWhiteSpace(optionIds[i])
                ? optionIds[i]
                : label ?? $"option_{i}";

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = i,
                ["option_id"] = optionId,
                ["title"] = label,
                ["enabled"] = button.IsEnabled
            });
        }

        return result;
    }

    private static Dictionary<string, object?> BuildCardSelectState(NCardGridSelectionScreen screen)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Screen type
        state["screen_type"] = screen switch
        {
            NDeckTransformSelectScreen => "transform",
            NDeckUpgradeSelectScreen => "upgrade",
            NDeckEnchantSelectScreen => "enchant",
            NDeckCardSelectScreen => "select",
            NSimpleCardSelectScreen => "simple_select",
            _ => screen.GetType().Name
        };

        // Player summary
        // Prompt text from UI label
        var bottomLabel = screen.GetNodeOrNull("%BottomLabel");
        if (bottomLabel != null)
        {
            var textVariant = bottomLabel.Get("text");
            string? prompt = textVariant.VariantType != Godot.Variant.Type.Nil ? StripRichTextTags(textVariant.AsString()) : null;
            state["prompt"] = prompt;
        }

        // Which cards the screen has selected right now. Read from the screen's own
        // `_selectedCards` set - the one OnCardClicked toggles - so `selected` cannot drift
        // from what the game will act on. Without it a caller driving a multi-card screen
        // (the enchant screens) is blind to its own selection (A-7).
        var selection = GetCardSelectSelection(screen);
        if (selection == null)
            Warn("card_select.selected", "screen has no readable _selectedCards field");

        // Cards in the grid (sorted by visual position - MoveToFront can reorder children)
        var cardHolders = FindAllSortedByPosition<NGridCardHolder>(screen);
        // Which cards the screen already holds selected. `select_card` TOGGLES a
        // holder, so a client that cannot see this cannot converge on a
        // multi-pick screen: re-sending a pick turns it back off. Every grid
        // selection screen keeps its own private set; nothing public exposes it,
        // which is why the game's own AutoSlay handler tracks a local list
        // instead. Read it so a client can see its own progress.
        var selectedCards = SelectedCardModels(screen);
        state["selection_observable"] = selectedCards != null;
        var selectedIndices = new List<int>();
        var cards = new List<Dictionary<string, object?>>();
        // Fork addition: the selected cards as card payloads, not just indices.
        var selectedCardEntries = new List<Dictionary<string, object?>>();
        int index = 0;
        int selectedInGrid = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index;
            if (selection != null)
            {
                bool isSelected = selection.Contains(card);
                cardInfo["selected"] = isSelected;
                if (isSelected)
                {
                    selectedInGrid++;
                    selectedIndices.Add(index);
                }
            }
            // Only on the upgrade screen: the transform/remove screens do not upgrade
            // anything, and every card carrying a preview would bloat the payload.
            if (screen is NDeckUpgradeSelectScreen) AttachUpgradePreview(cardInfo, card);
            AddCardHolderState(cardInfo, holder);
            cards.Add(cardInfo);
            if (cardInfo["is_selected"] is true)
                selectedCardEntries.Add(new Dictionary<string, object?>(cardInfo, StringComparer.Ordinal));
            index++;
        }
        state["cards"] = cards;
        if (selectedCards != null)
        {
            state["selected_count"] = selectedCards.Count;
            state["selected_indices"] = selectedIndices;
        }

        // How many are selected and how many the screen wants. `selected_count` is the
        // screen's own count (it can exceed what the visible grid window shows);
        // `required_count` is the "select N" of the prompt, null when the screen accepts a
        // range ("up to N"), where min_select/max_select give the two ends.
        if (selection != null)
            state["selected_count"] = selection.Count;
        var prefs = GetCardSelectPrefs(screen);
        if (prefs.HasValue)
        {
            int min = prefs.Value.MinSelect;
            int max = prefs.Value.MaxSelect;
            state["min_select"] = min;
            state["max_select"] = max;
            state["required_count"] = min == max ? min : (int?)null;
        }
        else
        {
            state["required_count"] = null;
        }
        if (selection != null && selection.Count != selectedInGrid)
        {
            // Only possible when the grid is scrolled: its holders are a sliding window
            // over the deck, so a selected card can be outside `cards[]`.
            state["selected_outside_grid"] = selection.Count - selectedInGrid;
        }

        AddCardSelectionContext(state);
        // Fork addition: selected card payloads alongside main's indices/counts.
        state["selected_cards"] = selectedCardEntries;

        // Preview container showing? (selection complete, awaiting confirm)
        // The subclasses each name theirs differently; FindCardSelectPreviewContainers
        // knows all the spellings (upgrade, enchant and the generic one).
        var previewContainers = FindCardSelectPreviewContainers(screen);
        bool previewShowing = previewContainers.Count > 0;
        state["preview_showing"] = previewShowing;
        if (previewShowing)
        {
            var previewCards = new List<Dictionary<string, object?>>();
            foreach (var container in previewContainers)
                AddPreviewCardsFromContainer(container, previewCards);
            state["preview_cards"] = previewCards;
        }

        // Button states - when a preview is open, cancel goes through the
        // preview container's Cancel / PreviewCancel button (same path as
        // the action handler), not the top-level %Close button.
        bool canCancel = false;
        foreach (var container in previewContainers)
        {
            var cancelBtn = GetCardSelectPreviewCancel(container);
            if (IsControlVisibleOrActionable(cancelBtn)) { canCancel = true; break; }
        }
        if (!canCancel)
        {
            var closeButton = screen.GetNodeOrNull<NBackButton>("%Close");
            canCancel = IsControlVisibleOrActionable(closeButton);
        }
        state["can_cancel"] = canCancel;

        // Confirm button - the preview's own confirm while one is open, otherwise the
        // screen's. Note that below a grid, "confirm" usually opens the preview rather
        // than applying the selection; confirm_selection reports which of the two it did.
        bool canConfirm = false;
        foreach (var container in previewContainers)
        {
            var confirm = GetCardSelectPreviewConfirm(container);
            if (IsControlVisibleOrActionable(confirm)) { canConfirm = true; break; }
        }
        if (!canConfirm && !previewShowing)
        {
            var mainConfirm = GetCardSelectMainConfirm(screen);
            if (IsControlVisibleOrActionable(mainConfirm)) canConfirm = true;
            // Fallback: search the screen tree for any enabled confirm button, for a
            // subclass that names its confirm button something else.
            if (!canConfirm)
                canConfirm = FindAll<NConfirmButton>(screen).Any(IsControlVisibleOrActionable);
        }
        state["can_confirm"] = canConfirm;

        // Expose non-card controls explicitly so API clients do not mistake
        // the selected card for the only available next action.
        var availableActions = new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal)
            {
                ["action"] = "select_card",
                ["parameter"] = "index",
                ["description"] = previewShowing
                    ? "Compatibility fallback: selecting again confirms the open preview. Prefer confirm_selection."
                    : "Select a card from the cards list."
            }
        };
        if (canConfirm)
        {
            availableActions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = "confirm_selection",
                ["description"] = "Confirm the selected card(s) and leave this selection step."
            });
        }
        if (canCancel)
        {
            availableActions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = "cancel_selection",
                ["description"] = previewShowing
                    ? "Return to the card grid without confirming."
                    : "Close or cancel the card selection screen."
            });
        }
        state["available_actions"] = availableActions;

        return state;
    }

    /// <summary>
    /// The cards a grid selection screen currently holds selected, or null when
    /// the screen keeps no such set.
    /// </summary>
    /// <remarks>
    /// Every `NCardGridSelectionScreen` subclass that allows more than one pick
    /// keeps its own `private readonly HashSet&lt;CardModel&gt; _selectedCards`
    /// (see `NDeckEnchantSelectScreen`), and nothing public exposes it — the
    /// game's own AutoSlay handlers track a local list rather than read it. A
    /// client needs it because `select_card` TOGGLES a holder: without knowing
    /// what is already selected it cannot converge on a screen that wants three
    /// picks, since re-sending one turns it back off.
    ///
    /// The field name is matched rather than the declaring type, so a new screen
    /// with the same shape is covered without another entry here. A screen that
    /// has no such field answers null, which is reported as no selection rather
    /// than as an empty one.
    /// </remarks>
    private static HashSet<CardModel>? SelectedCardModels(object screen)
    {
        return TryRead(() =>
        {
            var field = screen.GetType().GetField(
                "_selectedCards",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(screen) as HashSet<CardModel>;
        });
    }

    private static Dictionary<string, object?> BuildChooseCardState(NChooseACardSelectionScreen screen)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        state["screen_type"] = "choose";

        state["prompt"] = "Choose a card.";

        var cardHolders = FindAllSortedByPosition<NGridCardHolder>(screen);
        var cards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index;
            AddCardHolderState(cardInfo, holder);
            cards.Add(cardInfo);
            index++;
        }
        state["cards"] = cards;
        AddCardSelectionContext(state);

        var skipButton = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        state["can_skip"] = IsControlVisibleOrActionable(skipButton);
        state["preview_showing"] = false;
        state["can_confirm"] = false;
        state["can_cancel"] = state["can_skip"];
        state["can_select"] = ChooseCardScreenAcceptsInput(screen);

        return state;
    }

    /// <summary>
    /// Whether `NChooseACardSelectionScreen.SelectHolder` would act on a press
    /// right now, rather than swallow it.
    /// </summary>
    /// <remarks>
    /// The screen opens with a 350 ms deadzone — `SelectHolder` returns without
    /// touching its `TaskCompletionSource` unless
    /// `Time.GetTicksMsec() - _openedTicks > 350` — so that a click still in
    /// flight when the overlay animates in cannot pick a card the player never
    /// saw. Nothing about the press fails: the signal fires, the handler runs,
    /// and it simply does nothing.
    ///
    /// That is invisible to a client, and silently fatal to one: `select_card`
    /// reports `ok` either way, so an engine that keys the observation it acted
    /// on then waits forever for a state that will never change. A bot polling
    /// ten times a second answers the FIRST observation of the screen by
    /// construction, which is always inside the deadzone — so every
    /// choose-a-card screen (Knowledge Demon's Curse of Knowledge, Attack
    /// Potion, Discovery, Toolbox) wedged the run rather than occasionally
    /// missing.
    ///
    /// Reporting readiness rather than refusing the action is what makes this
    /// race-free: the flag only ever goes from false to true, so a state read
    /// just before the deadline and acted on just after it still lands.
    /// </remarks>
    private static bool ChooseCardScreenAcceptsInput(NChooseACardSelectionScreen screen)
    {
        return TryRead<bool?>(() =>
        {
            if (GetInstanceFieldValue(screen, "_openedTicks") is not ulong openedTicks)
                return true;
            return Godot.Time.GetTicksMsec() - openedTicks > ChooseCardScreenInputDeadzoneMsec;
        }) ?? true;
    }

    /// <summary>`NChooseACardSelectionScreen._noSelectionTimeMsec`.</summary>
    private const ulong ChooseCardScreenInputDeadzoneMsec = 350uL;

    private static Dictionary<string, object?> BuildBundleSelectState(NChooseABundleSelectionScreen screen)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        state["screen_type"] = "bundle";

        state["prompt"] = "Choose a bundle.";

        state["bundles"] = BuildBundleEntries(screen);
        AddCardSelectionContext(state);

        var previewContainer = screen.GetNodeOrNull<Godot.Control>("%BundlePreviewContainer");
        state["preview_showing"] = previewContainer?.Visible == true;
        state["preview_cards"] = BuildBundlePreviewCards(screen);

        var cancelButton = screen.GetNodeOrNull<NBackButton>("%Cancel");
        var confirmButton = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        state["can_cancel"] = IsControlVisibleOrActionable(cancelButton);
        state["can_confirm"] = IsControlVisibleOrActionable(confirmButton);
        var chooseActions = new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal)
            {
                ["action"] = "select_card",
                ["parameter"] = "index",
                ["description"] = "Choose a card from the cards list."
            }
        };
        if (state["can_cancel"] is true)
        {
            chooseActions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = "cancel_selection",
                ["description"] = "Skip or cancel this card choice."
            });
        }
        state["available_actions"] = chooseActions;

        return state;
    }

    private static List<Dictionary<string, object?>> BuildBundleEntries(NChooseABundleSelectionScreen screen)
    {
        var bundles = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var bundle in FindAll<NCardBundle>(screen)
                     .Where(bundle => bundle.Visible && bundle.IsVisibleInTree()))
        {
            var cards = new List<Dictionary<string, object?>>();
            int cardIndex = 0;
            foreach (var card in bundle.Bundle)
            {
                var cardInfo = BuildCardInfo(card);
                cardInfo["index"] = cardIndex;
                cards.Add(cardInfo);
                cardIndex++;
            }

            bundles.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = index,
                ["card_count"] = cards.Count,
                ["cards"] = cards,
                ["is_visible"] = true,
                ["can_select"] = bundle.Hitbox.IsEnabled && IsNodeVisible(bundle.Hitbox)
            });
            index++;
        }
        return bundles;
    }

    private static List<Dictionary<string, object?>> BuildBundlePreviewCards(
        NChooseABundleSelectionScreen screen)
    {
        var previewCards = new List<Dictionary<string, object?>>();
        var previewCardsContainer = screen.GetNodeOrNull<Godot.Control>("%Cards");
        if (previewCardsContainer == null)
            return previewCards;

        int previewIndex = 0;
        foreach (var card in FindAll<NPreviewCardHolder>(previewCardsContainer)
                     .Where(holder => IsNodeVisible(holder))
                     .Select(holder => holder.CardModel)
                     .OfType<CardModel>())
        {
            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = previewIndex;
            previewCards.Add(cardInfo);
            previewIndex++;
        }
        return previewCards;
    }

    private static Dictionary<string, object?> BuildHandSelectState(NPlayerHand hand)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Mode
        state["mode"] = hand.CurrentMode switch
        {
            NPlayerHand.Mode.SimpleSelect => "simple_select",
            NPlayerHand.Mode.UpgradeSelect => "upgrade_select",
            _ => hand.CurrentMode.ToString()
        };

        // Prompt text from %SelectionHeader
        var headerLabel = hand.GetNodeOrNull<Godot.Control>("%SelectionHeader");
        if (headerLabel != null)
        {
            var textVariant = headerLabel.Get("text");
            string? prompt = textVariant.VariantType != Godot.Variant.Type.Nil
                ? StripRichTextTags(textVariant.AsString())
                : null;
            state["prompt"] = prompt;
        }

        // Selectable cards (visible holders in the hand)
        var selectableCards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in hand.ActiveHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index;
            cardInfo["description"] = SafeGetCardDescription(card); // hand cards use default pile
            // Only when the prompt is "upgrade a card in hand"; the plain select modes
            // (discard, exhaust, ...) have nothing to decide from the upgraded text.
            if (hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect) AttachUpgradePreview(cardInfo, card);
            AddCardHolderState(cardInfo, holder);
            selectableCards.Add(cardInfo);
            index++;
        }
        state["cards"] = selectableCards;
        AddCardSelectionContext(state);

        // Already-selected cards. NPlayerHand has two separate selection code paths --
        // SelectCardInSimpleMode() (used by "simple_select": discard/exhaust-one prompts)
        // populates the %SelectedHandCardContainer UI node by physically moving the card
        // there, while SelectCardInUpgradeMode() (used by "upgrade_select": Armaments etc.)
        // instead shows an in-place NUpgradePreview and never touches that container -- so
        // reading only the container silently reports zero selected cards for every
        // upgrade_select prompt, even after a real selection succeeds. The private
        // `_selectedCards` field (List<CardModel>) is what both code paths actually update,
        // so read that directly via reflection instead of the UI-only container.
        if (GetInstanceFieldValue(hand, "_selectedCards") is List<CardModel> selectedCardModels
            && selectedCardModels.Count > 0)
        {
            var selectedCards = new List<Dictionary<string, object?>>();
            int selIdx = 0;
            foreach (var card in selectedCardModels)
            {
                if (card == null) continue;
                int handIndex = -1;
                var handCards = card.Owner.PlayerCombatState?.Hand.Cards;
                if (handCards != null)
                {
                    for (int position = 0; position < handCards.Count; position++)
                    {
                        if (ReferenceEquals(handCards[position], card))
                        {
                            handIndex = position;
                            break;
                        }
                    }
                }
                if (handIndex < 0)
                    throw new InvalidOperationException("Selected Hand card is absent from its combat Hand pile.");
                var selectedInfo = BuildCardState(card, handIndex);
                // index addresses the selected UI container; hand_index names
                // the physical card in player.hand, including identical copies.
                selectedInfo["index"] = selIdx;
                selectedInfo["hand_index"] = handIndex;
                selectedCards.Add(selectedInfo);
                selIdx++;
            }
            if (selectedCards.Count > 0)
                state["selected_cards"] = selectedCards;
        }
        else
        {
            // Fallback to the old UI-container approach in case a future mode uses yet
            // another representation this doesn't anticipate, or _selectedCards gets
            // renamed in a game patch.
            var selectedContainer = hand.GetNodeOrNull<Godot.Control>("%SelectedHandCardContainer");
            if (selectedContainer != null)
            {
                var selectedCards = new List<Dictionary<string, object?>>();
                var selectedHolders = FindAll<NSelectedHandCardHolder>(selectedContainer);
                int selIdx = 0;
                foreach (var card in selectedHolders.Select(holder => holder.CardModel).OfType<CardModel>())
                {
                    selectedCards.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["index"] = selIdx,
                        ["name"] = SafeGetText(() => card.Title)
                    });
                    selIdx++;
                }
                if (selectedCards.Count > 0)
                    state["selected_cards"] = selectedCards;
            }
        }

        // Confirm button state
        var confirmBtn = hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton");
        state["can_confirm"] = IsControlVisibleOrActionable(confirmBtn);

        return state;
    }

    private static Dictionary<string, object?> BuildRelicSelectState(NChooseARelicSelection screen)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);

        state["prompt"] = "Choose a relic.";

        var relicHolders = FindAll<NRelicBasicHolder>(screen)
            .Where(holder => holder.Relic?.Model != null && holder.IsEnabled && holder.Visible && holder.IsVisibleInTree())
            .ToList();
        var relics = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in relicHolders)
        {
            var relic = holder.Relic?.Model;
            if (relic == null) continue;

            relics.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = index,
                ["id"] = relic.Id.Entry,
                ["name"] = SafeGetText(() => relic.Title),
                ["description"] = SafeGetText(() => relic.DynamicDescription),
                ["rarity"] = relic.Rarity.ToString(),
                ["is_visible"] = true,
                ["can_select"] = holder.IsEnabled,
                ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
            });
            index++;
        }
        state["relics"] = relics;

        var skipButton = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        state["can_skip"] = skipButton?.IsEnabled == true && skipButton.Visible && skipButton.IsVisibleInTree();

        return state;
    }

    private static Dictionary<string, object?> BuildCrystalSphereState(NCrystalSphereScreen screen)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);

        var instructionsTitle = screen.GetNodeOrNull<Godot.Control>("%InstructionsTitle");
        if (instructionsTitle != null)
        {
            var textVariant = instructionsTitle.Get("text");
            if (textVariant.VariantType != Godot.Variant.Type.Nil)
                state["instructions_title"] = StripRichTextTags(textVariant.AsString());
        }

        var instructionsDescription = screen.GetNodeOrNull<Godot.Control>("%InstructionsDescription");
        if (instructionsDescription != null)
        {
            var textVariant = instructionsDescription.Get("text");
            if (textVariant.VariantType != Godot.Variant.Type.Nil)
                state["instructions_description"] = StripRichTextTags(textVariant.AsString());
        }

        var cells = FindAll<NCrystalSphereCell>(screen);
        state["grid_width"] = cells.Count > 0 ? cells.Max(c => c.Entity.X) + 1 : 0;
        state["grid_height"] = cells.Count > 0 ? cells.Max(c => c.Entity.Y) + 1 : 0;

        var cellStates = new List<Dictionary<string, object?>>();
        var clickableCells = new List<Dictionary<string, object?>>();
        foreach (var cell in cells.OrderBy(c => c.Entity.Y).ThenBy(c => c.Entity.X))
        {
            var cellVisible = cell.Visible && cell.IsVisibleInTree();
            var cellState = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x"] = cell.Entity.X,
                ["y"] = cell.Entity.Y,
                ["is_hidden"] = cell.Entity.IsHidden,
                ["is_clickable"] = cell.Entity.IsHidden && cellVisible,
                ["is_highlighted"] = cell.Entity.IsHighlighted,
                ["is_hovered"] = cell.Entity.IsHovered
            };

            if (!cell.Entity.IsHidden && cell.Entity.Item != null)
            {
                cellState["item_type"] = cell.Entity.Item.GetType().Name;
                cellState["is_good"] = cell.Entity.Item.IsGood;
            }

            cellStates.Add(cellState);
            if (cell.Entity.IsHidden && cellVisible)
            {
                clickableCells.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x"] = cell.Entity.X,
                    ["y"] = cell.Entity.Y
                });
            }
        }
        state["cells"] = cellStates;
        state["clickable_cells"] = clickableCells;

        var revealedItems = new List<Dictionary<string, object?>>();
        foreach (var item in cells
                     .Where(c => !c.Entity.IsHidden && c.Entity.Item != null)
                     .Select(c => c.Entity.Item!)
                     .Distinct())
        {
            revealedItems.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["item_type"] = item.GetType().Name,
                ["x"] = item.Position.X,
                ["y"] = item.Position.Y,
                ["width"] = item.Size.X,
                ["height"] = item.Size.Y,
                ["is_good"] = item.IsGood
            });
        }
        state["revealed_items"] = revealedItems;

        var bigButton = screen.GetNodeOrNull<NClickableControl>("%BigDivinationButton");
        var smallButton = screen.GetNodeOrNull<NClickableControl>("%SmallDivinationButton");
        bool bigUsable = bigButton?.Visible == true && bigButton.IsVisibleInTree() && bigButton.IsEnabled;
        bool smallUsable = smallButton?.Visible == true && smallButton.IsVisibleInTree() && smallButton.IsEnabled;
        bool bigActive = bigButton?.GetNodeOrNull<Godot.Control>("%Outline")?.Visible == true;
        bool smallActive = smallButton?.GetNodeOrNull<Godot.Control>("%Outline")?.Visible == true;

        string tool = "none";
        if (bigActive)
            tool = "big";
        else if (smallActive)
            tool = "small";
        state["tool"] = tool;
        state["can_use_big_tool"] = bigUsable;
        state["can_use_small_tool"] = smallUsable;

        var divinationsLeft = screen.GetNodeOrNull<Godot.Control>("%DivinationsLeft");
        if (divinationsLeft != null)
        {
            var textVariant = divinationsLeft.Get("text");
            if (textVariant.VariantType != Godot.Variant.Type.Nil)
                state["divinations_left_text"] = StripRichTextTags(textVariant.AsString());
        }

        state["can_proceed"] = FindCrystalSphereProceedButton(screen) != null;

        var minigame = BuildCrystalSphereTrace(screen);
        if (minigame != null)
            state["minigame"] = minigame;

        return state;
    }

    // True while a treasure room is loaded and still has something to do: an
    // unopened chest, or a relic left to pick. Side-effect-free (unlike
    // BuildTreasureState, which auto-opens the chest) so it can safely gate the
    // state-type decision. Used to report "treasure" over a lingering-visible map
    // without deadlocking once the chest is opened and relics are taken.
    private static bool TreasureHasPendingAction()
    {
        var treasureUI = FindFirst<NTreasureRoom>(
            ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);
        if (treasureUI == null)
            return false;

        // Chest not yet opened.
        var chestButton = treasureUI.GetNodeOrNull<NClickableControl>("Chest");
        if (chestButton is { IsEnabled: true })
            return true;

        // A relic is still available to pick.
        var relicCollection = treasureUI.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
        if (relicCollection?.Visible == true)
        {
            return FindAll<NTreasureRoomRelicHolder>(relicCollection)
                .Any(h => h.IsEnabled && h.Visible);
        }

        return false;
    }

    private static Dictionary<string, object?> BuildTreasureState()
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);

        var treasureUI = FindFirst<NTreasureRoom>(
            ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);

        if (treasureUI == null)
        {
            state["message"] = "Treasure room loading...";
            return state;
        }

        // Auto-open chest if not yet opened
        var chestButton = treasureUI.GetNodeOrNull<NClickableControl>("Chest");
        if (!_passiveRead && chestButton is { IsEnabled: true })
        {
            chestButton.ForceClick();
            state["message"] = "Opening chest...";
            return state;
        }

        // Show relics available for picking
        var relicCollection = treasureUI.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
        if (relicCollection?.Visible == true)
        {
            var holders = FindAll<NTreasureRoomRelicHolder>(relicCollection)
                .Where(h => h.IsEnabled && h.Visible && h.IsVisibleInTree())
                .ToList();

            var relics = new List<Dictionary<string, object?>>();
            int index = 0;
            foreach (var holder in holders)
            {
                var relic = holder.Relic?.Model;
                if (relic == null) continue;
                relics.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["index"] = index,
                    ["id"] = relic.Id.Entry,
                    ["name"] = SafeGetText(() => relic.Title),
                    ["description"] = SafeGetText(() => relic.DynamicDescription),
                    ["rarity"] = relic.Rarity.ToString(),
                    ["is_visible"] = true,
                    ["can_claim"] = holder.IsEnabled,
                    ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
                });
                index++;
            }
            state["relics"] = relics;
        }

        state["can_proceed"] = IsControlVisibleOrActionable(treasureUI.ProceedButton);

        return state;
    }

    private static string GetRewardTypeName(Reward reward) => reward switch
    {
        GoldReward => "gold",
        PotionReward => "potion",
        RelicReward => "relic",
        CardReward => "card",
        SpecialCardReward => "special_card",
        CardRemovalReward => "card_removal",
        _ => reward.GetType().Name.ToLowerInvariant()
    };

    private static List<Dictionary<string, object?>> BuildPowersState(Creature creature)
    {
        var powers = new List<Dictionary<string, object?>>();
        foreach (var power in creature.Powers)
        {
            // The planner needs every power instance, including hidden tracker
            // powers. Identity, lifecycle state, and instance links are read
            // without touching localization so a broken hover tip cannot erase
            // mechanically relevant state.
            var isVisible = TryRead<bool?>("status.is_visible", () => power.IsVisible) ?? true;
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = power.Id.Entry,
                ["amount"] = power.Amount,
                ["display_amount"] = power.DisplayAmount,
                ["amount_on_turn_start"] = power.AmountOnTurnStart,
                ["skip_next_duration_tick"] = power.SkipNextDurationTick,
                ["type"] = power.TypeForCurrentAmount.ToString(),
                ["instance_type"] = power.InstanceType.ToString(),
                ["applier_combat_id"] = power.Applier?.CombatId,
                ["target_combat_id"] = power.Target?.CombatId,
                ["is_visible"] = isVisible,
                ["vars"] = power.DynamicVars.ToDictionary(
                    pair => pair.Key,
                    pair => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["base"] = pair.Value.BaseValue,
                        ["enchanted"] = pair.Value.EnchantedValue,
                        ["preview"] = pair.Value.PreviewValue,
                        ["rendered"] = pair.Value.ToString(CultureInfo.InvariantCulture)
                    }, StringComparer.Ordinal),
                ["runtime"] = BuildPowerRuntimeState(power)
            };
            powers.Add(entry);
            AppendPowerText(entry, power, isVisible);
        }
        return powers;
    }

    /// <summary>
    /// Localization and hover tips are advisory. The exact power instance is already in the
    /// entry, so a throw from game UI code mid-transition only costs the text.
    /// </summary>
    private static void AppendPowerText(
        Dictionary<string, object?> entry, PowerModel power, bool isVisible)
        => TryDo("status.description", () =>
        {
            var allTips = power.HoverTips.ToList();
            string? resolvedDesc = null;
            var extraTips = new List<IHoverTip>();
            foreach (var tip in allTips)
            {
                if (string.Equals(tip.Id, power.Id.ToString(), StringComparison.Ordinal))
                {
                    if (tip is HoverTip ht && ht.Description != null)
                        resolvedDesc = StripRichTextTags(ht.Description);
                }
                else
                {
                    extraTips.Add(tip);
                }
            }
            resolvedDesc ??= SafeGetText(() => power.SmartDescription);

            entry["name"] = SafeGetText(() => power.Title);
            entry["description"] = resolvedDesc;
            entry["keywords"] = BuildHoverTips(extraTips);
            if (!isVisible)
                entry["hidden_in_ui"] = true;
        });

    // Left silent on purpose: its callers use it to probe alternatives (PreviewValue,
    // then IntValue), so a throw here is an expected step, not a missing field. The
    // caller warns when every alternative fails.
    private static int? SafeGetInt(Func<int> getter)
    {
        return TryRead(() => getter());
    }

    private static Dictionary<string, object?> BuildPowerRuntimeState(PowerModel power)
    {
        var runtime = new Dictionary<string, object?>(StringComparer.Ordinal);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // Concrete and intermediate power classes contain state such as
        // counters, selected cards, and facing. Base PowerModel lifecycle state
        // is exported explicitly by BuildPowersState and is not duplicated.
        for (Type? type = power.GetType(); type != null && type != typeof(PowerModel); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || field.IsLiteral)
                    continue;
                runtime[field.Name] = SerializePowerRuntimeValue(field.GetValue(power), seen, 0);
            }
        }

        // PowerModel owns each subclass's InitInternalData result in a private
        // field, so it must be read from the base class explicitly.
        var internalDataField = typeof(PowerModel).GetField("_internalData", BindingFlags.Instance | BindingFlags.NonPublic);
        runtime["_internalData"] = SerializePowerRuntimeValue(internalDataField?.GetValue(power), seen, 0);
        return runtime;
    }

    private static object? SerializePowerRuntimeValue(object? value, HashSet<object> seen, int depth)
    {
        if (value == null)
            return null;
        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal)
            return value;
        if (value is Enum enumValue)
            return enumValue.ToString();
        if (value is Creature creature)
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "creature", ["combat_id"] = creature.CombatId };
        if (value is CardPlay cardPlay)
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "card_play",
                ["play_id"] = GetCardPlayIdentity(cardPlay),
                ["card"] = BuildCardInfo(cardPlay.Card),
                ["play_index"] = cardPlay.PlayIndex,
                ["play_count"] = cardPlay.PlayCount
            };
        if (value is CardModel card)
        {
            var cardState = BuildCardInfo(card);
            cardState["kind"] = "card";
            cardState["pile"] = card.Pile?.Type.ToString();
            cardState["play_index"] = card.CurrentPlayIndex;
            // A stolen combat face can have temporary upgrades/costs. Only the
            // independently saved DeckVersion is the eventual fixed reward.
            cardState["deck_version"] = card.DeckVersion == null ? null : BuildCardInfo(card.DeckVersion);
            return cardState;
        }
        if (value is AbstractModel model)
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "model", ["id"] = model.Id.Entry };
        if (depth >= 5 || !seen.Add(value))
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "reference", ["type"] = value.GetType().FullName };

        if (value is IDictionary dictionary)
            return SerializeDictionary(dictionary, seen, depth);
        if (value is IEnumerable enumerable)
            return SerializeEnumerable(enumerable, seen, depth);
        return SerializeObjectFields(value, seen, depth);
    }

    private static List<Dictionary<string, object?>> SerializeDictionary(
        IDictionary dictionary, HashSet<object> seen, int depth)
    {
        var entries = new List<Dictionary<string, object?>>();
        foreach (DictionaryEntry pair in dictionary)
        {
            entries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = SerializePowerRuntimeValue(pair.Key, seen, depth + 1),
                ["value"] = SerializePowerRuntimeValue(pair.Value, seen, depth + 1)
            });
        }
        return entries;
    }

    private static List<object?> SerializeEnumerable(
        IEnumerable enumerable, HashSet<object> seen, int depth)
    {
        var items = new List<object?>();
        foreach (var item in enumerable)
            items.Add(SerializePowerRuntimeValue(item, seen, depth + 1));
        return items;
    }

    private static Dictionary<string, object?> SerializeObjectFields(
        object value, HashSet<object> seen, int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "object",
            ["type"] = value.GetType().FullName
        };
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!field.IsStatic && !field.IsLiteral)
                result[field.Name] = SerializePowerRuntimeValue(field.GetValue(value), seen, depth + 1);
        }
        return result;
    }

    private static List<Dictionary<string, object?>> BuildPetsState(Player player)
    {
        var pets = new List<Dictionary<string, object?>>();
        var combatState = player.PlayerCombatState;
        if (combatState == null) return pets;

        // Check Osty specifically (Byrdpip/PaelsLegion are cosmetic with no real combat state)
        var osty = combatState.GetPet<Osty>();
        if (osty != null)
        {
            pets.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = osty.Monster?.Id.Entry ?? "OSTY",
                ["combat_id"] = osty.CombatId,
                ["name"] = SafeGetText(() => osty.Monster?.Title) ?? "Otsy",
                ["alive"] = osty.IsAlive,
                ["hp"] = osty.CurrentHp,
                ["max_hp"] = osty.MaxHp,
                ["block"] = osty.Block,
                ["status"] = BuildPowersState(osty)
            });
        }

        return pets;
    }
}
