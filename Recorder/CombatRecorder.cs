using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Mcp.Recorder;

/// <summary>
/// Keeps the game's own combat replay (CombatReplayWriter) as a durable, gap-detecting record.
///
/// The game already records everything a replay needs - the run as it was on entering the room,
/// every enqueued action, every choice result and resumption, a checksum of the full combat state
/// after each action - but only in memory, and writes it to replays/latest.mcr, overwriting the
/// previous combat. This class copies each entry out as the game appends it, serialized by the
/// game's own PacketWriter, and adds what the game's replay does not carry: which API request (if
/// any) an action came from, which action a choice belongs to, the round/turn it happened in, and
/// a public observation at each decision boundary.
///
/// Rules this class keeps (docs/recording.md):
///  - It never changes what the game does. Everything reads; nothing here writes game state,
///    awaits, or calls game logic beyond property getters and the game's serializers. In
///    particular it does not call Anonymized() while a combat runs: IdAnonymizer draws from
///    Rng.Chaotic the first time it sees an id. The one call it makes is at close, on the replay
///    the game is about to anonymize itself in the same synchronous call (same draws, same order).
///  - It never throws into the game. Every entry point goes through Guard, and a failure marks the
///    record faulted instead of being lost.
///  - Hidden information (RNG state, draw order, full-state checksums, the run save) goes to
///    hidden.jsonl / replay.mcr only - never to public.jsonl and never to the state API. Player
///    ids (platform ids) never go to public.jsonl either; public lines use the player's slot.
/// </summary>
internal static class CombatRecorder
{
    internal const string Schema = "sts2-mod-record/1";

    private static readonly object s_gate = new();
    private static readonly RecordWriter s_writer = new();
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();

    internal static readonly string s_sessionId = Guid.NewGuid().ToString("N");
    private static readonly DateTimeOffset s_sessionStarted = DateTimeOffset.UtcNow;

    // --- configuration / installation -------------------------------------------------------

    internal static bool ConfigEnabled { get; private set; } = true;
    internal static string Root { get; private set; } = DefaultRoot();

    /// <summary>"not_installed" / "installed" / "failed". Patches are only applied when asked to.</summary>
    internal static string InstallState { get; private set; } = "not_installed";
    internal static string? InstallError { get; private set; }

    /// <summary>Runtime switch. Off means every hook returns at its first line.</summary>
    private static volatile bool s_active;

    private static string? s_dllSha256;

    // --- API attribution ---------------------------------------------------------------------

    private static long s_apiSeq;
    /// <summary>The request whose handler is running on the main thread right now (0 = none).</summary>
    [ThreadStatic] private static long _handlerRequest;
    [ThreadStatic] private static Type? _expectType;
    /// <summary>The request between receipt and response (HTTP thread). A time window, not a cause.</summary>
    private static long s_windowRequest;
    private static readonly ConditionalWeakTable<GameAction, StrongBox<long>> s_apiActions = new();
    /// <summary>The first id each action got. Resuming after a choice gives it a new id.</summary>
    private static readonly ConditionalWeakTable<GameAction, StrongBox<uint>> s_actionKeys = new();

    // --- records -----------------------------------------------------------------------------

    private sealed class PendingInitial
    {
        internal required CombatReplay Replay;
        internal required byte[] HeaderPacket;
        internal required JsonObject Ids;
        internal required JsonObject Run;
        internal required long StartTime;
        internal required int Act;
        internal required int Floor;
        /// <summary>MOD requests between entering the room and the combat (an event that starts a fight).</summary>
        internal readonly JsonArray Requests = new();
    }

    private sealed class OpenRecord
    {
        internal required string Id;
        internal required string Dir;
        internal required CombatReplayWriter? GameWriter;
        internal required CombatReplay? Replay;
        internal long Seq;
        internal long OpenedMs;
        internal int GameEventsBeforeOpen;
        internal int ChecksumsBeforeOpen;
        internal int Backfilled;
        internal readonly List<string> EventHashes = new();
        internal readonly List<string> ChecksumKeys = new();
        internal readonly List<string> Faults = new();
        internal readonly HashSet<GameAction> JournaledActions = new(ReferenceEqualityComparer.Instance);
        /// <summary>The first API request this record lists (earlier ones happened before it opened).</summary>
        internal long FirstRequest;
        internal JsonObject? LossOutcome;
        internal readonly List<double> HookMicros = new();
        internal int SentryAtOpen;
        internal string PublicPath => Path.Combine(Dir, "public.jsonl");
        internal string HiddenPath => Path.Combine(Dir, "hidden.jsonl");
    }

    private static PendingInitial? s_pending;
    private static OpenRecord? s_rec;
    private static bool s_inCleanup;
    private static int s_faultsTotal;
    private static string? s_lastFault;
    private static int s_recordsOpened;
    private static int s_sentryCaptures;
    /// <summary>
    /// Directories handed out in this process. The writer thread creates a directory only when it
    /// writes the first line, so numbering from the disk alone could give a name out twice.
    /// </summary>
    private static readonly HashSet<string> s_reservedDirs = new(StringComparer.Ordinal);

    // --- the tracking layer ------------------------------------------------------------------
    //
    // Two layers. The tracking layer follows the game from the moment the recorder is installed,
    // whether a record is being written or not: which API request made which action, which choices
    // are reserved / begun / paused, which console commands ran. The output layer (a record, turned
    // on and off by set_recording) only writes. A record that opens late therefore still knows what
    // happened before it opened, and what the tracking layer itself did not see is said to be
    // unseen ("unknown", "reserved_before_tracking") instead of being taken as absent.

    /// <summary>What the tracking layer knows about the current room.</summary>
    private sealed class RoomTracker
    {
        /// <summary>The game replay whose room entry was seen. Null when tracking started inside a room.</summary>
        internal CombatReplay? Replay;
        /// <summary>Choices reserved and not yet answered, per player slot.</summary>
        internal readonly Dictionary<int, SortedSet<uint>> OpenChoices = new();
        /// <summary>(slot, choice id) -> the action that owns it (from SignalPlayerChoiceBegun).</summary>
        internal readonly Dictionary<(int, uint), JsonObject> ChoiceOwners = new();
        /// <summary>The action currently paused for a choice (single player: at most one).</summary>
        internal GameAction? Paused;
        /// <summary>Paused when tracking started: its reservation and begin were not seen.</summary>
        internal bool PausedUnseen;
        /// <summary>Per slot, the next choice id when tracking started inside a room: smaller ids were reserved unseen.</summary>
        internal readonly Dictionary<int, uint> ChoiceFloor = new();
    }

    private static RoomTracker s_track = new();

    /// <summary>
    /// "startup": installed by the mod's initializer (StartIfConfigured), before the game has run a
    /// single console command, API request or run - the tracking layer has seen the whole process.
    /// "runtime": installed later by set_recording, at the menu or mid-run. Whatever happened in the
    /// process before that (a console command at the menu, an action requested and held back by the
    /// game) was not seen, and the game can hand such things back later looking new - a held-back
    /// action comes through RequestEnqueue again at the next player turn. So in a "runtime"
    /// process the recorder guarantees neither provenance nor the absence of outside inputs: every
    /// untagged player-driven action is "unknown" and inputs_outside_replay is "unknown".
    /// Supported scope: complete records come only from a process that started with "record": true.
    /// </summary>
    private static string s_installedAt = "not_installed";

    /// <summary>Every action seen at ActionQueueSynchronizer.RequestEnqueue since install.</summary>
    private static readonly ConditionalWeakTable<GameAction, object> s_seenRequests = new();

    /// <summary>
    /// Console commands run in this process since install that bypassed the action queue. Their
    /// effects need not stay in one room nor in the save (GodModeConsoleCmd keeps static state and a
    /// CombatManager.CombatSetUp subscription that acts in every later combat), so every record
    /// opened after one in the same process carries the list and a fault. A new process starts clean.
    /// </summary>
    private static readonly JsonArray s_consoleInProcess = new();

    private static bool Installed => string.Equals(InstallState, "installed", StringComparison.Ordinal);

    // ========================================================================================
    // Configuration and install
    // ========================================================================================

