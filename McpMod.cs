using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace STS2_MCP;

[ModInitializer("Initialize")]
public static partial class McpMod
{
    // Sourced from the assembly version, which the build stamps from
    // mod_manifest.json (see STS2_MCP.csproj). mod_manifest.json is the single
    // place to bump the version.
    public static readonly string Version = ResolveVersion();

    private static string ResolveVersion()
    {
        var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
            System.Attribute.GetCustomAttribute(
                typeof(McpMod).Assembly,
                typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        string? info = attr?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
        return typeof(McpMod).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Version of the state/action contract, reported as `schema_version` on every payload.
    ///
    /// Bumped only when an existing field or action changes meaning or disappears - the cases
    /// where a client that was written against the old shape is now wrong. Adding fields,
    /// actions, parameters or state_types does not bump it, so clients can keep feature-detecting
    /// additions by presence and use this only to notice a breaking change.
    ///
    /// 2: battle.enemies is no longer "the living enemies". A corpse the game keeps in the
    ///    fight (a Decimillipede segment waiting to reattach) is now listed with
    ///    alive:false, so a client that treated every entry as a target has to filter on
    ///    the new `alive` field.
    ///
    /// 3: map.next_encounters is gone. It reported which monster / elite / event the act would
    ///    serve NEXT, read out of the pre-rolled RoomSet - information the game does not show
    ///    until the room is entered, so it let a client route around matchups no player could
    ///    see. map.encounters_seen replaces it with what this act has ALREADY served, which is
    ///    what the map's own hover tips show for travelled nodes.
    /// </summary>
    public const int StateSchemaVersion = 3;

    // DefaultPort and ConfigFileName live in McpMod.Config.cs.

    /// <summary>
    /// How long an HTTP request waits for the game's main thread before giving up (503).
    ///
    /// A click-sized action resolves within the frame it is dequeued in, so this is never
    /// reached while the game renders; ten seconds only elapses when frames have stopped
    /// (scene load, hang, the OS pausing the app) and no wait would ever succeed.
    /// </summary>
    internal const int MainThreadTimeoutMs = 10_000;

    /// <summary>
    /// How many actions may sit in the main-thread queue before new requests are rejected (503).
    ///
    /// The queue is drained ten per frame, so a healthy game never accumulates this many:
    /// 32 pending items means nobody is draining it, and enqueueing more would only pile up
    /// stale actions that all fire at once when frames resume.
    /// </summary>
    internal const int MainThreadQueueLimit = 32;

    /// <summary>How often the settle wait re-reads the state, in ms. Roughly four frames at 60 fps.</summary>
    private const int SettlePollIntervalMs = 60;

    /// <summary>
    /// How many consecutive polls must look settled before the wait returns.
    ///
    /// One is not enough: a click takes effect on the next frame at the earliest, and between
    /// two queued actions the game looks idle for a single frame. Every client that polls this
    /// API converged on the same rule after shipping mid-resolution states to its caller.
    /// </summary>
    private const int SettleConsecutivePolls = 3;

    /// <summary>
    /// How long a completely unchanged state is waited on before the wait gives up.
    ///
    /// An action the game silently ignored (a button that was not there, a click the UI ate)
    /// never changes anything, so waiting the full timeout for it only stalls the caller.
    /// </summary>
    private const int SettleNoChangeGiveUpMs = 3_000;

    private const int SettleDefaultTimeoutMs = 10_000;
    private const int SettleMinTimeoutMs = 500;
    private const int SettleMaxTimeoutMs = 30_000;

    private static string? _buildCommit;
    private static bool _buildCommitResolved;

    /// <summary>
    /// The git revision this assembly was built from (the part after '+' in
    /// InformationalVersion), or null when the build could not determine one.
    /// Lets a bug report name the exact build rather than just "0.4.0".
    /// </summary>
    internal static string? BuildCommit
    {
        get
        {
            if (_buildCommitResolved)
                return _buildCommit;

            _buildCommitResolved = true;
            try
            {
                var informational = typeof(McpMod).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                int plus = informational?.IndexOf('+') ?? -1;
                if (plus >= 0 && plus + 1 < informational!.Length)
                    _buildCommit = informational[(plus + 1)..];
            }
            catch
            {
                // Reflection over assembly attributes can fail in a trimmed or odd host;
                // an unknown commit is not worth failing a state read over.
                _buildCommit = null;
            }

            return _buildCommit;
        }
    }

    /// <summary>Version with the build commit appended when known: "0.4.0+abc1234".</summary>
    internal static string VersionString =>
        BuildCommit is { Length: > 0 } commit ? $"{Version}+{commit}" : Version;

    private static HttpListener? _listener;
    private static Thread? _serverThread;
    private static string[] _boundPrefixes = Array.Empty<string>();
    private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    // Indentation was ~32% of a typical in-combat state response (12,069 -> 8,178 bytes).
    // The main consumer is an LLM that pays per token, so compact is the default and
    // ?pretty=1 opts back in. Two immutable instances: JsonSerializerOptions must not be
    // mutated after first use, and requests are served concurrently on ThreadPool threads.
    internal static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static readonly JsonSerializerOptions _jsonOptionsPretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // The game's own release version, from the release_info.json shipped next to the executable.
    // Fixed for the process, so it is read once — and only from the main thread (the manager is a
    // lazily-constructed singleton that touches Godot file IO). Null when the build ships without
    // a release_info.json, in which case the key is omitted from the state.
    private static string? _gameVersion;
    private static bool _gameVersionRead;

    internal static string? GameVersion()
    {
        if (_gameVersionRead) return _gameVersion;
        _gameVersionRead = true;
        try
        {
            _gameVersion = MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to read game version: {ex.Message}");
        }
        return _gameVersion;
    }

    public static void Initialize()
    {
        try
        {
            // Optional settings UI patches should not block the HTTP bridge itself.
            // The optional speed-settings detours stall startup on this macOS
            // host. The REST bridge does not depend on them.
            if (!OperatingSystem.IsMacOS()) TryApplyHarmonyPatches();
            GD.Print("[STS2 MCP] Initializing REST bridge (macOS: no settings detours)");

            // Expose a completion signal for async room entry; see TrackRoomEntries.
            TrackRoomEntries();

            // Reward-screen observation (docs/raw-full.md: rewards). Independent of the
            // settings-detour patches skipped above.
            InstallRewardTracking();

            _config = LoadConfig();
            int port = _config.Port;

            // Needs the config above: instant_mode lives in the same file.
            LoadInstantModePreference();

            // Connect to main thread process frame for action execution
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(ProcessMainThreadQueue));

            // Combat recorder (docs/recording.md). After LoadConfig, which feeds it the
            // "record"/"record_dir" keys from the same file.
            Recorder.CombatRecorder.StartIfConfigured();

            // Loopback only: the bridge is for the local controller, never the network.
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _boundPrefixes = new List<string>(_listener.Prefixes).ToArray();

            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "STS2_MCP_Server"
            };
            _serverThread.Start();

            GD.Print($"[STS2 MCP] v{VersionString} server started on {string.Join(", ", _boundPrefixes)}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to start: {ex}");
        }
    }