    private static string DefaultRoot()
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "sts2", "records");
    }

    /// <summary>Reads "record" / "record_dir" from Sts2Mcp.conf. Missing keys keep the defaults.</summary>
    internal static void Configure(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return;
        if (config.TryGetProperty("record", out JsonElement rec) && rec.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ConfigEnabled = rec.GetBoolean();
        if (config.TryGetProperty("record_dir", out JsonElement dir) && dir.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(dir.GetString()))
        {
            string d = dir.GetString()!;
            if (d.StartsWith("~/", StringComparison.Ordinal))
                d = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), d[2..]);
            Root = d;
        }
    }

    /// <summary>
    /// Applies the recorder's patches and subscriptions, all or nothing. Called at startup when the
    /// config enables recording, or later from set_recording. With "record": false in the config
    /// nothing is patched at all - the game runs exactly as with a mod that has no recorder.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Install is all-or-nothing and must report the exception type to its caller instead of escaping the config-driven startup path.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "EPC12:Suspicious exception handling",
        Justification = "InstallError is a structured field on the status endpoint; it carries the type and the message by design.")]
    internal static bool Install(string by)
    {
        lock (s_gate)
        {
            if (string.Equals(InstallState, "installed", StringComparison.Ordinal))
                return true;
            s_installedAt = by;
            try
            {
                RecorderPatches.Apply();
                SubscribeCombatEvents();
                InstallState = "installed";
                InstallError = null;
                if (RunManager.Instance.IsInProgress)
                    StartTrackingInsideRoom();
                StartDllHash();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => OnProcessExit();
                return true;
            }
            catch (Exception ex)
            {
                RecorderPatches.RemoveAll();
                InstallState = "failed";
                InstallError = $"{ex.GetType().Name}: {ex.Message}";
                GD.PrintErr($"[STS2 MCP] Recorder not installed: {InstallError}");
                return false;
            }
        }
    }

    internal static void StartIfConfigured()
    {
        if (ConfigEnabled && Install("startup"))
            s_active = true;
    }

    private static void StartDllHash()
    {
        _ = Task.Run(() => TryRecordDo("dll_hash", () =>
        {
            using FileStream fs = File.OpenRead(typeof(RunManager).Assembly.Location);
            s_dllSha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }));
    }

    private static void SubscribeCombatEvents()
    {
        // CombatManager is a process-wide singleton (CombatManager.Instance { get; } = new()).
        CombatManager cm = CombatManager.Instance;
        cm.CombatSetUp += s => Guard("combat_setup", () => OnCombatSetUp(s));
        cm.CombatBegan += s => Guard("combat_began", () => OnSimple("combat_began"));
        cm.TurnStarted += s => Guard("turn_started", () => OnTurnStarted());
        cm.TurnEnded += s => Guard("turn_ended", () => OnSimple("turn_ended"));
        cm.PlayerEndedTurn += (p, canBackOut) => Guard("player_ended_turn", () => OnPlayerEndedTurn(canBackOut));
        cm.PlayerUnendedTurn += p => Guard("player_unended_turn", () => OnSimple("player_unended_turn"));
        cm.CombatWon += r => Guard("combat_won", () => OnSimple("combat_won"));
        cm.CombatEnded += r => Guard("combat_ended", () => OnCombatEnded());
    }

    // ========================================================================================
    // Runtime switch (set_recording) and status
    // ========================================================================================

    internal static Dictionary<string, object?> SetRecording(bool enabled)
    {
        if (enabled)
        {
            if (!Install("runtime"))
                return new(StringComparer.Ordinal) { ["status"] = "error", ["error"] = $"Recorder could not be installed: {InstallError}" };
            lock (s_gate)
            {
                if (!s_active)
                {
                    s_active = true;
                    // A combat already under way is recorded from here on, marked as started late.
                    // (Not after it: a finished combat's state lingers until the next room, and
                    // its replay has already been written and stopped.)
                    if (CombatManager.Instance.IsInProgress || CombatManager.Instance.IsStarting)
                        Guard("open_on_enable", () => TryOpen("recording_enabled"));
                }
            }
        }
        else
        {
            lock (s_gate)
            {
                if (s_rec != null)
                    CloseRecord("recording_disabled", null);
                s_active = false;
                s_pending = null;
            }
        }
        return new(StringComparer.Ordinal) { ["status"] = "ok", ["message"] = enabled ? "Recording on" : "Recording off", ["recording"] = Status() };
    }

    internal static Dictionary<string, object?> Status()
    {
        lock (s_gate)
        {
            return new(StringComparer.Ordinal)
            {
                ["schema"] = Schema,
                ["config_enabled"] = ConfigEnabled,
                ["active"] = s_active,
                ["install"] = InstallState,
                ["install_error"] = InstallError,
                ["installed_at"] = s_installedAt,
                ["patches"] = RecorderPatches.Applied,
                ["root"] = Root,
                ["session"] = s_sessionId,
                ["records_opened"] = s_recordsOpened,
                ["current"] = s_rec == null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = s_rec.Id,
                    ["dir"] = s_rec.Dir,
                    ["seq"] = s_rec.Seq,
                    ["faults"] = s_rec.Faults.Count,
                },
                ["pending_initial_state"] = s_pending != null,
                ["faults_total"] = s_faultsTotal,
                ["last_fault"] = s_lastFault,
                ["sentry_captures"] = s_sentryCaptures,
                ["writer_failures"] = s_writer.Failures,
                ["writer_last_error"] = s_writer.LastError,
                ["writer_pending"] = s_writer.Pending,
                ["sts2_dll_sha256"] = s_dllSha256,
            };
        }
    }

    // ========================================================================================
    // API attribution
    // ========================================================================================

    /// <summary>Opens the request window. Returns the request id (0 when the recorder is not installed).</summary>
    internal static long ApiBegin(string action, Dictionary<string, JsonElement> parsed)
    {
        if (!Installed)
            return 0;
        long id = Interlocked.Increment(ref s_apiSeq);
        Interlocked.Exchange(ref s_windowRequest, id);
        var args = new JsonObject();
        foreach ((string? k, JsonElement v) in parsed)
        {
            if (string.Equals(k, "action", StringComparison.Ordinal))
                continue;
            // A payload the JSON reader rejects is kept as its raw text rather than dropped.
            string raw = v.GetRawText();
            JsonNode? value = null;
            args[k] = TryRecordDo("api_request.args", () => value = JsonNode.Parse(raw))
                ? value
                : JsonValue.Create(raw);
        }
        var line = new JsonObject { ["request"] = id, ["action"] = action, ["args"] = args };
        lock (s_gate)
        {
            // Outside a record, kept with the room's pending state: an event that turns into a
            // fight needs the choices that led to it (P2-3 of the design review).
            if (s_rec == null)
                s_pending?.Requests.Add(line.DeepClone());
            else
                EmitNoGameRead("api_request", line);
        }
        return id;
    }

    internal static void ApiEnd(long id, string? status)
    {
        if (id == 0)
            return;
        Interlocked.CompareExchange(ref s_windowRequest, 0, id);
        lock (s_gate)
        {
            if (s_rec != null)
                EmitNoGameRead("api_response", new JsonObject { ["request"] = id, ["status"] = status });
        }
    }

    /// <summary>Wraps a handler that runs on the main thread, so the action it enqueues carries the request.</summary>
    internal static Func<T> InHandler<T>(long id, Func<T> body) => () =>
    {
        long outer = _handlerRequest;
        _handlerRequest = id;
        try { return body(); }
        finally { _handlerRequest = outer; }
    };

    /// <summary>
    /// Marks the GameAction an API handler is about to enqueue as coming from the running request.
    /// Tied to the object, so an action the game holds back (a card played during the enemy turn
    /// waits for the player turn) keeps its origin when it is finally enqueued.
    ///
    /// Only the action the handler itself creates is marked: whatever the game enqueues as a
    /// consequence while the handler is still on the stack (a continuation that runs inline) is
    /// the game's own and stays "game".
    /// </summary>
    internal static T TagApiAction<T>(T action) where T : GameAction
    {
        // Tracking layer: tagged whether or not a record is open (a record opened before the game
        // enqueues a held-back action still names the request).
        if (Installed && _handlerRequest != 0)
        {
            long request = _handlerRequest;
            Guard("tag_api_action", () => s_apiActions.AddOrUpdate(action, new StrongBox<long>(request)));
        }
        return action;
    }

    /// <summary>
    /// For handlers that ask the game to build the action (PotionModel.EnqueueManualUse builds the
    /// UsePotionAction itself): the next action of <paramref name="type"/> that reaches
    /// ActionQueueSynchronizer.RequestEnqueue during the returned scope is marked.
    /// </summary>
    internal static IDisposable ExpectApiAction(Type type)
    {
        Type? outer = _expectType;
        _expectType = type;
        return new Scope(() => _expectType = outer);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    /// <summary>Prefix of ActionQueueSynchronizer.RequestEnqueue.</summary>
    internal static void OnRequestEnqueue(GameAction action)
    {
        Guard("request_enqueue", () =>
        {
            s_seenRequests.AddOrUpdate(action, true);
            if (_handlerRequest == 0 || _expectType == null || action.GetType() != _expectType)
                return;
            _expectType = null;
            s_apiActions.AddOrUpdate(action, new StrongBox<long>(_handlerRequest));
        });
    }

    /// <summary>
    /// mod_api: made by a MOD handler (tagged on the object). not_mod_api: a player-driven action the
    /// tracking layer saw reach RequestEnqueue untagged (the UI, including a click by hand).
    /// unknown: a player-driven action it never saw requested - requested before the recorder was
    /// installed, or through some other door; never guessed to be either. game: hook actions and the
    /// game's own (ReadyToBeginEnemyTurnAction ...).
    /// </summary>
    private static JsonObject OriginOf(GameAction action)
    {
        if (s_apiActions.TryGetValue(action, out StrongBox<long>? box))
        {
            var o = new JsonObject { ["kind"] = "mod_api", ["request"] = box.Value };
            if (s_rec != null && box.Value < s_rec.FirstRequest)
                o["request_before_open"] = true;
            return o;
        }
        if (action is GenericHookGameAction || !ActionQueueSet.IsGameActionPlayerDriven(action))
            return new JsonObject { ["kind"] = "game" };
        if (!string.Equals(s_installedAt, "startup", StringComparison.Ordinal))
            return new JsonObject { ["kind"] = "unknown", ["why"] = "recorder installed at runtime: requests before the install were not seen" };
        if (s_seenRequests.TryGetValue(action, out _))
            return new JsonObject { ["kind"] = "not_mod_api" };
        return new JsonObject { ["kind"] = "unknown", ["why"] = "not seen at RequestEnqueue" };
    }

    private static JsonObject WindowOrigin()
    {
        long w = Interlocked.Read(ref s_windowRequest);
        return w != 0
            ? new JsonObject { ["kind"] = "in_api_window", ["request"] = w }
            : new JsonObject { ["kind"] = "outside_api_window" };
    }

    // ========================================================================================
    // Game hooks (called from RecorderPatches and the CombatManager events)
    // ========================================================================================

    /// <summary>Postfix of CombatReplayWriter.RecordInitialState: the run as it was on entering the room.</summary>
    internal static void OnInitialState(CombatReplayWriter writer, SerializableRun serializableRun)
    {
        if (!s_active)
            return;
        Guard("initial_state", () =>
        {
            lock (s_gate)
            {
                s_inCleanup = false;
                // A new room while a record is still open: the old one never saw its end.
                if (s_rec != null)
                    CloseRecord("superseded_by_room_entry", null);

                CombatReplay? replay = RecorderPatches.ReplayOf(writer);
                if (replay == null)
                {
                    s_pending = null;
                    return;
                }
                RunState? run = RunManager.Instance.DebugOnlyGetState();
                s_pending = new PendingInitial
                {
                    Replay = replay,
                    HeaderPacket = Packet(HeaderOf(replay)),
                    Ids = IdsOf(replay),
                    Run = RunInfo(run, serializableRun),
                    StartTime = serializableRun.StartTime,
                    Act = run?.CurrentActIndex ?? serializableRun.CurrentActIndex,
                    Floor = run?.ActFloor ?? -1,
                };
            }
        });
    }

    private static void OnCombatSetUp(CombatState state)
    {
        if (!s_active)
            return;
        lock (s_gate)
        {
            TryOpen("combat_setup");
            if (s_rec == null)
                return;
            JsonObject data = RoomInfo(state);
            data["combat_local_id"] = CombatManager.Instance.CurrentCombatId?.Value;
            EmitWithObservation("combat_setup", data, hidden: true);
        }
    }

    private static void OnSimple(string kind)
    {
        if (!s_active)
            return;
        lock (s_gate)
        {
            if (s_rec != null)
                Emit(kind, null);
        }
    }

    /// <summary>A turn start is a decision boundary: public and hidden observation.</summary>
    private static void OnTurnStarted()
    {
        if (!s_active)
            return;
        lock (s_gate)
        {
            if (s_rec != null)
                EmitWithObservation("turn_started", null, hidden: true);
        }
    }

    /// <summary>
    /// The end-turn button ends the turn from inside an EndPlayerTurnAction, and card effects that
    /// end it (VOID_FORM) do so from inside their card's action - both are in the game's replay.
    /// A turn ended with no action running bypassed the queue, so no replay can reproduce it.
    /// </summary>
    private static void OnPlayerEndedTurn(bool canBackOut)
    {
        if (!s_active)
            return;
        lock (s_gate)
        {
            if (s_rec == null)
                return;
            GameAction? running = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
            if (running == null && CombatManager.Instance.IsInProgress)
                s_rec.Faults.Add("end_turn_outside_action_queue");
            Emit("player_ended_turn", new JsonObject
            {
                ["can_back_out"] = canBackOut,
                ["running_action"] = running == null ? null : RecordObserve.DescribeAction(running, KeyOf(running)),
            });
        }
    }

    /// <summary>
    /// CombatEnded fires after the replay is written when the combat is won (EndCombatInternal),
    /// and with nothing written when it is lost (ProcessPendingLoss) - the replay then waits for
    /// RunManager.CleanUp, by which time CombatManager.Reset has dropped the combat. So a loss
    /// takes its observation and outcome here and closes at CleanUp.
    /// </summary>
    private static void OnCombatEnded()
    {
        if (!s_active)
            return;
        lock (s_gate)
        {
            if (s_rec == null)
                return;
            s_rec.LossOutcome = Outcome();
            EmitWithObservation("combat_lost", new JsonObject { ["outcome"] = s_rec.LossOutcome?.DeepClone() }, hidden: true);
        }
    }

    /// <summary>Postfix of the writer's RecordGameAction / RecordActionResume / RecordPlayerChoice.</summary>
    internal static void OnGameEvent(CombatReplayWriter writer, int countBefore, GameAction? action)
    {
        Guard("game_event", () =>
        {
            lock (s_gate)
            {
                CombatReplay? replay = RecorderPatches.ReplayOf(writer);
                // The writer returns early outside combat / when disabled: nothing was appended.
                if (replay == null || countBefore < 0 || replay.events.Count != countBefore + 1)
                    return;
                RecordGameEvent(replay, action);
            }
        });
    }

    private static void RecordGameEvent(CombatReplay replay, GameAction? action)
    {
        CombatReplayEvent ev = replay.events[^1];
        // Tracking layer: a choice result settles its choice, recording or not.
        JsonObject? parent = ev.eventType == CombatReplayEventType.PlayerChoice
            ? SettleChoice(SlotOf(ev.playerId), ev.choiceId) : null;
        if (!s_active)
            return;
        if (s_rec == null)
            TryOpen("game_event");
        if (s_rec == null || !ReferenceEquals(s_rec.Replay, replay))
            return;
        JournalEvent(replay, replay.events.Count - 1, action, backfilled: false, parent);
    }

    private static void JournalEvent(CombatReplay replay, int index, GameAction? action, bool backfilled,
        JsonObject? choiceParent = null)
    {
        OpenRecord rec = s_rec!;
        CombatReplayEvent ev = replay.events[index];
        byte[] packet = Packet(ev);
        rec.EventHashes.Add(Sha(packet));

        var data = new JsonObject
        {
            ["index"] = index,
            ["type"] = ev.eventType.ToString(),
            ["player_slot"] = SlotOf(ev.playerId),
        };
        if (backfilled)
            data["backfilled"] = true;
        AppendEventDetails(rec, data, ev, action, choiceParent);

        long seq = Emit("game_event", data, hidden: true, noAt: backfilled);
        EmitHidden(seq, "game_event", new JsonObject
        {
            ["index"] = index,
            ["packet"] = Convert.ToBase64String(packet),
        });
    }

    /// <summary>Adds the fields that depend on which kind of replay event this is.</summary>
    private static void AppendEventDetails(
        OpenRecord rec,
        JsonObject data,
        CombatReplayEvent ev,
        GameAction? action,
        JsonObject? choiceParent)
    {
        switch (ev.eventType)
        {
            case CombatReplayEventType.GameAction:
                AppendGameActionDetails(rec, data, ev, action);
                break;
            case CombatReplayEventType.HookAction:
                AppendHookActionDetails(rec, data, ev, action);
                break;
            case CombatReplayEventType.ResumeAction:
                data["resumed_action_id"] = ev.actionId;
                break;
            case CombatReplayEventType.PlayerChoice:
                AppendChoiceDetails(rec, data, ev, choiceParent);
                break;
        }
    }

    private static void AppendGameActionDetails(
        OpenRecord rec, JsonObject data, CombatReplayEvent ev, GameAction? action)
    {
        if (action != null)
        {
            uint? key = action.Id;
            foreach ((string? k, JsonNode? v) in RecordObserve.DescribeAction(action, key))
                data[k] = v?.DeepClone();
            data["origin"] = OriginOf(action);
            data["cause_action_id"] = RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id;
            TrackJournaledAction(rec, action);
        }
        data["action"] = RecordObserve.DescribeNetAction(ev.action, SlotOf);
    }

    private static void AppendHookActionDetails(
        OpenRecord rec, JsonObject data, CombatReplayEvent ev, GameAction? action)
    {
        data["hook_id"] = ev.hookId;
        data["game_action_type"] = ev.gameActionType?.ToString();
        if (action == null)
            return;
        TrackJournaledAction(rec, action);
        data["action_id"] = action.Id;
        data["action_key"] = action.Id;
        data["cause_action_id"] = RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id;
    }

    private static void AppendChoiceDetails(
        OpenRecord rec, JsonObject data, CombatReplayEvent ev, JsonObject? choiceParent)
    {
        data["choice_id"] = ev.choiceId;
        data["result"] = RecordObserve.DescribeChoiceResult(ev.playerChoiceResult, SlotOf);
        data["origin"] = WindowOrigin();
        if (choiceParent == null)
            return;
        foreach ((string? k, JsonNode? v) in choiceParent.ToList())
        {
            choiceParent.Remove(k);
            data[k] = v;
        }
        if (data["patch_missed"]?.GetValue<bool>() == true)
            rec.Faults.Add($"patch_missed:reserve:{ev.choiceId}");
    }

    /// <summary>
    /// Remembers an action that appears in the replay, so a later cancel can be attributed to it.
    /// </summary>
    private static void TrackJournaledAction(OpenRecord rec, GameAction action)
    {
        uint? key = action.Id;
        if (key != null)
            s_actionKeys.AddOrUpdate(action, new StrongBox<uint>(key.Value));
        rec.JournaledActions.Add(action);
        // BeforeCancelled, not a patch on the small GameAction.Cancel (see RecorderPatches).
        action.BeforeCancelled += OnCancelled;
    }

    /// <summary>
    /// The owner of a choice, settled when its result arrives (the point where the choice id is
    /// final): the action SignalPlayerChoiceBegun named for it, and the action paused for a choice
    /// right now. In single player the queue is one and GetReadyAction skips a queue that is
    /// gathering a choice, so at most one action is paused. A choice with no pause at all came from
    /// a context that does not pause (BlockingPlayerChoiceContext / ThrowingPlayerChoiceContext).
    /// Tracking layer: runs for every result, recording or not.
    /// </summary>
    private static JsonObject SettleChoice(int? slot, uint? choiceId)
    {
        RoomTracker t = s_track;
        int s = slot ?? -1;
        var data = new JsonObject();
        if (choiceId is { } c)
        {
            bool beforeTracking = t.ChoiceFloor.TryGetValue(s, out uint floor) && c < floor;
            if (t.ChoiceOwners.Remove((s, c), out JsonObject? owner))
            {
                data["owner"] = owner;
                data["owner_state"] = "observed";
            }
            else
            {
                data["owner"] = null;
                data["owner_state"] = beforeTracking ? "reserved_before_tracking" : "unobserved";
            }
            bool reservedSeen = t.OpenChoices.TryGetValue(s, out SortedSet<uint>? open) && open.Remove(c);
            // A reservation the tracking layer should have seen and did not: the patch missed it.
            if (!reservedSeen && !beforeTracking)
                data["patch_missed"] = true;
        }
        // "paused" / "not_paused" are both observations: tracking reads the queue when it starts and
        // follows every pause and resume after that.
        data["pause_state"] = "paused";
        if (t.Paused == null)
            data["pause_state"] = "not_paused";
        else if (t.PausedUnseen)
            data["pause_state"] = "paused_since_before_tracking";
        data["paused_action"] = t.Paused == null ? null : RecordObserve.DescribeAction(t.Paused, KeyOf(t.Paused));
        data["pending_choice_ids"] = PendingChoices(s);
        return data;
    }

    /// <summary>Postfix of the writer's RecordChecksum.</summary>
    internal static void OnChecksum(CombatReplayWriter writer, int countBefore)
    {
        if (!s_active)
            return;
        Guard("checksum", () =>
        {
            lock (s_gate)
            {
                CombatReplay? replay = RecorderPatches.ReplayOf(writer);
                if (replay == null || countBefore < 0 || replay.checksumData.Count != countBefore + 1)
                    return;
                if (s_rec == null)
                    TryOpen("checksum");
                if (s_rec == null || !ReferenceEquals(s_rec.Replay, replay))
                    return;
                JournalChecksum(replay, replay.checksumData.Count - 1, backfilled: false);
            }
        });
    }

    /// <summary>
    /// A checksum line carries the id and context in public and the checksum value in hidden: the
    /// value is a hash of the full state, draw order included, so a searcher that could read it
    /// could test guesses about the hidden state against it. The full state itself is not copied
    /// per action (it would need Anonymized() mid-combat); it is in replay.mcr at close.
    /// </summary>
    private static void JournalChecksum(CombatReplay replay, int index, bool backfilled)
    {
        OpenRecord rec = s_rec!;
        ReplayChecksumData cs = replay.checksumData[index];
        rec.ChecksumKeys.Add(ChecksumKey(cs));
        var data = new JsonObject
        {
            ["index"] = index,
            ["checksum_id"] = cs.checksumData.id,
            ["context"] = cs.context,
            // The action running now is only the checksum's action when it is copied as it happens.
            ["action_id"] = backfilled ? null : RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id,
        };
        if (backfilled)
            data["backfilled"] = true;
        long seq = Emit("checksum", data, hidden: true, noAt: backfilled);
        EmitHidden(seq, "checksum", new JsonObject
        {
            ["index"] = index,
            ["checksum_id"] = cs.checksumData.id,
            ["checksum"] = cs.checksumData.checksum,
        });
    }

    /// <summary>
    /// Postfix of ActionQueueSet.EnqueueWithoutSynchronizing. The game's writer records every
    /// action enqueued while the combat is in progress; one it did not record (its handler threw,
    /// or another handler before it did) makes the replay unreproducible, so it is flagged.
    /// Before IsInProgress (combat setup) and after it (the combat is ending) the game does not
    /// record actions at all; a replay regenerates the setup ones from the initial state.
    /// </summary>
    internal static void OnEnqueued(GameAction action)
    {
        if (!s_active)
            return;
        Guard("enqueued", () =>
        {
            lock (s_gate)
            {
                if (s_rec == null || s_rec.JournaledActions.Contains(action))
                    return;
                JsonObject data = RecordObserve.DescribeAction(action, action.Id);
                data["origin"] = OriginOf(action);
                CombatManager cm = CombatManager.Instance;
                string phase = "outside";
                if (cm.IsInProgress)
                    phase = "in_progress";
                else if (cm.IsStarting)
                    phase = "starting";
                else if (cm.IsEnding)
                    phase = "ending";
                data["phase"] = phase;
                if (cm.IsInProgress && s_rec.GameWriter?.IsEnabled == true)
                {
                    s_rec.Faults.Add($"enqueue_unrecorded:{action.Id}");
                    Emit("enqueue_unrecorded", data);
                }
                else
                {
                    Emit("enqueue_outside_combat", data);
                }
            }
        });
    }

    internal static void OnChoiceReserved(Player player, uint choiceId)
    {
        Guard("choice_reserved", () =>
        {
            lock (s_gate)
            {
                int slot = PlayerSlot(player) ?? -1;
                if (!s_track.OpenChoices.TryGetValue(slot, out SortedSet<uint>? open))
                    s_track.OpenChoices[slot] = open = new SortedSet<uint>();
                open.Add(choiceId);
                if (!s_active || s_rec == null)
                    return;
                GameAction? running = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
                Emit("choice_reserved", new JsonObject
                {
                    ["choice_id"] = choiceId,
                    ["player_slot"] = slot,
                    ["running_action"] = running == null ? null : RecordObserve.DescribeAction(running, KeyOf(running)),
                });
            }
        });
    }

    /// <summary>
    /// Prefix of SignalPlayerChoiceBegun: the choice being begun is the one this player reserved
    /// last. Every caller of ReserveChoiceId with a context (CardSelectCmd, v0.111.0) calls
    /// SignalPlayerChoiceBegun on the next line with nothing awaited in between. Read in the
    /// prefix, before the hook context's synchronous part can run other code.
    /// </summary>
    internal static uint? OnChoiceBegunPrefix(Player chooser)
    {
        if (!Installed)
            return null;
        lock (s_gate)
        {
            int slot = PlayerSlot(chooser) ?? -1;
            return s_track.OpenChoices.TryGetValue(slot, out SortedSet<uint>? open) && open.Count > 0 ? open.Max : null;
        }
    }

    /// <summary>
    /// Postfix of the concrete SignalPlayerChoiceBegun. The context names the action that owns the
    /// choice - for a hook's choice, the hook action it has just generated (set before the method's
    /// first await, so it is there when the async method returns to us).
    /// </summary>
    internal static void OnChoiceBegun(PlayerChoiceContext context, Player chooser, uint? choiceId)
    {
        Guard("choice_begun", () =>
        {
            lock (s_gate)
            {
                GameAction? owner = context switch
                {
                    GameActionPlayerChoiceContext g => g.Action,
                    HookPlayerChoiceContext h => h.GameAction,
                    _ => null,
                };
                int slot = PlayerSlot(chooser) ?? -1;
                if (choiceId is { } c)
                {
                    var ownerJson = new JsonObject { ["context"] = context.GetType().Name };
                    if (owner != null)
                        foreach ((string? k, JsonNode? v) in RecordObserve.DescribeAction(owner, KeyOf(owner)))
                            ownerJson[k] = v?.DeepClone();
                    s_track.ChoiceOwners[(slot, c)] = ownerJson;
                }
                if (!s_active || s_rec == null)
                    return;
                Emit("choice_begun", new JsonObject
                {
                    ["choice_id"] = choiceId,
                    ["context"] = context.GetType().Name,
                    ["player_slot"] = slot,
                    ["owner"] = owner == null ? null : RecordObserve.DescribeAction(owner, KeyOf(owner)),
                });
            }
        });
    }

    /// <summary>
    /// Postfix of ActionQueueSet.PauseActionForPlayerChoice - the pending-choice boundary. When a
    /// resumption was already waiting, the game resumes the action inside this very call and the
    /// id changes under us; both ids are written.
    /// </summary>
    internal static void OnPausedForChoice(GameAction action, PlayerChoiceOptions options, uint? idBefore)
    {
        Guard("choice_paused", () =>
        {
            lock (s_gate)
            {
                if (idBefore is { } first && !s_actionKeys.TryGetValue(action, out _))
                    s_actionKeys.AddOrUpdate(action, new StrongBox<uint>(first));
                if (action.State == GameActionState.GatheringPlayerChoice)
                {
                    s_track.Paused = action;
                    s_track.PausedUnseen = false;
                }
                if (!s_active || s_rec == null)
                    return;
                JsonObject data = RecordObserve.DescribeAction(action, KeyOf(action));
                data["action_id_before"] = idBefore;
                data["options"] = options.ToString();
                data["state_after"] = action.State.ToString();
                data["pending_choice_ids"] = PendingChoices(OwnerSlot(action));
                EmitWithObservation("choice_paused", data, hidden: true);
            }
        });
    }

    internal static void OnResumed(uint oldId, uint nextActionId)
    {
        Guard("action_resumed", () =>
        {
            lock (s_gate)
            {
                RoomTracker t = s_track;
                var data = new JsonObject
                {
                    ["old_action_id"] = oldId,
                    // ResumeActionWithoutSynchronizing hands out exactly one new id after the event.
                    ["new_action_id"] = nextActionId - 1,
                };
                // A choice the game settles without asking (e.g. HEADBUTT with one card in the
                // discard pile) is reserved and resumed with no PlayerChoice result in between.
                if (t.Paused != null && (t.Paused.Id == oldId || t.Paused.Id == nextActionId - 1))
                {
                    GameAction paused = t.Paused;
                    uint? key = KeyOf(paused);
                    uint? hook = (paused as GenericHookGameAction)?.HookId;
                    var unanswered = new JsonArray();
                    foreach (((int slot, uint choice), JsonObject? owner) in t.ChoiceOwners.ToList())
                    {
                        bool mine = (key != null && AsUInt(owner["action_key"]) == key)
                                    || (hook != null && AsUInt(owner["hook_id"]) == hook);
                        if (!mine)
                            continue;
                        t.ChoiceOwners.Remove((slot, choice));
                        if (t.OpenChoices.TryGetValue(slot, out SortedSet<uint>? open))
                            open.Remove(choice);
                        unanswered.Add(choice);
                    }
                    data["unanswered_choice_ids"] = unanswered;
                    if (t.PausedUnseen)
                        data["paused_since_before_tracking"] = true;
                    data["action_key"] = key;
                    data["class"] = paused.GetType().Name;
                    t.Paused = null;
                    t.PausedUnseen = false;
                }
                if (s_active && s_rec != null)
                    Emit("action_resumed", data);
            }
        });
    }

    private static void OnCancelled(GameAction action)
    {
        if (!s_active)
            return;
        Guard("action_cancelled", () =>
        {
            lock (s_gate)
            {
                if (s_rec == null)
                    return;
                Emit("action_cancelled", RecordObserve.DescribeAction(action, KeyOf(action)));
            }
        });
    }

    /// <summary>
    /// Prefix of ActionExecutor.AfterActionFinished (reached through the action's AfterFinished
    /// delegate). A player-driven action finishing is a decision boundary and gets a public
    /// observation; the game's hook actions only get a line.
    /// </summary>
    internal static void OnActionFinished(GameAction action)
    {
        if (!s_active)
            return;
        Guard("action_finished", () =>
        {
            lock (s_gate)
            {
                if (s_rec == null || action.State != GameActionState.Finished)
                    return;
                JsonObject data = RecordObserve.DescribeAction(action, KeyOf(action));
                bool boundary = action is not GenericHookGameAction && ActionQueueSet.IsGameActionPlayerDriven(action);
                if (boundary)
                    EmitWithObservation("action_finished", data, hidden: false);
                else
                    Emit("action_finished", data);
            }
        });
    }

    internal static void OnSentryCapture() => Interlocked.Increment(ref s_sentryCaptures);

    /// <summary>
    /// Prefix of DevConsole.ProcessCommand(string). In single player the command runs directly
    /// (ProcessCommandInternal), not as a ConsoleCmdGameAction, so nothing it does is in the game's
    /// replay, and what it leaves behind need not be in the save either (see s_consoleInProcess).
    /// </summary>
    internal static void OnConsoleCommand(string inputValue, Func<bool> goesThroughQueue)
    {
        Guard("console_command", () =>
        {
            lock (s_gate)
            {
                // Only a networked command in a multiplayer run with a local player is enqueued as a
                // ConsoleCmdGameAction (DevConsole.ProcessCommand); everything else - single player,
                // and every command at the menu - runs directly.
                bool viaQueue = goesThroughQueue();
                RunState? run = RunManager.Instance.DebugOnlyGetState();
                var line = new JsonObject
                {
                    ["command"] = inputValue.Trim(),
                    ["via_action_queue"] = viaQueue,
                    ["act"] = run?.CurrentActIndex,
                    ["act_floor"] = run?.ActFloor,
                    ["in_combat"] = CombatManager.Instance.IsInProgress,
                };
                if (!viaQueue)
                    s_consoleInProcess.Add(line.DeepClone());
                if (!s_active || s_rec == null)
                    return;
                if (!viaQueue)
                    s_rec.Faults.Add("console_command_outside_action_queue");
                Emit("console_command", line);
            }
        });
    }

    /// <summary>Postfix of RecordInitialState, recording on or off: a new room, a new replay to track.</summary>
    internal static void TrackRoomEntry(CombatReplayWriter writer)
    {
        Guard("track_room_entry", () =>
        {
            lock (s_gate)
            {
                s_track = new RoomTracker { Replay = RecorderPatches.ReplayOf(writer) };
            }
        });
    }

    /// <summary>
    /// Installed with a run already going (set_recording with "record": false in the config): the
    /// room's entry was not seen. Take what can be read now - the next choice ids and an action
    /// already paused - and mark the rest as unseen.
    /// </summary>
    private static void StartTrackingInsideRoom()
    {
        var t = new RoomTracker { Replay = null };
        TryRecordDo("start_tracking", () =>
        {
            IReadOnlyList<uint>? ids = RunManager.Instance.PlayerChoiceSynchronizer?.ChoiceIds;
            if (ids != null)
                for (int i = 0; i < ids.Count; i++)
                    t.ChoiceFloor[i] = ids[i];
            GameAction? paused = RecorderPatches.QueuedActions(RunManager.Instance.ActionQueueSet)
                .FirstOrDefault(a => a.State == GameActionState.GatheringPlayerChoice);
            if (paused != null)
            {
                t.Paused = paused;
                t.PausedUnseen = true;
                // Its first id was given out before tracking; the id it has now stands in.
                if (paused.Id is { } idNow)
                    s_actionKeys.AddOrUpdate(paused, new StrongBox<uint>(idNow));
            }
        });
        s_track = t;
    }

    internal static void OnCleanUpStart() => s_inCleanup = true;
    internal static void OnCleanUpEnd() => s_inCleanup = false;

    /// <summary>
    /// Prefix of CombatReplayWriter.WriteReplay. When the game is about to write (and stop) its
    /// replay, the record is closed with a copy of exactly that replay.
    /// </summary>
    internal static void OnWriteReplay(CombatReplayWriter writer, bool stopRecording)
    {
        if (!s_active || !stopRecording)
            return;
        Guard("write_replay", () =>
        {
            lock (s_gate)
            {
                CombatReplay? replay = RecorderPatches.ReplayOf(writer);
                if (s_rec == null || replay == null || !ReferenceEquals(s_rec.Replay, replay))
                    return;
                // Only two callers stop the replay: CombatManager.EndCombatInternal (won; the
                // CombatWon / CombatEnded events fire after this) and RunManager.CleanUp (after a
                // loss, or leaving mid-combat: save and quit, closing the window).
                string reason;
                if (!s_inCleanup)
                    reason = "combat_won";
                else if (s_rec.LossOutcome != null)
                    reason = "combat_lost";
                else
                    reason = "cleanup";
                CloseRecord(reason, replay);
                if (s_inCleanup)
                    s_writer.Drain(1000); // the window may be closing (CleanUp(graceful: false))
            }
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The process is going away; there is nowhere left to report to and the last file writes may themselves be failing.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "ERP022:Swallowed exception",
        Justification = "Same as CA1031: the process is exiting, so a fault line could not be written or read.")]
    private static void OnProcessExit()
    {
        try
        {
            lock (s_gate)
            {
                if (s_rec != null)
                    CloseRecord("process_exit", null);
            }
            s_writer.Drain(2000);
        }
        catch { /* the process is going away */ }
    }

    // ========================================================================================
    // Record lifecycle
    // ========================================================================================

    private static void TryOpen(string trigger)
    {
        if (s_rec != null)
            return;
        RunState? run = RunManager.Instance.DebugOnlyGetState();
        if (run == null)
            return;
        CombatReplayWriter? writer = RunManager.Instance.CombatReplayWriter;
        CombatReplay? replay = writer == null ? null : RecorderPatches.ReplayOf(writer);

        PendingInitial? pending = s_pending != null && replay != null && ReferenceEquals(s_pending.Replay, replay) ? s_pending : null;
        int eventsBefore = replay?.events.Count ?? 0;
        int checksBefore = replay?.checksumData.Count ?? 0;
        AbstractRoom? room = run.CurrentRoom;
        bool eventRoom = (room as CombatRoom)?.ParentEventId != null || run.CurrentRoomCount > 1;
        string boundary = ResolveOpenBoundary(eventRoom, pending, eventsBefore, checksBefore);
        OpenFacts facts = BeginOpenRecord(
            trigger, run, writer, replay, pending, boundary, eventRoom, eventsBefore, checksBefore);

        Emit("record_open", BuildRecordOpenEvent(facts));
        if (string.Equals(facts.Inputs, "tainted", StringComparison.Ordinal))
            facts.Record.Faults.Add("console_command_in_process");

        if (replay != null)
        {
            EmitOpenInitialState(replay, pending);
            BackfillExistingEvents(replay, eventsBefore, checksBefore);
        }
        s_pending = null;
    }

    /// <summary>What the record-open line reports, gathered while opening.</summary>
    private sealed record OpenFacts
    {
        internal required OpenRecord Record { get; init; }
        internal required RunState ActiveRun { get; init; }
        internal required string Trigger { get; init; }
        internal required int Attempt { get; init; }
        internal required CombatReplayWriter? GameWriter { get; init; }
        internal required CombatReplay? Replay { get; init; }
        internal required PendingInitial? Pending { get; init; }
        internal required string Boundary { get; init; }
        internal required bool EventRoom { get; init; }
        internal required string InitialState { get; init; }
        internal required int EventsBefore { get; init; }
        internal required int ChecksBefore { get; init; }
        internal required string Inputs { get; init; }
        internal required bool RoomTracked { get; init; }
        internal required System.Collections.Generic.IReadOnlyList<uint>? ChoiceIdsAtOpen { get; init; }
        internal required GameAction? PausedAtOpen { get; init; }
    }

    /// <summary>
    /// Creates the record for this room and gathers what the open line reports.
    /// </summary>
    private static OpenFacts BeginOpenRecord(
        string trigger,
        RunState run,
        CombatReplayWriter? writer,
        CombatReplay? replay,
        PendingInitial? pending,
        string boundary,
        bool eventRoom,
        int eventsBefore,
        int checksBefore)
    {
        long startTime = pending?.StartTime ?? RecorderPatches.RunStartTime();
        int act = pending?.Act ?? run.CurrentActIndex;
        int floor = pending?.Floor ?? run.ActFloor;

        string runDir = Path.Combine(Root, startTime.ToString(CultureInfo.InvariantCulture));
        (int number, int attempt) = NextNumbers(runDir, act, floor);
        string name = $"{number:D3}-a{act}f{floor:D2}";
        string dir = Path.Combine(runDir, name);
        var record = new OpenRecord
        {
            Id = $"{startTime}/{name}",
            Dir = dir,
            GameWriter = writer,
            Replay = replay,
            OpenedMs = s_clock.ElapsedMilliseconds,
            GameEventsBeforeOpen = eventsBefore,
            ChecksumsBeforeOpen = checksBefore,
            SentryAtOpen = s_sentryCaptures,
            FirstRequest = Interlocked.Read(ref s_apiSeq) + 1,
        };
        s_rec = record;
        s_recordsOpened++;
        s_reservedDirs.Add(dir);
        return new OpenFacts
        {
            Record = record,
            ActiveRun = run,
            Trigger = trigger,
            Attempt = attempt,
            GameWriter = writer,
            Replay = replay,
            Pending = pending,
            Boundary = boundary,
            EventRoom = eventRoom,
            InitialState = DescribeInitialState(replay, pending),
            EventsBefore = eventsBefore,
            ChecksBefore = checksBefore,
            Inputs = DescribeReplayInputs(),
            RoomTracked = replay != null && ReferenceEquals(s_track.Replay, replay),
            ChoiceIdsAtOpen = RunManager.Instance.PlayerChoiceSynchronizer?.ChoiceIds,
            PausedAtOpen = s_track.Paused,
        };
    }

    /// <summary>Whether the record copied the room's start itself, read it back, or has none.</summary>
    private static string DescribeInitialState(CombatReplay? replay, PendingInitial? pending)
    {
        if (replay == null)
            return "missing";
        return pending != null ? "room_entry" : "read_back";
    }

    private static string ResolveOpenBoundary(
        bool eventRoom, PendingInitial? pending, int eventsBefore, int checksBefore)
    {
        if (eventRoom)
            return "event_room";
        if (pending != null && eventsBefore == 0 && checksBefore == 0)
            return "room_entry";
        return CombatManager.Instance.IsInProgress ? "mid_combat" : "mid_room";
    }

    /// <summary>
    /// Where this process's inputs came from. The game's replay cannot reproduce a console
    /// command, so a record is only "complete" when the recorder saw the whole run.
    /// </summary>
    private static string DescribeReplayInputs()
    {
        if (s_consoleInProcess.Count > 0)
            return "tainted";
        return string.Equals(s_installedAt, "startup", StringComparison.Ordinal) ? "complete" : "unknown";
    }

    private static JsonObject BuildRecordOpenEvent(OpenFacts facts)
        => new()
        {
            ["schema"] = Schema,
            ["record_id"] = s_rec!.Id,
            ["trigger"] = facts.Trigger,
            ["attempt"] = facts.Attempt,
            ["session"] = new JsonObject
            {
                ["id"] = s_sessionId,
                ["pid"] = System.Environment.ProcessId,
                ["started_utc"] = s_sessionStarted.ToString("O"),
            },
            ["mod"] = new JsonObject
            {
                ["version"] = McpMod.Version,
                ["commit"] = McpMod.BuildCommit,
                ["state_schema"] = McpMod.StateSchemaVersion,
            },
            ["game"] = new JsonObject
            {
                ["version"] = ReleaseInfoManager.Instance.ReleaseInfo?.Version,
                ["commit"] = ReleaseInfoManager.Instance.ReleaseInfo?.Commit,
                ["model_id_hash"] = ModelIdSerializationCache.Hash,
                ["sts2_dll_sha256"] = s_dllSha256,
            },
            ["profile_id"] = SafeProfileId(),
            ["run"] = facts.Pending?.Run.DeepClone() ?? RunInfo(facts.ActiveRun, null),
            ["start"] = BuildRecordStartSection(facts),
        };

    private static JsonObject BuildRecordStartSection(OpenFacts facts)
        => new()
        {
            ["boundary"] = facts.Boundary,
            // The game's replay starts at the room's entry either way; whether this record
            // copied that start itself (room_entry) or read it back from the game later.
            ["initial_state"] = facts.InitialState,
            ["game_events_before_open"] = facts.EventsBefore,
            ["checksums_before_open"] = facts.ChecksBefore,
            ["game_writer_enabled"] = facts.GameWriter?.IsEnabled,
            ["game_writer_recording"] = facts.Replay != null,
            ["reproducible_from_start"] = string.Equals(facts.Boundary, "room_entry", StringComparison.Ordinal),
            ["pre_combat_requests"] = facts.EventRoom ? facts.Pending?.Requests.DeepClone() : null,
            // "room_entry": the tracking layer saw this room's entry (it knows every reservation,
            // pause and request since). "inside_room": it started inside the room (installed by
            // set_recording): what came before is marked unseen where it matters.
            ["tracking"] = facts.RoomTracked ? "room_entry" : "inside_room",
            ["recorder_installed"] = s_installedAt,
            ["choice_ids_at_open"] = facts.ChoiceIdsAtOpen == null ? null
                : new JsonArray(facts.ChoiceIdsAtOpen.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
            ["paused_at_open"] = facts.PausedAtOpen == null
                ? null : RecordObserve.DescribeAction(facts.PausedAtOpen, KeyOf(facts.PausedAtOpen)),
            ["inputs_outside_replay"] = facts.Inputs,
            ["console_in_process"] = s_consoleInProcess.Count > 0 ? s_consoleInProcess.DeepClone() : null,
        };

    /// <summary>
    /// Copies the game's own start-of-room replay state into the record: counters as the game
    /// holds them now, since the header's are from the room entry and something may have run
    /// in between (an event before its fight).
    /// </summary>
    private static void EmitOpenInitialState(CombatReplay replay, PendingInitial? pending)
    {
        JsonObject idsNow = IdsNow();
        JsonNode ids = pending?.Ids.DeepClone() ?? IdsOf(replay);
        long seq = Emit("initial_state", new JsonObject { ["ids"] = ids, ["ids_at_open"] = idsNow }, hidden: true);
        EmitHidden(seq, "initial_state", new JsonObject
        {
            ["format"] = "CombatReplay packet (events and checksums empty), game PacketWriter; player ids as the game holds them",
            ["packet"] = Convert.ToBase64String(pending?.HeaderPacket ?? Packet(HeaderOf(replay))),
        });
    }

    /// <summary>
    /// Enabled mid-combat: copy what the game already holds, marked as such. The MOD's own
    /// lines (origin, turn context, observations) for that stretch do not exist.
    /// </summary>
    private static void BackfillExistingEvents(CombatReplay replay, int eventsBefore, int checksBefore)
    {
        for (int i = 0; i < eventsBefore; i++)
            JournalEvent(replay, i, null, backfilled: true);
        for (int i = 0; i < checksBefore; i++)
            JournalChecksum(replay, i, backfilled: true);
        s_rec!.Backfilled = eventsBefore + checksBefore;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A self-check failure must not stop the record from closing; it is reported inside the record's own self_check block.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "EPC12:Suspicious exception handling",
        Justification = "The self_check block carries the exception type and message by design - that is what a reader of the record needs.")]
    private static void CloseRecord(string reason, CombatReplay? replay)
    {
        OpenRecord? rec = s_rec;
        if (rec == null)
            return;

        var selfCheck = new JsonObject();
        if (replay != null)
        {
            try
            {
                selfCheck["events"] = Compare(replay.events.Select(e => Sha(Packet(e))).ToList(), rec.EventHashes);
                selfCheck["checksums"] = Compare(replay.checksumData.Select(ChecksumKey).ToList(), rec.ChecksumKeys);
                // The game anonymizes this same replay on the next line of WriteReplay; doing it one
                // call earlier makes the same Rng.Chaotic draws in the same order (IdAnonymizer caches).
                byte[] mcr = Packet(replay.Anonymized());
                selfCheck["replay_mcr_sha256"] = Sha(mcr);
                selfCheck["replay_mcr_bytes"] = mcr.Length;
                s_writer.WriteRaw(Path.Combine(rec.Dir, "replay.mcr"), mcr);
            }
            catch (Exception ex)
            {
                rec.Faults.Add($"close:{ex.GetType().Name}");
                selfCheck["error"] = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        JsonObject? outcome = rec.LossOutcome ?? Outcome();
        Emit("record_close", new JsonObject
        {
            ["reason"] = reason,
            ["outcome"] = outcome,
            ["game_replay_captured"] = replay != null,
            ["journal"] = new JsonObject
            {
                ["game_events"] = rec.EventHashes.Count,
                ["checksums"] = rec.ChecksumKeys.Count,
                ["backfilled"] = rec.Backfilled,
                ["last_seq"] = rec.Seq,
            },
            ["self_check"] = selfCheck,
            ["sentry_captures"] = s_sentryCaptures - rec.SentryAtOpen,
            ["main_thread_us"] = Timing(rec.HookMicros),
            ["faults"] = new JsonArray(rec.Faults.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
        });
        s_writer.CloseFile(rec.PublicPath);
        s_writer.CloseFile(rec.HiddenPath);
        s_rec = null;
    }

    /// <summary>The journal against the game's own list at close, position by position.</summary>
    private static JsonObject Compare(List<string> game, List<string> journal)
    {
        int? firstMismatch = null;
        for (int i = 0; i < Math.Max(game.Count, journal.Count); i++)
        {
            if (i >= game.Count || i >= journal.Count || !string.Equals(game[i], journal[i], StringComparison.Ordinal))
            {
                firstMismatch = i;
                break;
            }
        }
        return new JsonObject
        {
            ["game"] = game.Count,
            ["journal"] = journal.Count,
            ["first_mismatch"] = firstMismatch,
            ["complete"] = firstMismatch == null,
        };
    }

    private static JsonObject Timing(List<double> micros)
    {
        if (micros.Count == 0)
            return new JsonObject { ["count"] = 0 };
        var sorted = micros.OrderBy(x => x).ToList();
        double P(double q) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
        return new JsonObject
        {
            ["count"] = sorted.Count,
            ["p50"] = Math.Round(P(0.50), 1),
            ["p99"] = Math.Round(P(0.99), 1),
            ["max"] = Math.Round(sorted[^1], 1),
            ["total_ms"] = Math.Round(sorted.Sum() / 1000.0, 2),
        };
    }

    private static (int number, int attempt) NextNumbers(string runDir, int act, int floor)
    {
        int max = 0, same = 0;
        string suffix = $"-a{act}f{floor:D2}";
        // A directory listing that fails leaves the counters where they were, so the
        // number is never reused but may not be the smallest free one.
        TryRecordDo("next_numbers", () =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(runDir))
                foreach (string d in Directory.GetDirectories(runDir))
                    names.Add(Path.GetFileName(d));
            foreach (string? d in s_reservedDirs.Where(d => string.Equals(Path.GetDirectoryName(d), runDir, StringComparison.Ordinal)))
                names.Add(Path.GetFileName(d));
            foreach (string n in names)
            {
                int dash = n.IndexOf('-');
                if (dash > 0 && int.TryParse(n[..dash], CultureInfo.InvariantCulture, out int k))
                    max = Math.Max(max, k);
                if (n.EndsWith(suffix, StringComparison.Ordinal))
                    same++;
            }
        });
        return (max + 1, same + 1);
    }

    // ========================================================================================
    // Emit helpers (all called with s_gate held)
    // ========================================================================================

    /// <param name="noAt">For entries copied after the fact (backfill): "at" would be the
    /// moment of copying, not of the entry, so it is left out.</param>
    private static long Emit(string kind, JsonObject? data, bool hidden = false, bool noAt = false)
    {
        OpenRecord? rec = s_rec;
        if (rec == null)
            return -1;
        var line = new JsonObject
        {
            ["seq"] = rec.Seq++,
            ["kind"] = kind,
            ["t_ms"] = s_clock.ElapsedMilliseconds - rec.OpenedMs,
            ["at"] = noAt ? null : SafeAt(),
        };
        if (hidden)
            line["hidden"] = true;
        MoveInto(line, data);
        s_writer.AppendLine(rec.PublicPath, line);
        return line["seq"]!.GetValue<long>();
    }

    private static void EmitHidden(long seq, string kind, JsonObject data)
    {
        OpenRecord? rec = s_rec;
        if (rec == null || seq < 0)
            return;
        var line = new JsonObject { ["seq"] = seq, ["kind"] = kind };
        MoveInto(line, data);
        s_writer.AppendLine(rec.HiddenPath, line);
    }

    private static void EmitWithObservation(string kind, JsonObject? data, bool hidden)
    {
        (JsonObject pub, JsonObject hidden)? obs = SafeObserve();
        data ??= new JsonObject();
        if (obs is { } o)
            data["observation"] = o.pub;
        bool withHidden = hidden && obs != null;
        long seq = Emit(kind, data, hidden: withHidden);
        if (withHidden)
            EmitHidden(seq, "observation", new JsonObject { ["observation"] = obs!.Value.hidden });
    }

    /// <summary>For lines produced off the main thread (HTTP): no "at", no game reads.</summary>
    private static void EmitNoGameRead(string kind, JsonObject data)
    {
        OpenRecord? rec = s_rec;
        if (rec == null)
            return;
        var line = new JsonObject { ["seq"] = rec.Seq++, ["kind"] = kind, ["t_ms"] = s_clock.ElapsedMilliseconds - rec.OpenedMs };
        MoveInto(line, data);
        s_writer.AppendLine(rec.PublicPath, line);
    }

    private static void MoveInto(JsonObject line, JsonObject? data)
    {
        if (data == null)
            return;
        foreach ((string? k, JsonNode? v) in data.ToList())
        {
            data.Remove(k);
            line[k] = v;
        }
    }

    private static JsonObject? SafeAt()
    {
        return TryRecord("at", () => RecordObserve.At());
    }

    private static (JsonObject pub, JsonObject hidden)? SafeObserve()
    {
        return TryRecord("observe", () => RecordObserve.Observe(SlotOf));
    }

    /// <summary>
    /// The one door into the recorder from the game. Catches everything (the game's own handlers
    /// around ActionResumed and PlayerChoiceReceived have no try/catch, and ActionEnqueued's sends
    /// what it catches to Sentry), and times the work done on the game's thread.
    /// </summary>
    private static void Guard(string where, Action body)
    {
        long start = Stopwatch.GetTimestamp();
        TryRecordDo(where, body);
        // Timing is collected whether or not the hook body worked, and is never worth a fault.
        TryRecordQuietDo(() =>
        {
            OpenRecord? rec = s_rec;
            if (rec != null && rec.HookMicros.Count < 200_000)
                rec.HookMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Last-resort reporter: it must not throw into the hook that called it, and there is no other channel left to use.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "ERP022:Swallowed exception",
        Justification = "Same as CA1031: reporting this failure would mean calling NoteFault from inside NoteFault.")]
    private static void NoteFault(string where, Exception ex)
    {
        try
        {
            lock (s_gate)
            {
                Interlocked.Increment(ref s_faultsTotal);
                s_lastFault = $"{where}: {ex.GetType().Name}: {ex.Message}";
                OpenRecord? rec = s_rec;
                if (rec == null)
                    return;
                rec.Faults.Add(where);
                s_writer.AppendLine(rec.PublicPath, new JsonObject
                {
                    ["seq"] = rec.Seq++,
                    ["kind"] = "recorder_fault",
                    ["t_ms"] = s_clock.ElapsedMilliseconds - rec.OpenedMs,
                    ["where"] = where,
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                });
            }
        }
        catch
        {
            // Never throw into the game - and never call NoteFault again from here.
        }
    }

    /// <summary>
    /// Reads one value for a record line. A recorder must never throw into the game's
    /// hook, so a failure becomes a recorder fault and the line keeps the same "not
    /// there" meaning the record contract uses.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The recorder reads game members by name and must survive any of them changing.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "ERP022:Swallowed exception",
        Justification = "The exception is routed to NoteFault, which records the type, the message and the site.")]
    internal static T? TryRecord<T>(string where, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            NoteFault(where, ex);
            return default;
        }
    }

    /// <summary><see cref="TryRecord{T}"/> for a body whose only outcome is "it worked".</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The recorder reads game members by name and must survive any of them changing.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "ERP022:Swallowed exception",
        Justification = "The exception is routed to NoteFault, which records the type, the message and the site.")]
    internal static bool TryRecordDo(string where, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            NoteFault(where, ex);
            return false;
        }
    }

    /// <summary>
    /// Best-effort recorder work whose failure is not worth a fault line: a timing
    /// sample, or a reader whose absence is the normal case.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "These reads only feed diagnostics, so a miss changes nothing a consumer acts on.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "ERP022:Swallowed exception",
        Justification = "A fault line per timing sample would drown the faults that matter.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2486:Exceptions should not be ignored",
        Justification = "Same as ERP022: a missing timing sample is not a compatibility break.")]
    internal static T? TryRecordQuiet<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary><see cref="TryRecordQuiet{T}"/> for a body whose only outcome is "it worked".</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "These reads only feed diagnostics, so a miss changes nothing a consumer acts on.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "ERP022:Swallowed exception",
        Justification = "A fault line per timing sample would drown the faults that matter.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2486:Exceptions should not be ignored",
        Justification = "Same as ERP022: a missing timing sample is not a compatibility break.")]
    internal static void TryRecordQuietDo(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Nothing to report: see the justification above.
        }
    }

    // ========================================================================================
    // Small readers
    // ========================================================================================

    private static readonly PacketWriter PacketWriterInstance = new() { WarnOnGrow = false };

    /// <summary>The game's own serializer (a writer of our own), into a fresh array.</summary>
    private static byte[] Packet<T>(T value) where T : IPacketSerializable
    {
        lock (PacketWriterInstance)
        {
            PacketWriter w = PacketWriterInstance;
            w.Reset();
            w.Write(value);
            w.ZeroByteRemainder();
            return w.Buffer.AsSpan(0, w.BytePosition).ToArray();
        }
    }

    /// <summary>The replay's header: the same fields, events and checksums left empty. A data copy.</summary>
    private static CombatReplay HeaderOf(CombatReplay replay) => new()
    {
        version = replay.version,
        gitCommit = replay.gitCommit,
        modelIdHash = replay.modelIdHash,
        choiceIds = replay.choiceIds.ToList(),
        rewardIds = replay.rewardIds.ToList(),
        nextActionId = replay.nextActionId,
        nextChecksumId = replay.nextChecksumId,
        nextHookId = replay.nextHookId,
        serializableRun = replay.serializableRun,
    };

    private static string ChecksumKey(ReplayChecksumData c) => $"{c.checksumData.id}:{c.checksumData.checksum}:{c.context}";

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonObject IdsOf(CombatReplay replay) => new()
    {
        ["next_action_id"] = replay.nextActionId,
        ["next_hook_id"] = replay.nextHookId,
        ["next_checksum_id"] = replay.nextChecksumId,
        ["choice_ids"] = new JsonArray(replay.choiceIds.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
        ["reward_ids"] = new JsonArray(replay.rewardIds.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
    };

    private static JsonObject IdsNow()
    {
        RunManager rm = RunManager.Instance;
        return new JsonObject
        {
            ["next_action_id"] = rm.ActionQueueSet?.NextActionId,
            ["next_hook_id"] = rm.ActionQueueSynchronizer?.NextHookId,
            ["next_checksum_id"] = rm.ChecksumTracker?.NextId,
            ["choice_ids"] = rm.PlayerChoiceSynchronizer == null ? null
                : new JsonArray(rm.PlayerChoiceSynchronizer.ChoiceIds.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
        };
    }

    private static JsonObject RoomInfo(CombatState state)
    {
        RunState? run = RunManager.Instance.DebugOnlyGetState();
        AbstractRoom? room = run?.CurrentRoom;
        return new JsonObject
        {
            ["encounter"] = state.Encounter?.Id.Entry,
            ["room_type"] = room?.RoomType.ToString(),
            ["room_class"] = room?.GetType().Name,
            ["parent_event"] = (room as CombatRoom)?.ParentEventId?.Entry,
            ["room_count"] = run?.CurrentRoomCount,
            ["enemies"] = new JsonArray(state.Enemies.Select(e => (JsonNode)new JsonObject
            {
                ["combat_id"] = e.CombatId,
                ["monster"] = e.Monster?.Id.Entry,
            }).ToArray()),
        };
    }

    private static JsonObject? Outcome()
        => TryRecord("outcome", OutcomeCore);

    private static JsonObject? OutcomeCore()
    {
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        Player? me = RecordObserve.LocalPlayer();
        if (combat == null || me == null)
            return null;
        return new JsonObject
        {
            ["player_hp"] = me.Creature.CurrentHp,
            ["player_alive"] = me.Creature.IsAlive,
            ["enemies_alive"] = combat.Enemies.Count(e => e.IsAlive),
            ["round"] = combat.RoundNumber,
            ["turn"] = me.PlayerCombatState?.TurnNumber,
        };
    }

    private static JsonObject RunInfo(RunState? run, SerializableRun? save)
    {
        Player? player = run == null ? null : RecordObserve.LocalPlayer();
        return new JsonObject
        {
            ["start_time"] = save?.StartTime ?? (run == null ? null : RecorderPatches.RunStartTime()),
            ["character"] = player?.Character.Id.Entry,
            ["ascension"] = run?.AscensionLevel ?? save?.Ascension,
            ["act"] = run?.CurrentActIndex ?? save?.CurrentActIndex,
            ["act_floor"] = run?.ActFloor,
            ["total_floor"] = run?.TotalFloor,
            ["map_coord"] = run?.CurrentMapCoord is { } c ? new JsonObject { ["col"] = c.col, ["row"] = c.row } : null,
        };
    }

    private static uint? AsUInt(JsonNode? node)
    {
        return TryRecordQuiet<uint?>(() => node is JsonValue v && v.TryGetValue(out uint u) ? u : null);
    }

    private static uint? KeyOf(GameAction action) =>
        s_actionKeys.TryGetValue(action, out StrongBox<uint>? box) ? box.Value : action.Id;

    private static JsonArray PendingChoices(int? slot) =>
        new((s_track.OpenChoices.TryGetValue(slot ?? -1, out SortedSet<uint>? open) ? open : new SortedSet<uint>())
            .Select(c => (JsonNode)JsonValue.Create(c)).ToArray());

    private static int? PlayerSlot(Player player)
    {
        RunState? run = RunManager.Instance.DebugOnlyGetState();
        if (run == null)
            return null;
        for (int i = 0; i < run.Players.Count; i++)
            if (ReferenceEquals(run.Players[i], player))
                return i;
        return null;
    }

    /// <summary>A platform player id as the player's slot in the run (never the id itself).</summary>
    internal static int? SlotOf(ulong? netId)
    {
        if (netId is not { } id)
            return null;
        RunState? run = RunManager.Instance.DebugOnlyGetState();
        if (run == null)
            return null;
        for (int i = 0; i < run.Players.Count; i++)
            if (run.Players[i].NetId == id)
                return i;
        return -1;
    }

    private static int? OwnerSlot(GameAction action)
    {
        return TryRecordQuiet<int?>(() => SlotOf(action.OwnerId));
    }

    private static int? SafeProfileId()
    {
        return TryRecordQuiet<int?>(() => SaveManager.Instance.CurrentProfileId);
    }
}