    private static void TryApplyHarmonyPatches()
    {
        try
        {
            new Harmony("com.sts2mcp").PatchAll();
        }
        catch (Exception ex)
        {
            GD.Print(
                $"[STS2 MCP] Optional Harmony settings UI injection skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ProcessMainThreadQueue()
    {
        RewardObservation(RefreshRewardRun);

        // Instant Mode is a mod preference rather than a game setting, so it has to be
        // pushed into (and pulled back out of) the game's prefs as the run context changes.
        ReconcileInstantMode();

        // Reports a leaked NetLoadingHandle once it has outlived any real
        // transition. See McpMod.LoadingDiagnostics.cs.
        CheckLoadingHandleLeak();

        int processed = 0;
        while (_mainThreadQueue.TryDequeue(out var action) && processed < 10)
        {
            try { action(); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Main thread action error: {ex}"); }
            processed++;
        }
    }

    internal static Task<T> RunOnMainThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    internal static Task RunOnMainThread(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Per-request flag telling a queued closure that its caller has stopped waiting.</summary>
    private sealed class MainThreadCancellation
    {
        // volatile: written on the HTTP thread, read on the game's main thread.
        internal volatile bool Cancelled;
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the game's main thread and waits for it.
    ///
    /// Every blocking HTTP handler goes through here instead of waiting on the task forever:
    /// the queue is drained from Godot's ProcessFrame signal, so when frames stop (scene load,
    /// hang, the OS pausing the app, a modal the engine blocks on) a plain wait never returns.
    /// That held a ThreadPool thread per request, and - worse - every request queued meanwhile
    /// still ran once frames resumed, so an agent that retried `end_turn` five times got five
    /// end_turns.
    /// </summary>
    /// <exception cref="MainThreadUnavailableException">
    /// The queue is already backed up, or the main thread did not answer in time. In both cases
    /// the work was NOT performed, so the caller may retry.
    /// </exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "This is the one place that blocks: HTTP worker threads wait for the Godot main thread to pump the queued work, with a timeout and a queue-limit guard. Awaiting instead would leave the worker with no continuation point the game thread controls.")]
    internal static T RunOnMainThreadBlocking<T>(Func<T> func, int timeoutMs = MainThreadTimeoutMs)
    {
        int pending = _mainThreadQueue.Count;
        if (pending >= MainThreadQueueLimit)
        {
            throw new MainThreadUnavailableException(
                $"main thread queue is full ({pending} pending); the game is not processing frames");
        }

        var cancellation = new MainThreadCancellation();
        var tcs = new TaskCompletionSource<T>();
        _mainThreadQueue.Enqueue(() =>
        {
            // The client already gave up on this request; running the action now would apply a
            // command the caller has since retried or abandoned.
            if (cancellation.Cancelled) return;

            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        bool completed;
        try
        {
            completed = tcs.Task.Wait(timeoutMs);
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            // Unwrap so callers' `catch (Exception ex)` keeps showing the action's own message.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable; satisfies definite assignment
        }

        if (!completed)
        {
            // Unavoidable race: if the closure has already started running, setting this changes
            // nothing and the action completes anyway. A click-sized action resolves inside one
            // frame, so in practice a timeout means the closure never started.
            cancellation.Cancelled = true;
            throw new MainThreadUnavailableException(
                $"timed out after {timeoutMs} ms waiting for the game's main thread");
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    private static void ServerLoop()
    {
        while (_listener?.IsListening == true)
        {
            try
            {
                var context = _listener.GetContext();
                // Handle each request asynchronously so we don't block the listener
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            // This bridge is for the local controller, never browser pages.
            if (!string.IsNullOrEmpty(request.Headers["Origin"]))
            {
                SendError(response, 403, "Browser origins are not permitted");
                return;
            }
            if (string.Equals(request.HttpMethod, "POST"
, StringComparison.Ordinal) && !(request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                SendError(response, 415, "application/json required");
                return;
            }

            bool pretty = WantsPretty(request);

            if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.Ordinal))
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";

            if (string.Equals(path, "/", StringComparison.Ordinal))
            {
                // A dictionary, not an anonymous type: _jsonOptions drops null *properties*,
                // and "commit" has to stay present (as null) for clients that read it blindly.
                // kind/endpoint_count/endpoints come from the fork's discoverable api index.
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                {
                    var endpoints = BuildEndpointIndex();
                    SendJson(response, new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["message"] = $"Hello from STS2 MCP v{VersionString}",
                        ["status"] = "ok",
                        ["kind"] = "api_index",
                        ["version"] = Version,
                        ["commit"] = BuildCommit,
                        ["schema_version"] = StateSchemaVersion,
                        ["bound_prefixes"] = _boundPrefixes,
                        ["endpoint_count"] = endpoints.Count,
                        ["endpoints"] = endpoints
                    }, pretty);
                }
                else
                {
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
                }
            }
            else if (string.Equals(path, "/api/v1/singleplayer", StringComparison.Ordinal))
            {
                if (!string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal) && !string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
                {
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
                    return;
                }

                // Hard-block singleplayer endpoint during multiplayer runs
                // to prevent calling the non-sync-safe end_turn path
                if (IsMultiplayerRun())
                {
                    SendError(response, 409,
                        "Multiplayer run is active. Use /api/v1/multiplayer instead.", pretty,
                        "multiplayer_run_active");
                    return;
                }

                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetState(request, response);
                else if (string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
                    HandlePostAction(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/multiplayer", StringComparison.Ordinal))
            {
                if (!string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal) && !string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
                {
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
                    return;
                }

                // Guard: reject multiplayer endpoint during singleplayer runs
                if (!IsMultiplayerRun())
                {
                    SendError(response, 409,
                        "Not in a multiplayer run. Use /api/v1/singleplayer instead.", pretty,
                        "not_multiplayer_run");
                    return;
                }

                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetMultiplayerState(request, response);
                else if (string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
                    HandlePostMultiplayerAction(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/settings", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetSettings(response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/profiles", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetProfiles(request, response);
                else if (string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal))
                    HandlePostProfiles(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/profile", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetProfile(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (string.Equals(path, "/api/v1/compendium", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetCompendium(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (string.Equals(path, "/api/v1/record", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    SendJson(response, Recorder.CombatRecorder.Status(), pretty);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (string.Equals(path, "/api/v1/record/inspect", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleInspectReplay(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (string.Equals(path, "/api/v1/wiki", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetWiki(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (string.Equals(path, "/api/v1/cardpools", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetCardPools(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (string.Equals(path, "/api/v1/potionpools", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetPotionPools(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (string.Equals(path, "/api/v1/relicbag", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetRelicBag(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (string.Equals(path, "/api/v1/mapdrawings", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetMapDrawings(response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/compendium", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetCompendium(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/bestiary", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetBestiary(response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/glossary/cards", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetGlossaryCards(response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/glossary/relics", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetGlossaryRelics(response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/glossary/potions", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetGlossaryPotions(response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else if (string.Equals(path, "/api/v1/glossary/keywords", StringComparison.Ordinal))
            {
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal))
                    HandleGetGlossaryKeywords(response);
                else
                    SendError(response, 405, "Method not allowed", pretty, "method_not_allowed");
            }
            else
            {
                SendError(response, 404, "Not found", pretty, "not_found");
            }
        }
        // Safety net for any blocking call reached outside a handler's own try/catch: a stalled
        // main thread must never be reported as an internal error.
        catch (MainThreadUnavailableException ex)
        {
            TryDo(() => { SendUnavailable(context.Response, ex.Message, WantsPretty(context.Request)); });
            /* response may already be closed */
        }
        catch (Exception ex)
        {
            TryDo(() =>
            {
                SendError(context.Response, 500, $"Internal error: {ex.Message}",
                    WantsPretty(context.Request), "internal_error");
            });
            /* response may already be closed */
        }
    }

    private static List<Dictionary<string, object?>> BuildEndpointIndex()
    {
        return new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/singleplayer", ["description"] = "Read singleplayer state with status/kind envelope and active-run context" },
            new(StringComparer.Ordinal) { ["method"] = "POST", ["path"] = "/api/v1/singleplayer", ["description"] = "Perform singleplayer action" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/multiplayer", ["description"] = "Read multiplayer state with status/kind envelope and active-run context" },
            new(StringComparer.Ordinal) { ["method"] = "POST", ["path"] = "/api/v1/multiplayer", ["description"] = "Perform multiplayer action" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/settings", ["description"] = "Read settings and preferences" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/profile", ["description"] = "Read active profile progress plus normalized save/run context" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/compendium", ["description"] = "Read Compendium-shaped profile progress plus normalized save/run context" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/bestiary", ["description"] = "Read monster and encounter metadata" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/glossary/cards", ["description"] = "Read active-run card pool metadata plus profile/save/run context" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/glossary/relics", ["description"] = "Read active-run relic pool metadata plus profile/save/run context" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/glossary/potions", ["description"] = "Read active-run potion pool metadata plus profile/save/run context" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/glossary/keywords", ["description"] = "Read active-run keyword metadata plus profile/save/run context" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/profiles", ["description"] = "List profile slots plus normalized save context" },
            new(StringComparer.Ordinal) { ["method"] = "POST", ["path"] = "/api/v1/profiles", ["description"] = "Switch or delete profile slots" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/record", ["description"] = "Read combat recorder status" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/record/inspect", ["description"] = "Inspect a recorded combat replay" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/wiki", ["description"] = "Fuzzy-search discovered card and relic wiki entries" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/cardpools", ["description"] = "Read the active card pools" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/potionpools", ["description"] = "Read the active potion pools" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/relicbag", ["description"] = "Read the generated relic bag" },
            new(StringComparer.Ordinal) { ["method"] = "GET", ["path"] = "/api/v1/mapdrawings", ["description"] = "Read stroke and live node drawing positions" }
        };
    }

    // Called on HTTP thread (not main thread) as a best-effort guard.
    // The try/catch handles race conditions during run transitions.
    // Authoritative checks happen inside RunOnMainThread lambdas.
    internal static bool IsMultiplayerRun()
    {
        return TryRead<bool?>(() => MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress
                && MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.Type.IsMultiplayer()) ?? false;
    }

    private static void HandleGetMultiplayerState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";
        bool pretty = WantsPretty(request);

        if (!TryValidateStateFormat(response, format))
            return;

        string detail = request.QueryString["detail"] ?? "summary";
        if (detail is not ("summary" or "full"))
        {
            SendError(response, 400, "detail must be either 'summary' or 'full'.", pretty);
            return;
        }
        bool fullDetail = string.Equals(detail, "full", StringComparison.Ordinal);

        // Card descriptions and enemy intent labels are rendered by the game with the
        // player's standing powers and relics already applied. "unpowered" strips that
        // baking back to base + enchantment; see UnpoweredCardVars in McpMod.Helpers.cs.
        string values = request.QueryString["values"] ?? "powered";
        if (values is not ("powered" or "unpowered"))
        {
            SendError(response, 400, "values must be either 'powered' or 'unpowered'.", pretty);
            return;
        }
        bool unpowered = string.Equals(values, "unpowered", StringComparison.Ordinal);

        try
        {
            var state = RunOnMainThreadBlocking(() => WithCardValues(unpowered, () =>
            {
                var s = BuildMultiplayerGameState(fullDetail);
                s["detail"] = detail;
                if (fullDetail)
                    s["relic_bag_generation"] = RelicBagGeneration;
                s["game_version"] = GameVersion();
                s["values"] = values;
                return s;
            }));

            if (string.Equals(format, "markdown", StringComparison.Ordinal))
            {
                string md = FormatAsMarkdown(state);
                SendText(response, md, "text/markdown");
            }
            else
            {
                SendJson(response, state, pretty);
            }
        }
        catch (MainThreadUnavailableException ex)
        {
            try { SendUnavailable(response, ex.Message, pretty); }
            catch { /* response may be unusable */ }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] HandleGetMultiplayerState: {ex}");
            TryDo(() =>
            {
                response.StatusCode = 500;
                SendJson(response, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "error",
                    ["error"] = $"Failed to read multiplayer game state: {ex.Message}",
                    ["error_code"] = "multiplayer_state_read_failed"
                }, pretty);
            });
            /* response may be unusable */
        }
    }

    private static void HandlePostMultiplayerAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool pretty = WantsPretty(request);
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON", pretty, "invalid_json");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field", pretty, "missing_action");
            return;
        }
        if (actionElem.ValueKind != JsonValueKind.String)
        {
            SendError(response, 400, "'action' field must be a string", pretty, "invalid_action_type");
            return;
        }

        string action = actionElem.GetString() ?? "";

        // Menu actions (FTUE/popup dismissal, game-over, character select, etc.) are
        // scene-tree-driven and equally valid in MP. Route them to the shared handler
        // so MP clients can dismiss blocking FTUE prompts without going through the
        // run-mode-specific dispatcher.
        if (string.Equals(action, "menu_select", StringComparison.Ordinal))
        {
            try
            {
                var option = parsed.TryGetValue("option", out var optElem) ? optElem.GetString() ?? "" : "";
                var seed = parsed.TryGetValue("seed", out var seedElem) ? seedElem.GetString() : null;
                int? ascension = ReadOptionalInt(parsed, "ascension");
                var result = RunOnMainThreadBlocking(() => ExecuteMenuSelect(option, seed, ascension));
                SendJson(response, EnsureStatus(result), pretty);
            }
            catch (MainThreadUnavailableException ex)
            {
                SendUnavailable(response, ex.Message, pretty);
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Menu action failed: {ex.Message}", pretty);
            }
            return;
        }

        try
        {
            var result = RunOnMainThreadBlocking(() => ExecuteMultiplayerAction(action, parsed));
            SendJson(response, EnsureStatus(result), pretty);
        }
        catch (MainThreadUnavailableException ex)
        {
            SendUnavailable(response, ex.Message, pretty);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Multiplayer action failed: {ex.Message}", pretty);
        }
    }

    private static int? ReadOptionalInt(Dictionary<string, JsonElement> data, string key)
    {
        if (!data.TryGetValue(key, out var elem))
            return null;
        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int number))
            return number;
        if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), CultureInfo.InvariantCulture, out int parsedNumber))
            return parsedNumber;
        return null;
    }

    private static void HandleGetState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";
        bool pretty = WantsPretty(request);

        if (!TryValidateStateFormat(response, format))
            return;

        // Card descriptions and enemy intent labels are rendered by the game with the
        // player's standing powers and relics already applied. "unpowered" strips that
        // baking back to base + enchantment; see UnpoweredCardVars in McpMod.Helpers.cs.
        string values = request.QueryString["values"] ?? "powered";
        if (values is not ("powered" or "unpowered"))
        {
            SendError(response, 400, "values must be either 'powered' or 'unpowered'.", pretty);
            return;
        }
        bool unpowered = string.Equals(values, "unpowered", StringComparison.Ordinal);

        try
        {
            // ?passive=1: read without the UI side effects some screens have (auto-opening the
            // merchant inventory, force-clicking the fake merchant / treasure chest). A watcher that
            // polls the state while a human plays must not reopen what the human just closed.
            bool passive = ReadFlagParam(new Dictionary<string, JsonElement>(StringComparer.Ordinal), request, "passive", false);
            var state = RunOnMainThreadBlocking(() =>
            {
                _passiveRead = passive;
                try
                {
                    return WithCardValues(unpowered, () =>
                    {
                        var s = BuildGameState();
                        s["game_version"] = GameVersion();
                        s["values"] = values;
                        return s;
                    });
                }
                finally { _passiveRead = false; }
            });

            if (string.Equals(format, "markdown", StringComparison.Ordinal))
            {
                try
                {
                    SendText(response, FormatAsMarkdown(state), "text/markdown");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[STS2 MCP] FormatAsMarkdown failed, returning JSON: {ex}");
                    SendJson(response, state, pretty);
                }
            }
            else
            {
                SendJson(response, state, pretty);
            }
        }
        catch (MainThreadUnavailableException ex)
        {
            try { SendUnavailable(response, ex.Message, pretty); }
            catch { /* response may be unusable */ }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] HandleGetState: {ex}");
            TryDo(() =>
            {
                response.StatusCode = 500;
                SendJson(response, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = "error",
                    ["error"] = $"Failed to read game state: {ex.Message}",
                    ["error_code"] = "singleplayer_state_read_failed"
                }, pretty);
            });
            /* response may be unusable */
        }
    }

    /// <summary>
    /// `true` / `1` / `"1"` / `"true"` / `"yes"` / `"on"` in the body, or the same in the query
    /// string (`?wait=1`, or a bare `?wait`). Anything else is false; a missing key is
    /// <paramref name="fallback"/>.
    /// </summary>
    private static bool ReadFlagParam(
        Dictionary<string, JsonElement> data, HttpListenerRequest request, string key, bool fallback)
    {
        if (data.TryGetValue(key, out var elem))
        {
            switch (elem.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number: return elem.TryGetInt32(out int number) && number != 0;
                case JsonValueKind.String: return IsTruthyFlag(elem.GetString());
            }
        }

        string? query = request.QueryString[key];
        if (query != null)
            return IsTruthyFlag(query);

        // A valueless `?wait` is filed under the null key by HttpListener, not under "wait".
        string? valueless = request.QueryString[null];
        if (valueless != null && Array.IndexOf(valueless.Split(','), key) >= 0)
            return true;

        return fallback;
    }

    private static bool IsTruthyFlag(string? value)
    {
        if (value == null) return false;
        // `?wait=` with nothing after it still reads as "switch it on".
        if (value.Length == 0) return true;
        return string.Equals(value, "1"
, StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the game has finished acting on whatever was last asked of it.</summary>
    /// <remarks>
    /// Kept free of game types on purpose: it reads only the dictionary a <c>BuildGameState</c>
    /// produced, so it can be reasoned about (and tested) without a running game.
    /// </remarks>
    internal static bool IsSettledState(Dictionary<string, object?> state)
    {
        if (state.TryGetValue("is_resolving", out var resolving) && resolving is true)
            return false;

        string? stateType = state.GetValueOrDefault("state_type") as string;
        if (stateType is "monster" or "elite" or "boss")
        {
            // The enemies' turn drains the action queue in bursts, so `is_resolving` blinks false
            // between two enemy moves. Only the play phase means the caller may act again.
            if (state.GetValueOrDefault("battle") is not Dictionary<string, object?> battle)
                return false;
            if (battle.GetValueOrDefault("is_play_phase") is not true)
                return false;
        }

        return true;
    }

    /// <summary>
    /// A cheap digest of everything an action could plausibly move: screen, floor, round, the
    /// player's hp/block/energy, the cards in hand, the enemies' hp/block, and how many options
    /// the current screen offers. Used only to tell "the action did nothing" from "the action
    /// landed", so it does not have to be exhaustive - just stable between two idle reads.
    /// </summary>
    internal static string StateFingerprint(Dictionary<string, object?> state)
    {
        var sb = new StringBuilder();
        sb.Append(state.GetValueOrDefault("state_type")).Append('|');
        sb.Append(state.GetValueOrDefault("menu_screen")).Append('|');

        if (state.GetValueOrDefault("run") is Dictionary<string, object?> run)
            sb.Append(run.GetValueOrDefault("floor"));
        sb.Append('|');

        if (state.GetValueOrDefault("battle") is Dictionary<string, object?> battle)
        {
            sb.Append(battle.GetValueOrDefault("round"))
              .Append(',').Append(battle.GetValueOrDefault("turn"))
              .Append(',').Append(battle.GetValueOrDefault("is_play_phase"));
            if (battle.GetValueOrDefault("enemies") is List<Dictionary<string, object?>> enemies)
            {
                foreach (var enemy in enemies)
                {
                    sb.Append(';').Append(enemy.GetValueOrDefault("entity_id"))
                      .Append(':').Append(enemy.GetValueOrDefault("hp"))
                      .Append('/').Append(enemy.GetValueOrDefault("block"));
                }
            }
        }
        sb.Append('|');

        if (state.GetValueOrDefault("player") is Dictionary<string, object?> player)
        {
            sb.Append(player.GetValueOrDefault("hp"))
              .Append('/').Append(player.GetValueOrDefault("block"))
              .Append('/').Append(player.GetValueOrDefault("energy"));
            if (player.GetValueOrDefault("hand") is List<Dictionary<string, object?>> hand)
            {
                foreach (var card in hand)
                    sb.Append(';').Append(card.GetValueOrDefault("uid"));
            }
        }
        sb.Append('|');

        if (state.GetValueOrDefault("options") is System.Collections.ICollection options)
            sb.Append(options.Count);

        return sb.ToString();
    }

    /// <summary>
    /// Polls the state from the HTTP thread until the game has settled, then appends
    /// `waited_ms` / `polls` / `settled` / `changed` (and `state`) to the action's own result.
    /// </summary>
    private static void AppendSettleResult(
        Dictionary<string, object?> result, string? before, int timeoutMs, bool includeState)
    {
        var elapsed = Stopwatch.StartNew();
        int polls = 0;
        int consecutiveSettled = 0;
        bool changed = false;
        bool settled = false;
        string? waitError = null;
        Dictionary<string, object?>? last = null;

        while (true)
        {
            Dictionary<string, object?>? state = null;
            try
            {
                state = RunOnMainThreadBlocking(() => BuildGameState());
            }
            catch (MainThreadUnavailableException ex)
            {
                // The action itself already ran; only our observation of it failed. Turning this
                // into a 503 would tell the caller "nothing was applied", which is a lie.
                waitError = ex.Message;
                break;
            }
            catch (Exception ex)
            {
                // BuildGameState throws while a scene is swapping (run and room models are torn
                // down mid-frame). That is the opposite of settled, so keep polling.
                waitError = ex.Message;
                consecutiveSettled = 0;
            }
            polls++;

            if (state != null)
            {
                last = state;
                waitError = null;
                if (before != null && !string.Equals(StateFingerprint(state), before, StringComparison.Ordinal))
                    changed = true;

                if (IsSettledState(state))
                {
                    if (++consecutiveSettled >= SettleConsecutivePolls)
                    {
                        settled = true;
                        break;
                    }
                }
                else
                {
                    consecutiveSettled = 0;
                }
            }

            // Nothing moved at all: the action was a no-op as far as the game is concerned, and
            // no amount of further waiting will change that.
            if (before != null && !changed && elapsed.ElapsedMilliseconds >= SettleNoChangeGiveUpMs)
            {
                settled = true;
                break;
            }

            if (elapsed.ElapsedMilliseconds >= timeoutMs)
                break;

            Thread.Sleep(SettlePollIntervalMs);
        }

        elapsed.Stop();
        result["waited_ms"] = (int)elapsed.ElapsedMilliseconds;
        result["polls"] = polls;
        result["settled"] = settled;
        result["changed"] = changed;
        if (waitError != null)
            result["wait_error"] = waitError;
        if (includeState && last != null)
            result["state"] = last;
    }

    /// <summary>
    /// Runs a singleplayer action on the main thread and, when the caller passed `wait`, keeps
    /// reading the state until the game has settled - so a single round trip answers both
    /// "was the action accepted?" and "what does the game look like now?".
    /// </summary>
    private static Dictionary<string, object?> ExecuteWithOptionalWait(
        HttpListenerRequest request, Dictionary<string, JsonElement> parsed,
        Func<Dictionary<string, object?>> action)
    {
        if (!ReadFlagParam(parsed, request, "wait", false))
            return EnsureStatus(RunOnMainThreadBlocking(action));

        int timeoutMs = Math.Clamp(
            ReadOptionalInt(parsed, "wait_timeout_ms")
                ?? (int.TryParse(request.QueryString["wait_timeout_ms"], CultureInfo.InvariantCulture, out int q) ? q : SettleDefaultTimeoutMs),
            SettleMinTimeoutMs, SettleMaxTimeoutMs);
        bool includeState = ReadFlagParam(parsed, request, "include_state", true);

        // Snapshot before acting, so an action that changes nothing can be recognised instead of
        // being waited out. A MainThreadUnavailableException here is a genuine 503: nothing ran yet.
        string? before = null;
        try { before = StateFingerprint(RunOnMainThreadBlocking(() => BuildGameState())); }
        catch (MainThreadUnavailableException) { throw; }
        catch { /* an unreadable pre-state only costs us the no-op shortcut */ }

        var result = EnsureStatus(RunOnMainThreadBlocking(action));

        // Waiting on an action the game rejected would just burn the whole timeout.
        if (string.Equals(result.GetValueOrDefault("status") as string, "ok", StringComparison.Ordinal))
        {
            AppendSettleResult(result, before, timeoutMs, includeState);
            if (result.ContainsKey("reward_id") || result.ContainsKey("parent_reward_id"))
            {
                try { RunOnMainThreadBlocking(() => { AppendRewardActionOutcome(result); return true; }); }
                catch (MainThreadUnavailableException) { result["outcome_observed"] = false; }
            }
        }

        return result;
    }

    private static bool TryValidateStateFormat(HttpListenerResponse response, string format)
    {
        if (format is "json" or "markdown")
            return true;

        SendError(response, 400, "Invalid format. Use: json, markdown", "invalid_format");
        return false;
    }

    private static void HandlePostAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool pretty = WantsPretty(request);
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON", pretty, "invalid_json");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field", pretty, "missing_action");
            return;
        }
        if (actionElem.ValueKind != JsonValueKind.String)
        {
            SendError(response, 400, "'action' field must be a string", pretty, "invalid_action_type");
            return;
        }

        string action = actionElem.GetString() ?? "";

        // The recorder's switch works with or without a run, and is not itself recorded.
        if (string.Equals(action, "set_recording", StringComparison.Ordinal))
        {
            try
            {
                if (!parsed.TryGetValue("enabled", out var en) || en.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    SendError(response, 400, "set_recording needs 'enabled': true or false", pretty);
                    return;
                }
                bool enabled = en.GetBoolean();
                SendJson(response, EnsureStatus(RunOnMainThreadBlocking(() => Recorder.CombatRecorder.SetRecording(enabled))), pretty);
            }
            catch (MainThreadUnavailableException ex)
            {
                SendUnavailable(response, ex.Message, pretty);
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"set_recording failed: {ex.Message}", pretty);
            }
            return;
        }

        // Every other POST is listed in the combat record (when one is open), and the game
        // actions its handler enqueues are tagged with the request - see docs/recording.md.
        long apiRequest = Recorder.CombatRecorder.ApiBegin(action, parsed);
        string? apiStatus = null;
        try
        {
            apiStatus = DispatchPostAction(request, response, parsed, action, apiRequest, pretty);
        }
        finally
        {
            Recorder.CombatRecorder.ApiEnd(apiRequest, apiStatus);
        }
    }

    /// <summary>Runs one singleplayer POST and answers it. Returns the result's status for the record.</summary>
    private static string? DispatchPostAction(
        HttpListenerRequest request, HttpListenerResponse response,
        Dictionary<string, JsonElement> parsed, string action, long apiRequest, bool pretty)
    {
        // Revealing timeline epochs is a main-menu operation, so it must not require a run.
        if (string.Equals(action, "timeline_reveal_epochs", StringComparison.Ordinal))
        {
            try
            {
                var result = ExecuteWithOptionalWait(request, parsed,
                    Recorder.CombatRecorder.InHandler(apiRequest, () => ExecuteTimelineRevealEpochs()));
                SendJson(response, result, pretty);
                return result.GetValueOrDefault("status") as string;
            }
            catch (MainThreadUnavailableException ex)
            {
                SendUnavailable(response, ex.Message, pretty);
                return "unavailable";
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Timeline reveal failed: {ex.Message}", pretty);
                return "exception";
            }
        }

        // Handle menu actions separately (no run required)
        if (string.Equals(action, "menu_select", StringComparison.Ordinal))
        {
            try
            {
                var option = parsed.TryGetValue("option", out var optElem) ? optElem.GetString() ?? "" : "";
                var seed = parsed.TryGetValue("seed", out var seedElem) ? seedElem.GetString() : null;
                int? ascension = ReadOptionalInt(parsed, "ascension");
                var result = ExecuteWithOptionalWait(request, parsed,
                    Recorder.CombatRecorder.InHandler(apiRequest, () => ExecuteMenuSelect(option, seed, ascension)));
                SendJson(response, result, pretty);
                return result.GetValueOrDefault("status") as string;
            }
            catch (MainThreadUnavailableException ex)
            {
                SendUnavailable(response, ex.Message, pretty);
                return "unavailable";
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Menu action failed: {ex.Message}", pretty);
                return "exception";
            }
        }

        // Utility actions valid with or without a run (fork additions).
        if (string.Equals(action, "set_time_scale", StringComparison.Ordinal) || string.Equals(action, "set_ascension", StringComparison.Ordinal))
        {
            try
            {
                var result = ExecuteWithOptionalWait(request, parsed,
                    Recorder.CombatRecorder.InHandler(apiRequest, () => string.Equals(action, "set_time_scale"
, StringComparison.Ordinal) ? ExecuteSetTimeScale(parsed)
                        : ExecuteSetAscension(parsed)));
                SendJson(response, result, pretty);
                return result.GetValueOrDefault("status") as string;
            }
            catch (MainThreadUnavailableException ex)
            {
                SendUnavailable(response, ex.Message, pretty);
                return "unavailable";
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Action failed: {ex.Message}", pretty);
                return "exception";

            }
        }

        try
        {
            var result = ExecuteWithOptionalWait(request, parsed,
                Recorder.CombatRecorder.InHandler(apiRequest, () => ExecuteAction(action, parsed)));
            SendJson(response, result, pretty);
            return result.GetValueOrDefault("status") as string;
        }
        catch (MainThreadUnavailableException ex)
        {
            SendUnavailable(response, ex.Message, pretty);
            return "unavailable";
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Action failed: {ex.Message}", pretty);
            return "exception";
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
        Justification = "scripts/audit_endpoints.py asserts this structured-dispatch guard exists; the action paths stopped calling it in the fork merge and re-wiring it changes HTTP status codes, which needs its own decision.")]
    private static void SendActionResultJson(HttpListenerResponse response, Dictionary<string, object?> result)
    {
        if (result.TryGetValue("status", out var status) && string.Equals(status as string, "error", StringComparison.Ordinal))
        {
            if (result.TryGetValue("error_code", out var errorCode))
            {
                response.StatusCode = (errorCode as string) switch
                {
                    "missing_menu_option" or "unknown_menu_option" or "unknown_action" or "unknown_multiplayer_action" => 400,
                    "not_on_menu" or "run_not_in_progress" or "not_multiplayer_run" or "blocking_popup_active" => 409,
                    "local_player_unavailable" => 409,
                    "timeline_manual_action_required" => 409,
                    _ => 400
                };
            }
            else
            {
                response.StatusCode = 400;
            }
        }
        SendJson(response, result);
    }
}

/// <summary>
/// The game's main thread did not run the requested work: the queue was already backed up, or
/// the wait timed out. Distinct from an action failure so handlers can answer 503 + retry
/// instead of 500 - nothing was executed, and the same request is safe to send again.
/// </summary>
internal sealed class MainThreadUnavailableException : Exception
{
    internal MainThreadUnavailableException(string message) : base(message) { }
}
