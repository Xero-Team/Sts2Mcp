using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using STS2_MCP.Core.Contracts;

namespace STS2_MCP;

public static partial class McpMod
{
    // STS2 v0.107 removed CombatManager.IsPlayPhase. The turn phase now lives per-player on
    // PlayerCombatState.Phase, so these helpers rebuild the old semantics: we're in the play
    // phase when combat is running, it's the player side's turn, and the player is in Play.
    internal static bool IsPlayPhase(MegaCrit.Sts2.Core.Entities.Players.Player? player)
    {
        if (!IsPlayerSideTurn()) return false;
        return player?.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play;
    }

    // State-report variant: true when any player is still in the Play phase. In singleplayer
    // this is the local player; in co-op it means at least one player can still act.
    internal static bool IsPlayPhase(MegaCrit.Sts2.Core.Combat.ICombatState? combatState)
    {
        if (!IsPlayerSideTurn() || combatState == null) return false;
        foreach (var player in combatState.Players)
        {
            if (player?.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play)
                return true;
        }
        return false;
    }

    private static bool IsPlayerSideTurn()
    {
        var manager = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
        if (manager is not { IsInProgress: true }) return false;
        return manager.DebugOnlyGetState()?.CurrentSide == MegaCrit.Sts2.Core.Combat.CombatSide.Player;
    }

    private static CardModel? SafeBuildUpgradedCardPreview(CardModel card)
    {
        if (!card.IsUpgradable)
            return null;

        // A card model can refuse to clone (an unmodelled card type, a mid-combat
        // mutation), and the preview is a nicety: report it and let the caller omit it.
        return TryRead("card.upgrade_preview", () =>
        {
            var preview = card.IsMutable
                ? (CardModel)card.MutableClone()
                : card.ToMutable();
            preview.UpgradeInternal();
            return preview;
        });
    }

    private static string? SafeGetCardUpgradePreviewDescription(CardModel card, PileType pile = PileType.Hand)
    {
        var preview = SafeBuildUpgradedCardPreview(card);
        if (preview != null)
            return SafeGetCardDescription(preview, pile);

        return card.IsUpgradable
            ? SafeGetText(() => card.GetDescriptionForUpgradePreview())?.Replace("\n", " ")
            : null;
    }

    // The caller's name stands in for a field name: this helper reads a few hundred
    // different things per state and "text: NullReferenceException" alone names none of
    // them. Warnings are de-duplicated, so the volume of calls costs at most one entry.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A game member that vanished must not kill a response; the failure is recorded in McpMod.Warnings.")]
    internal static string? SafeGetText(Func<object?> getter,
        [CallerMemberName] string caller = "")
    {
        try
        {
            var result = getter();
            if (result == null) return null;
            // If it's a LocString, call GetFormattedText
            if (result is MegaCrit.Sts2.Core.Localization.LocString locString)
                return StripRichTextTags(locString.GetFormattedText());
            return result.ToString();
        }
        catch (Exception ex) { Warn($"text in {caller}", ex); return null; }
    }

    /// <summary>
    /// Reads one value whose source may throw because the game member was renamed or removed.
    /// The exception is recorded in <see cref="McpMod.Warnings"/> and the caller gets
    /// <c>default</c>, which is the same "absent, not guessed" meaning the state contract uses.
    /// Callers must not wrap this in another try/catch: the whole point is that the catch lives
    /// here, once, instead of at every read.
    /// </summary>
    internal static T? TryRead<T>(Func<T> read,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => TryRead($"{member}:{line}", read);

    /// <summary>The same read with a hand-written context, for a builder whose field name is
    /// more useful to a support report than the method it happens to live in.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The mod's read path is a compatibility boundary; a renamed game member is expected, not exceptional.")]
    internal static T? TryRead<T>(string context, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Warn(context, ex);
            return default;
        }
    }

    /// <summary>
    /// <see cref="TryRead{T}"/> for a read whose only outcome the caller needs is "it worked".
    /// Returns false when the action threw, so the caller can <c>continue</c> or fall back.
    /// </summary>
    internal static bool TryDo(Action action,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => TryDo($"{member}:{line}", action);

    /// <summary>The same action with a hand-written context.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The mod's read path is a compatibility boundary; a renamed game member is expected, not exceptional.")]
    internal static bool TryDo(string context, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Warn(context, ex);
            return false;
        }
    }

    /// <summary>
    /// <see cref="TryRead{T}(string, Func{T})"/> for the one failure that must not be
    /// treated as "absent": a backed-up or stalled main thread means the work was not
    /// done, and the HTTP layer has to answer 503 instead of guessing a value.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The mod's read path is a compatibility boundary; only the one failure that carries its own meaning is rethrown.")]
    internal static T? TryReadUnless<T, TException>(string context, Func<T> read)
        where TException : Exception
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not TException)
        {
            Warn(context, ex);
            return default;
        }
    }

    /// <summary>
    /// A read whose failure is normal rather than broken: a Godot node freed while a scene
    /// transitions, or a candidate member name this game version does not have. Returns
    /// default and records nothing - a search loop would otherwise report dozens of
    /// non-problems per state read, and the state contract already means "absent, not
    /// guessed" when a field is missing.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Godot throws ObjectDisposedException from a freed node and reflection throws a different type per model shape; at these call sites both mean 'not there'.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "ERP022:Swallowed exception",
        Justification = "Recording the same 'not there' for every candidate name would report dozens of non-problems per state read.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2486:Exceptions should not be ignored",
        Justification = "Same as ERP022: the call sites read a member that may legitimately be absent.")]
    internal static T? TryReadQuiet<T>(Func<T> read)
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

    /// <summary>
    /// <see cref="TryDo"/> with <see cref="TryReadQuiet{T}"/>'s meaning: the call is
    /// expected to fail in normal use (restoring input on a screen that is already torn
    /// down, scrolling a list that was just reparented) and nothing is recorded.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "These are best-effort UI calls whose failure changes nothing the caller acts on.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "ERP022:Swallowed exception",
        Justification = "Reporting every teardown race would drown the warnings a state read actually needs.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2486:Exceptions should not be ignored",
        Justification = "Same as ERP022: a UI call that races a scene change is not a compatibility break.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2737:Catch clauses should do more than rethrow",
        Justification = "Quiet by design: the catch is the entire behavior, and the comment above states why.")]
    internal static void TryDoQuiet(Action action)
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

    /// <summary>
    /// Runs an action whose failure the client should see as an error result rather than
    /// an exception. Returns null when the call succeeded, otherwise the failure message.
    /// The failure is recorded once under <paramref name="context"/>, like a state read.
    /// </summary>
    /// <remarks>
    /// The message is unwrapped from the reflection wrapper first: a game call reached
    /// through <c>MethodInfo.Invoke</c> otherwise reports "Exception has been thrown by
    /// the target of an invocation" and drops the only useful part.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "An action that presses a game button can throw anything the game throws; mapping it to an error response is the boundary's job.")]
    internal static string? TryAction(string context, Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            Warn(context, ex);
            return DescribeFailure(ex);
        }
    }

    /// <summary>
    /// <see cref="TryAction"/> for a body that produces the response itself. When the body
    /// throws, <paramref name="failure"/> decides the response: a screen torn down mid-call
    /// is a retry, not a 500, and only the call site can tell them apart.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Same boundary as TryAction: the call site maps the failure to a response it can describe.")]
    internal static Dictionary<string, object?> RunAction(
        string context,
        Func<Dictionary<string, object?>> body,
        Func<Exception, Dictionary<string, object?>> failure)
    {
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            Warn(context, ex);
            return failure(ex);
        }
    }

    /// <summary>The one-line form of an exception for a client-facing message.</summary>
    internal static string DescribeFailure(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException { InnerException: { } inner })
            ex = inner;

        return ex.Message.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    internal static string StripRichTextTags(string text)
    {
        // Remove BBCode-style tags like [color=red], [/color], etc.
        // Special case: [img]res://path/to/file.png[/img] → [file.png]
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                // Check for [img]...[/img] pattern
                if (text.AsSpan(i).StartsWith("[img]"))
                {
                    int contentStart = i + 5; // length of "[img]"
                    int closeTag = text.IndexOf("[/img]", contentStart, StringComparison.Ordinal);
                    if (closeTag >= 0)
                    {
                        string path = text[contentStart..closeTag];
                        int lastSlash = path.LastIndexOf('/');
                        string filename = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
                        sb.Append('[').Append(filename).Append(']');
                        i = closeTag + 6; // length of "[/img]"
                        continue;
                    }
                }

                int end = text.IndexOf(']', i);
                if (end >= 0) { i = end + 1; continue; }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    // The preference is passed down from the request rather than kept in a thread-static or
    // global flag: requests are served concurrently on ThreadPool threads.
    internal static bool WantsPretty(HttpListenerRequest request)
    {
        var query = request.QueryString;
        string? value = query["pretty"];
        if (value == null)
        {
            // "?pretty" without "=" has no name, so NameValueCollection files it under the
            // null key, joining several such parameters with commas.
            string? valueless = query[null];
            if (valueless == null)
                return false;
            return valueless.Split(',').Any(part => part.Trim().Equals("pretty", StringComparison.OrdinalIgnoreCase));
        }

        return value.Length == 0
            || value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    internal static void SendJson(HttpListenerResponse response, object data, bool pretty = false)
    {
        string json = JsonSerializer.Serialize(data, pretty ? ContractJson.PrettyOptions : ContractJson.Options);
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    internal static void SendText(HttpListenerResponse response, string text, string contentType = "text/plain")
    {
        byte[] buffer = Encoding.UTF8.GetBytes(text);
        response.ContentType = $"{contentType}; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    internal static void SendError(
        HttpListenerResponse response, int statusCode, string message, bool pretty = false,
        string? errorCode = null)
    {
        response.StatusCode = statusCode;
        // "status" is carried here too so a client never has to branch on the HTTP code:
        // transport-level failures (bad JSON, unknown route, wrong method) read exactly
        // like the action-level failures produced by Error().
        SendJson(response, new ErrorResponse
        {
            Error = message,
            ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode
        }, pretty);
    }

    /// <summary>
    /// Error-code-first shorthand kept for the fork's call sites
    /// (<c>SendError(response, 409, "...", "run_not_in_progress")</c>). Both overloads
    /// share the implementation above, so the wire shape is identical.
    /// </summary>
    internal static void SendError(
        HttpListenerResponse response, int statusCode, string message, string errorCode)
        => SendError(response, statusCode, message, false, errorCode);

    /// <summary>
    /// 503 for "the game's main thread never picked this up". `retry: true` is the contract:
    /// the request was not executed, so a client may safely send it again - unlike a 500, where
    /// the action ran and failed.
    /// </summary>
    internal static void SendUnavailable(
        HttpListenerResponse response, string message, bool pretty = false)
    {
        response.StatusCode = 503;
        SendJson(response, new UnavailableResponse { Error = message }, pretty);
    }

    /// <summary>
    /// Runs one request handler and maps its two failure modes onto the wire contract:
    /// a main thread that never picked the work up becomes a retryable 503, anything
    /// else a 500 carrying the handler's own error code.
    /// </summary>
    /// <remarks>
    /// Read endpoints all fail the same way, so the two catch clauses live here instead
    /// of being repeated at each of them. A handler that needs a different status (an
    /// action that returns 400/409 for a rejected payload, say) should keep its own
    /// try/catch - this wrapper is for the "build and send" shape only.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "This is the HTTP boundary: no exception from a builder may escape and kill the listener loop.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "EPC12:Suspicious exception handling",
        Justification = "The response body carries the message; the Godot log line above it carries the type and the trace.")]
    internal static void RunHandler(
        HttpListenerResponse response, Action handler, string failurePrefix,
        string? failureCode = null, bool pretty = false)
    {
        try
        {
            handler();
        }
        catch (MainThreadUnavailableException ex)
        {
            TryDo(() => SendUnavailable(response, ex.Message, pretty));
        }
        catch (Exception ex)
        {
            // The body carries only the message; the log keeps the type and the trace.
            GD.PrintErr($"[STS2 MCP] {failurePrefix}: {ex}");
            TryDo(() => SendError(response, 500, $"{failurePrefix}: {ex.Message}", pretty, failureCode));
        }
    }

    /// <summary>
    /// <see cref="RunHandler"/> for an action dispatch branch, which also reports what the
    /// recorder should store: the handler's own status, "unavailable" for a stalled main
    /// thread, or "exception" for a failure.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "This is the HTTP boundary: no exception from an action executor may escape and kill the listener loop.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "EPC12:Suspicious exception handling",
        Justification = "The response body carries the message; the Godot log line above it carries the type and the trace.")]
    internal static string? RunActionHandler(
        HttpListenerResponse response, Func<string?> handler, string failurePrefix, bool pretty = false)
    {
        try
        {
            return handler();
        }
        catch (MainThreadUnavailableException ex)
        {
            TryDo(() => SendUnavailable(response, ex.Message, pretty));
            return "unavailable";
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] {failurePrefix}: {ex}");
            TryDo(() => SendError(response, 500, $"{failurePrefix}: {ex.Message}", pretty));
            return "exception";
        }
    }

    internal static void SendReadResultJson(HttpListenerResponse response, object result, bool pretty = false)
    {
        if (result is Dictionary<string, object?> data
            && data.TryGetValue("status", out var status)
            && string.Equals(status as string, "error", StringComparison.Ordinal))
        {
            response.StatusCode = data.TryGetValue("error_code", out var errorCode)
                ? (errorCode as string) switch
                {
                    "save_manager_unavailable" or "settings_data_unavailable" or "profile_data_unavailable" => 503,
                    _ => 500
                }
                : 500;
        }

        SendJson(response, result, pretty);
    }

    private static bool IsCombatPlayPhase()
    {
        var manager = CombatManager.Instance;
        if (manager == null || !manager.IsInProgress)
            return false;

        // STS2 0.107 removed the compiled IsPlayPhase accessor, so read it by name when it
        // is there, then fall back to the side the state reports, then to the action queue.
        var property = TryRead(() => manager.GetType().GetProperty(
            "IsPlayPhase",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic));
        if (property?.PropertyType == typeof(bool)
            && TryRead(() => property.GetValue(manager)) is bool isPlayPhase)
            return isPlayPhase;

        var currentSide = TryRead(() => manager.DebugOnlyGetState()?.CurrentSide.ToString());
        if (!string.IsNullOrEmpty(currentSide))
            return currentSide.Contains("player", StringComparison.OrdinalIgnoreCase);

        return TryRead<bool?>(() => !manager.PlayerActionsDisabled) ?? false;
    }

    private static MerchantInventory? GetMerchantInventory(object? owner)
    {
        return TryGetMerchantInventory(owner) ??
               TryGetMerchantInventory(NMerchantRoom.Instance) ??
               TryGetMerchantInventory(NMerchantRoom.Instance?.Inventory);
    }

    private static MerchantInventory? TryGetMerchantInventory(object? source)
    {
        if (source == null)
            return null;
        if (source is MerchantInventory inventory)
            return inventory;

        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.DeclaredOnly;

        for (var type = source.GetType(); type != null; type = type.BaseType)
        {
            foreach (var property in type.GetProperties(Flags))
            {
                if (property.GetIndexParameters().Length != 0 ||
                    !typeof(MerchantInventory).IsAssignableFrom(property.PropertyType))
                    continue;

                if (TryReadQuiet(() => property.GetValue(source)) is MerchantInventory propertyInventory)
                    return propertyInventory;
            }

            foreach (var field in type.GetFields(Flags))
            {
                if (!typeof(MerchantInventory).IsAssignableFrom(field.FieldType))
                    continue;

                if (TryReadQuiet(() => field.GetValue(source)) is MerchantInventory fieldInventory)
                    return fieldInventory;
            }
        }

        return null;
    }

    /// <summary>
    /// Last line of defence for the POST contract: every action result must carry
    /// "status". A future action that forgets it would silently break clients that
    /// branch on that key alone, so the value is derived here from the presence of
    /// "error" rather than left missing.
    /// </summary>
    private static Dictionary<string, object?> EnsureStatus(Dictionary<string, object?> result)
    {
        if (!result.ContainsKey("status"))
            result["status"] = result.ContainsKey("error") ? "error" : "ok";
        return result;
    }

    private static Dictionary<string, object?> Error(
        string message,
        string? errorCode = null,
        [CallerFilePath] string callerFilePath = "")
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "error", ["error"] = message };
        if (string.IsNullOrWhiteSpace(errorCode)
            && (callerFilePath.EndsWith("McpMod.Actions.cs", StringComparison.Ordinal)
                || callerFilePath.EndsWith("McpMod.MultiplayerActions.cs", StringComparison.Ordinal)))
        {
            errorCode = "action_error";
        }
        if (!string.IsNullOrWhiteSpace(errorCode))
            data["error_code"] = errorCode;
        return data;
    }

    private static Dictionary<string, object?> WithStateEnvelope(Dictionary<string, object?> state, string kind)
    {
        state.TryAdd("status", "ok");
        state.TryAdd("kind", kind);
        return state;
    }

    private static string? NormalizePathForJson(string? path)
        => string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/');

    private static object? GetInstanceFieldValue(object source, string fieldName)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.DeclaredOnly;

        for (var type = source.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(fieldName, Flags);
            if (field != null)
                return field.GetValue(source);
        }

        return null;
    }

    private static object? GetInstanceMemberValue(object source, string memberName)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.DeclaredOnly;

        for (var type = source.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(memberName, Flags);
            if (property is { CanRead: true } && property.GetIndexParameters().Length == 0)
                return property.GetValue(source);

            var field = type.GetField(memberName, Flags);
            if (field != null)
                return field.GetValue(source);
        }

        return null;
    }

    private static void AddMenuOptionIfVisible(
        List<Dictionary<string, object?>> options,
        object owner,
        string fieldName,
        string label)
    {
        TryDo($"options.{label}", () =>
        {
            var btn = GetInstanceFieldValue(owner, fieldName);
            if (btn is Control ctrl && IsNodeVisible(ctrl))
            {
                var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = label,
                    ["enabled"] = isEnabled ?? true
                });
            }
        });
    }

    // ---- Grid card selection (NCardGridSelectionScreen and its subclasses) -------------
    //
    // Every subclass follows the same two-step shape: click cards in the grid, then a
    // *preview* panel opens and its own Confirm applies the selection. The screens do not
    // share a base member for any of it - each declares its own `_selectedCards` /
    // `_prefs` fields and names its preview container differently in the scene - so the
    // helpers below are the one place that knows all the spellings.
    //
    // Getting the preview container wrong is what A-7 was: the enchant screen's containers
    // were missing from the list, so `confirm_selection` fell through to the screen's main
    // Confirm, which on these screens means "open the preview" (PreviewSelection), not
    // "apply". Pressing it while the preview was already open re-ran PreviewSelection and
    // appended a second copy of every selected card to the preview row.

    /// <summary>
    /// Preview container node names across the NCardGridSelectionScreen subclasses:
    /// upgrade uses %Upgrade*PreviewContainer, enchant %Enchant*PreviewContainer, and
    /// remove/transform the generic %PreviewContainer. Only one is ever visible.
    /// </summary>
    /// <summary>
    /// A regex runs on the game's main thread, so a pattern over a game-supplied
    /// identifier must be bounded. Every pattern the mod uses is linear in the
    /// input; 250 ms is generous for a name and short enough that a pathological
    /// string cannot freeze a frame.
    /// </summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly string[] CardSelectPreviewContainerNames =
    {
        "%UpgradeSinglePreviewContainer",
        "%UpgradeMultiPreviewContainer",
        "%EnchantSinglePreviewContainer",
        "%EnchantMultiPreviewContainer",
        "%PreviewContainer",
    };

    /// <summary>The preview panel currently covering the grid, or null when the screen is
    /// still in its card-picking step.</summary>
    internal static Control? FindVisibleCardSelectPreview(NCardGridSelectionScreen screen)
    {
        foreach (var name in CardSelectPreviewContainerNames)
        {
            var container = TryReadQuiet(() => screen.GetNodeOrNull<Control>(name));
            if (container?.Visible == true)
                return container;
        }
        return null;
    }

    /// <summary>Every visible preview container, for the state's preview card list.</summary>
    internal static List<Control> FindCardSelectPreviewContainers(NCardGridSelectionScreen screen)
    {
        var found = new List<Control>();
        foreach (var name in CardSelectPreviewContainerNames)
        {
            TryDo(() =>
            {
                var container = screen.GetNodeOrNull<Control>(name);
                if (container?.Visible == true)
                    found.Add(container);
            });
        }
        return found;
    }

    /// <summary>The button that applies a previewed selection. Named "Confirm" in the
    /// upgrade/enchant/transform scenes and "%PreviewConfirm" in the remove scene.</summary>
    internal static NConfirmButton? GetCardSelectPreviewConfirm(Control container)
        => container.GetNodeOrNull<NConfirmButton>("Confirm")
           ?? container.GetNodeOrNull<NConfirmButton>("%PreviewConfirm");

    /// <summary>The button that drops a previewed selection and returns to the grid.</summary>
    internal static NBackButton? GetCardSelectPreviewCancel(Control container)
        => container.GetNodeOrNull<NBackButton>("Cancel")
           ?? container.GetNodeOrNull<NBackButton>("%PreviewCancel");

    /// <summary>The screen's own Confirm, below the grid. On most of these screens it
    /// opens the preview rather than applying anything.</summary>
    internal static NConfirmButton? GetCardSelectMainConfirm(NCardGridSelectionScreen screen)
        => screen.GetNodeOrNull<NConfirmButton>("Confirm")
           ?? screen.GetNodeOrNull<NConfirmButton>("%Confirm");

    /// <summary>
    /// The cards the screen currently has selected. Every subclass keeps them in its own
    /// private `_selectedCards` HashSet - the same set OnCardClicked toggles - so this is
    /// the game's own answer, not a guess reconstructed from highlights.
    /// Null means the field could not be read (a renamed field in a future game build).
    /// </summary>
    internal static HashSet<CardModel>? GetCardSelectSelection(NCardGridSelectionScreen screen)
    {
        return TryRead(() => GetInstanceFieldValue(screen, "_selectedCards") as HashSet<CardModel>);
    }

    /// <summary>
    /// How many cards the screen wants: `_prefs` carries MinSelect/MaxSelect, which is what
    /// the game itself checks before it will complete the selection.
    /// </summary>
    internal static CardSelectorPrefs? GetCardSelectPrefs(NCardGridSelectionScreen screen)
    {
        return TryRead(() => GetInstanceFieldValue(screen, "_prefs") as CardSelectorPrefs?);
    }

    internal static List<T> FindAll<T>(Node start) where T : Node
    {
        var list = new List<T>();
        if (IsLiveNode(start))
            FindAllRecursive(start, list);
        return list;
    }

    /// <summary>
    /// FindAll variant that sorts results by visual position (row-major: top-to-bottom, left-to-right).
    /// NGridCardHolder.OnFocus() calls MoveToFront() which scrambles child order for z-rendering.
    /// Sorting by GlobalPosition restores the correct visual order for both single-row (card rewards,
    /// choose-a-card) and multi-row (deck selection grids) layouts.
    /// </summary>
    internal static List<T> FindAllSortedByPosition<T>(Node start) where T : Control
    {
        var list = FindAll<T>(start);
        list.Sort((a, b) =>
        {
            int cmp = a.GlobalPosition.Y.CompareTo(b.GlobalPosition.Y);
            return cmp != 0 ? cmp : a.GlobalPosition.X.CompareTo(b.GlobalPosition.X);
        });
        return list;
    }

    private static void FindAllRecursive<T>(Node node, List<T> found) where T : Node
    {
        if (!IsLiveNode(node))
            return;
        if (node is T item)
            found.Add(item);
        // Nodes are freed continuously while a scene transitions, so a walk that meets a
        // dead child stops there instead of reporting a field that is simply gone.
        var children = TryReadQuiet(() => node.GetChildren().ToList());
        if (children == null)
            return;
        foreach (var child in children)
            FindAllRecursive(child, found);
    }

    /// <summary>
    /// Last-resort description for a hover tip whose own text is empty: ask the model
    /// the tip points at. PowerModel/RelicModel/CardModel each expose their own text
    /// under a different name, so try them in order of specificity.
    /// </summary>
    private static string? DescribeCanonicalModel(IHoverTip tip)
    {
        var model = TryRead("keywords.canonical_model", () => (object?)tip.CanonicalModel);
        if (model == null)
            return null;

        foreach (var propertyName in new[] { "SmartDescription", "DynamicDescription", "Description" })
        {
            // This loop tries candidate property names until one works, so a throw on the
            // way is a step in the search, not a lost field.
            var text = TryReadQuiet(() => ReadModelText(model, propertyName));
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    /// <summary>One candidate description property of a hover tip's model, or null when
    /// this game version has no such member or it holds nothing readable.</summary>
    private static string? ReadModelText(object model, string propertyName)
    {
        var property = model.GetType().GetProperty(propertyName);
        if (property == null)
            return null;
        var raw = property.GetValue(model)?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = StripRichTextTags(raw);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static List<Dictionary<string, object?>> BuildHoverTips(IEnumerable<IHoverTip> tips)
    {
        var result = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        TryDo("keywords", () =>
        {
            foreach (var tip in IHoverTip.RemoveDupes(tips))
            {
                var entry = TryRead("keywords.tip", () => DescribeHoverTip(tip, seen));
                if (entry != null)
                    result.Add(entry);
            }
        });
        return result;
    }

    /// <summary>One keyword entry, or null when the tip carries nothing worth reporting
    /// or repeats one already emitted.</summary>
    private static Dictionary<string, object?>? DescribeHoverTip(IHoverTip tip, HashSet<string> seen)
    {
        string? title = null;
        string? description = null;

        if (tip is HoverTip ht)
        {
            title = ht.Title != null ? StripRichTextTags(ht.Title) : null;
            description = StripRichTextTags(ht.Description);
        }
        else if (tip is CardHoverTip cardTip)
        {
            title = SafeGetText(() => cardTip.Card.Title);
            description = SafeGetCardDescription(cardTip.Card);
        }

        // Some keywords ship with no description text at all (Sly is one), and some
        // describe themselves in terms of themselves. Fall back to the canonical model
        // the tip points at, which carries the real effect text, before giving up.
        if (string.IsNullOrWhiteSpace(description))
            description = DescribeCanonicalModel(tip);

        if (title == null && description == null)
            return null;

        string key = title ?? description!;
        if (!seen.Add(key))
            return null;

        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = title,
            ["description"] = description
        };

        string? tipId = SafeGetText(() => tip.Id);
        if (!string.IsNullOrWhiteSpace(tipId))
            entry["id"] = tipId;
        if (string.IsNullOrWhiteSpace(description))
            entry["description_missing"] = true;

        return entry;
    }

    internal static T? FindFirst<T>(Node start) where T : Node
    {
        if (!IsLiveNode(start))
            return null;
        if (start is T result)
            return result;
        Godot.Collections.Array<Node> children;
        try
        {
            children = start.GetChildren();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        foreach (var child in children)
        {
            var val = FindFirst<T>(child);
            if (val != null) return val;
        }
        return null;
    }

    internal static bool IsLiveNode(Node? node)
    {
        try
        {
            return node != null && GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    internal static bool IsNodeVisible(CanvasItem? node)
    {
        try
        {
            return node != null && IsLiveNode(node) && node.Visible && node.IsVisibleInTree();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsMapScreenOpenOrVisible()
    {
        var mapScreen = NMapScreen.Instance;
        return mapScreen != null && (mapScreen.IsOpen || IsNodeVisible(mapScreen));
    }

    /// <summary>
    /// Has the map screen been deliberately OPENED, ignoring a lingering close?
    /// </summary>
    /// <remarks>
    /// NMapScreen.Close() clears IsOpen and only then animates out, so for the
    /// rest of that animation IsNodeVisible() still reports the screen -- which
    /// makes IsMapScreenOpenOrVisible() true immediately after travelling into
    /// a room. That is the right reading for "is the map on screen" and the
    /// wrong one for "did the player go back to the map", and the merchant
    /// branch needs the second.
    ///
    /// NMerchantRoom._Ready only calls SetTravelEnabled(true); the one merchant
    /// path that calls NMapScreen.Open() is HideScreen, wired to the
    /// ProceedButton's Released signal. So at a merchant this IS the discrete
    /// "the shop is finished" signal, which is what the rest-site and event
    /// branches have in their own room state and the merchant lacked.
    /// </remarks>
    private static bool IsMapScreenDeliberatelyOpen()
    {
        return NMapScreen.Instance?.IsOpen == true;
    }

    /// <summary>
    /// Is the run in the middle of a room transition?
    /// </summary>
    /// <remarks>
    /// RunManager wraps every room change in a NetLoadingHandle, which raises
    /// NetService.IsGameLoading for the whole scope: map travel
    /// (EnterMapPointInternal), an event option that pushes a combat room
    /// (EnterRoomWithoutExitingCurrentRoom - the "Fight" options), act changes
    /// and run start. The singleplayer service tracks the flag too, so this is
    /// the one signal that covers every path.
    ///
    /// It is needed because RunManager.EnterRoomInternal pushes the destination
    /// onto the room stack BEFORE awaiting its Enter, so a state read during a
    /// transition sees the new room already current while none of its managers
    /// have initialized - and before that push, the SOURCE screen is still
    /// current and still looks actionable even though its choice is already
    /// executing.
    /// </remarks>
    private static bool IsRunTransitionInFlight()
    {
        return TryRead<bool?>(() => MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress
                && MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.IsGameLoading) ?? false;
    }

    /// <summary>
    /// Is a map travel already resolving?
    /// </summary>
    /// <remarks>
    /// NMapScreen.IsTraveling is set the instant a destination is finalized
    /// (TravelToMapCoord) and is cleared only after RunManager has finished
    /// entering the destination room. Throughout that window the map screen is
    /// still visible and its points still report MapPointState.Travelable, so
    /// anything keying off those two alone happily offers - and takes - a
    /// second travel into a room that is still loading.
    /// </remarks>
    private static bool IsMapTravelInFlight()
    {
        try
        {
            var mapScreen = NMapScreen.Instance;
            return mapScreen != null && IsLiveNode(mapScreen) && mapScreen.IsTraveling;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The map points the player could actually click right now, in column order.
    /// </summary>
    /// <remarks>
    /// Mirrors the game's own NMapPoint.IsTravelable gate exactly. MapPointState
    /// is only the topology flag - the game's own comment calls it "adjacent
    /// node you can travel to IF you have completed the room" - and it stays
    /// Travelable for the whole room. NMapScreen.IsTravelEnabled is the half
    /// that says the room IS finished: TravelToMapCoord clears it, and every
    /// room turns it back on when it completes (NMapRoom/NMerchantRoom on load,
    /// NRestSiteRoom/NTreasureRoom/NEventRoom/NCrystalSphereScreen on proceed,
    /// CombatManager after a win). Gating on it therefore keeps the
    /// lingering-map escape hatches in the state builder working while closing
    /// the room-transition window.
    /// </remarks>
    internal static List<NMapPoint> GetTravelableMapPoints(NMapScreen mapScreen)
    {
        // IsTraveling covers a travel started FROM this map screen. It does not
        // cover an act change: RunManager.EnterAct opens the new act's map screen
        // (EnterRoomInternal(new MapRoom())) and only THEN awaits its room fade,
        // so for the length of that fade the new map is drawn with travelable
        // points while the act transition is still in flight. Travelling into
        // that window is not merely early, it corrupts the run: the entry we
        // start calls NTransition.RoomFadeIn, whose first act is _tween?.Kill()
        // on the very tween EnterAct is awaiting - and Tween.Kill() never emits
        // `finished`, while TweenHelper.AwaitFinished only bails out when its
        // OWNER node leaves the tree (NTransition never does). EnterAct's await
        // therefore hangs forever, stranding its two NetLoadingHandles, and
        // NetLoadingHandle's refcount can then never return to zero - pinning
        // IsGameLoading true for the rest of the session, which makes every
        // later event choice unanswerable. Diagnosed 2026-08-09 from the handle
        // trace in McpMod.LoadingDiagnostics.cs; the window is normally hidden
        // behind the fade animation and only reachable under Instant Mode.
        if (mapScreen.IsTraveling || IsRunTransitionInFlight())
            return new List<NMapPoint>();

        return FindAll<NMapPoint>(mapScreen)
            .Where(mp => mp.Point != null
                         && (mapScreen.IsDebugTravelEnabled
                             || (mapScreen.IsTravelEnabled && mp.State == MapPointState.Travelable)))
            .OrderBy(mp => mp.Point.coord.col)
            .ToList();
    }

    private static bool IsControlVisibleInTree(NClickableControl? control)
    {
        try
        {
            return control != null &&
                   IsLiveNode(control) &&
                   IsNodeVisible(control);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static Node? GetOpenModalNode()
    {
        try
        {
            return NModalContainer.Instance?.OpenModal as Node;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static bool IsDescendantOf(Node? node, Node ancestor)
    {
        return TryReadQuiet(() =>
        {
            for (var current = node; current != null && IsLiveNode(current); current = current.GetParent())
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
            }
            return false;
        });
    }

    private static bool IsControlVisibleInOpenModal(NClickableControl? control, Node? openModal = null)
    {
        openModal ??= GetOpenModalNode();
        if (control == null || openModal == null || !IsLiveNode(control) || !IsLiveNode(openModal))
            return false;

        try
        {
            return control.Visible && IsDescendantOf(control, openModal);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsPopupButtonActionable(NClickableControl? control)
    {
        if (control == null || !IsLiveNode(control))
            return false;

        try
        {
            return control.IsEnabled &&
                   (IsControlVisibleInTree(control) || IsControlVisibleInOpenModal(control));
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsControlVisibleOrActionable(NClickableControl? control)
    {
        return IsControlVisibleInTree(control) && control!.IsEnabled;
    }

    private static bool IsFtueNodeActive(Node node)
    {
        if (node is not CanvasItem canvas || !IsLiveNode(node))
            return false;

        if (IsNodeVisible(canvas))
            return true;

        if (ReferenceEquals(GetOpenModalNode(), node))
            return true;

        try
        {
            return GetInstanceFieldValue(node, "_confirmButton") is NClickableControl confirmButton &&
                   (IsControlVisibleInTree(confirmButton) || IsControlVisibleInOpenModal(confirmButton));
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static Dictionary<string, object?>? BuildVisibleFtueState(Node root)
    {
        var tutorialFtue = FindVisibleAcceptTutorialsFtue(root);
        if (tutorialFtue != null && IsFtueNodeActive(tutorialFtue))
        {
            var state = NewStateResult("menu");
            state["menu_screen"] = "tutorial_prompt";
            state["message"] = "Enable Tutorials? Choose yes or no.";
            state["options"] = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { ["name"] = "no", ["enabled"] = true },
                new(StringComparer.Ordinal) { ["name"] = "yes", ["enabled"] = true }
            };
            return state;
        }

        var ftue = FindVisibleGenericFtue(root);
        if (ftue != null)
        {
            var canAdvance = FindFtueAdvanceButton(ftue) != null;
            var state = NewStateResult("menu");
            state["menu_screen"] = "tutorial";
            state["message"] = "Tutorial popup active. Use advance to dismiss.";
            state["options"] = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { ["name"] = "advance", ["enabled"] = canAdvance },
                new(StringComparer.Ordinal) { ["name"] = "proceed", ["enabled"] = canAdvance }
            };
            return state;
        }

        var popup = BuildVisiblePopupState(root);
        if (popup != null)
            return popup;

        return null;
    }

    private static Dictionary<string, object?>? BuildVisiblePopupState(Node root)
    {
        var popup = FindVisibleVerticalPopup(root);
        var options = popup != null
            ? GetPopupOptions(popup)
            : GetVisiblePopupButtonOptions(root);

        var stateOptions = new List<Dictionary<string, object?>>();
        foreach (var option in options)
        {
            stateOptions.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = option.Name,
                ["enabled"] = option.Button.IsEnabled
            });
        }

        if (stateOptions.Count == 0)
            return null;

        var state = NewStateResult("menu");
        state["menu_screen"] = "popup";
        state["message"] = popup != null ? GetVerticalPopupText(popup, "TitleLabel") ?? "Popup active." : "Popup active.";
        state["body"] = popup != null ? GetVerticalPopupText(popup, "BodyLabel") : null;
        state["options"] = stateOptions;
        return state;
    }

    private static MegaCrit.Sts2.Core.Nodes.Ftue.NAcceptTutorialsFtue? FindVisibleAcceptTutorialsFtue(Node root)
    {
        var openModal = GetOpenModalNode();
        if (openModal is MegaCrit.Sts2.Core.Nodes.Ftue.NAcceptTutorialsFtue openFtue &&
            IsFtueNodeActive(openFtue))
        {
            return openFtue;
        }

        return FindAll<MegaCrit.Sts2.Core.Nodes.Ftue.NAcceptTutorialsFtue>(root)
            .FirstOrDefault(ftue => IsFtueNodeActive(ftue) || ReferenceEquals(openModal, ftue));
    }

    private static bool IsAnyFtueVisible(Node root)
    {
        return BuildVisibleFtueState(root) != null;
    }

    private static NVerticalPopup? FindVisibleVerticalPopup(Node root)
    {
        var openModal = GetOpenModalNode();
        if (openModal is NVerticalPopup openPopup && GetPopupOptions(openPopup).Count > 0)
            return openPopup;

        if (openModal != null)
        {
            foreach (var popup in FindAll<NVerticalPopup>(openModal))
            {
                if (GetPopupOptions(popup).Count > 0)
                    return popup;
            }
        }

        foreach (var popup in FindAll<NVerticalPopup>(root))
        {
            if ((IsNodeVisible(popup) || (openModal != null && IsDescendantOf(popup, openModal))) &&
                GetPopupOptions(popup).Count > 0)
            {
                return popup;
            }
        }
        return null;
    }

    private static List<(string Name, NClickableControl Button)> GetPopupOptions(NVerticalPopup popup)
    {
        var options = new List<(string Name, NClickableControl Button)>();
        AddPopupOption(options, popup.YesButton, "yes");
        AddPopupOption(options, popup.NoButton, "no");
        return options;
    }

    private static List<(string Name, NClickableControl Button)> GetVisiblePopupButtonOptions(Node root)
    {
        var options = new List<(string Name, NClickableControl Button)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openModal = GetOpenModalNode();
        if (openModal != null)
        {
            foreach (var button in FindAll<NClickableControl>(openModal))
                AddVisiblePopupButtonOption(options, seen, button, null, openModal);
        }

        foreach (var button in FindAll<NPopupYesNoButton>(root))
        {
            AddVisiblePopupButtonOption(options, seen, button, button.IsYes ? "yes" : "no", null);
        }

        foreach (var modal in FindAll<NModalContainer>(root))
        {
            if (!IsNodeVisible(modal))
                continue;

            foreach (var button in FindAll<NClickableControl>(modal))
                AddVisiblePopupButtonOption(options, seen, button, null, null);
        }

        return options;
    }

    private static void AddVisiblePopupButtonOption(
        List<(string Name, NClickableControl Button)> options,
        HashSet<string> seen,
        NClickableControl? button,
        string? fallback,
        Node? openModal)
    {
        if (!(IsControlVisibleInTree(button) || IsControlVisibleInOpenModal(button, openModal)))
            return;

        var label = button is NPopupYesNoButton popupButton
            ? GetPopupButtonLabel(popupButton)
            : GetClickableControlLabel(button!);
        var name = NormalizeMenuOptionName(label) ?? fallback;
        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            return;

        options.Add((name, button!));
    }

    private static void AddPopupOption(
        List<(string Name, NClickableControl Button)> options,
        NPopupYesNoButton? button,
        string fallback)
    {
        if (!IsPopupButtonActionable(button))
            return;

        var label = GetPopupButtonLabel(button!);
        var name = NormalizeMenuOptionName(label) ?? fallback;
        options.Add((name, button!));
    }

    private static string? GetVerticalPopupText(NVerticalPopup popup, string propertyName)
    {
        var label = popup.GetType().GetProperty(propertyName)?.GetValue(popup);
        return GetNodeText(label);
    }

    private static string? GetPopupButtonLabel(NPopupYesNoButton button)
    {
        var label = GetInstanceFieldValue(button, "_label");
        return GetNodeText(label);
    }

    private static string? GetClickableControlLabel(NClickableControl button)
    {
        foreach (var fieldName in new[] { "_label", "_textLabel", "_title", "_buttonLabel" })
        {
            var label = GetNodeText(GetInstanceFieldValue(button, fieldName));
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        foreach (var propName in new[] { "Text", "Label", "Title" })
        {
            var label = SafeGetText(() => button.GetType().GetProperty(propName)?.GetValue(button));
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        var childText = FindNodeTextRecursive(button);
        if (!string.IsNullOrWhiteSpace(childText))
            return childText;

        var nodeName = button.Name.ToString();
        if (!string.IsNullOrWhiteSpace(nodeName))
            return nodeName.EndsWith("Button", StringComparison.OrdinalIgnoreCase)
                ? nodeName[..^"Button".Length]
                : nodeName;

        return null;
    }

    private static string? FindNodeTextRecursive(Node start)
    {
        if (!IsLiveNode(start))
            return null;

        var text = GetNodeText(start);
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        Godot.Collections.Array<Node> children;
        try
        {
            children = start.GetChildren();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        foreach (var child in children)
        {
            text = FindNodeTextRecursive(child);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string? GetNodeText(object? node)
    {
        if (node == null)
            return null;

        foreach (var propName in new[] { "Text", "BbcodeText" })
        {
            var text = SafeGetText(() => node.GetType().GetProperty(propName)?.GetValue(node));
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string? NormalizeMenuOptionName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var sb = new StringBuilder();
        foreach (var ch in StripRichTextTags(text).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_')
                sb.Append('_');
        }

        var normalized = sb.ToString().Trim('_');
        return normalized.Length == 0 ? null : normalized;
    }

    private static Node? FindVisibleGenericFtue(Node start)
    {
        if (ReferenceEquals(start, (Godot.Engine.GetMainLoop() as SceneTree)?.Root))
        {
            var openModal = GetOpenModalNode();
            if (openModal != null)
            {
                var openFtue = FindVisibleGenericFtue(openModal);
                if (openFtue != null)
                    return openFtue;
            }
        }

        if (!IsLiveNode(start))
            return null;

        var typeName = start.GetType().FullName ?? "";
        if (typeName.StartsWith("MegaCrit.Sts2.Core.Nodes.Ftue.", StringComparison.Ordinal) &&
            !typeName.EndsWith(".NAcceptTutorialsFtue", StringComparison.Ordinal) &&
            IsFtueNodeActive(start))
        {
            return start;
        }

        Godot.Collections.Array<Node> children;
        try
        {
            children = start.GetChildren();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        foreach (var child in children)
        {
            var val = FindVisibleGenericFtue(child);
            if (val != null)
                return val;
        }

        return null;
    }

    private static NClickableControl? FindFtueAdvanceButton(Node ftue)
    {
        foreach (var fieldName in new[]
        {
            "_confirmButton",
            "_advanceButton",
            "_nextButton",
            "_proceedButton",
            "_acknowledgeButton",
            "_arrowButton",
            "_rightArrowButton",
            "_rightButton"
        })
        {
            if (GetInstanceFieldValue(ftue, fieldName) is NClickableControl fieldButton &&
                IsPopupButtonActionable(fieldButton))
            {
                return fieldButton;
            }
        }

        try
        {
            foreach (var field in ftue.GetType().GetFields(
                         System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.NonPublic |
                         System.Reflection.BindingFlags.Instance))
            {
                if (field.GetValue(ftue) is NClickableControl fieldButton &&
                    IsPopupButtonActionable(fieldButton))
                {
                    return fieldButton;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        return FindAll<NClickableControl>(ftue).FirstOrDefault(IsPopupButtonActionable);
    }

    // One run-modifier tickbox on the custom-run screen.
    //
    // Modifier ids are NOT unique on that screen: the "character cards" modifier is
    // instantiated once per character and every instance reports Id.Entry ==
    // "CHARACTER_CARDS". Each entry therefore also carries a Key that is unique on the
    // screen — the id suffixed with the character it applies to, or with an index as a
    // last resort. The Key is what callers select and what `selected.modifiers` reports;
    // the raw Id is still surfaced for callers that reason about the underlying model.
    private sealed class RunModifierEntry
    {
        internal RunModifierEntry(
            NRunModifierTickbox tickbox,
            ModifierModel modifier,
            string id,
            string key,
            string? characterId)
        {
            Tickbox = tickbox;
            Modifier = modifier;
            Id = id;
            Key = key;
            CharacterId = characterId;
        }

        internal NRunModifierTickbox Tickbox { get; }
        internal ModifierModel Modifier { get; }
        internal string Id { get; }
        internal string Key { get; }
        internal string? CharacterId { get; }
    }

    // Run-modifier tickboxes under `screen`, in screen order. Empty for screens that have
    // none (the plain character select), which is how callers tell the screens apart.
    private static List<RunModifierEntry> GetRunModifierEntries(Node screen)
    {
        var entries = new List<RunModifierEntry>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tickbox in FindAll<NRunModifierTickbox>(screen))
        {
            var entry = TryReadQuiet(() => DescribeRunModifier(tickbox, usedKeys));
            if (entry != null)
                entries.Add(entry);
        }

        return entries;
    }

    /// <summary>One tickbox as an entry, or null when it exposes no modifier.</summary>
    private static RunModifierEntry? DescribeRunModifier(NRunModifierTickbox tickbox, HashSet<string> usedKeys)
    {
        var modifier = tickbox.Modifier;
        if (modifier == null)
            return null;

        var id = modifier.Id.Entry;

        string? characterId = modifier is CharacterCards characterCards
            ? TryReadQuiet(() => characterCards.CharacterModel.Entry)
            : null;

        var key = string.IsNullOrEmpty(characterId) ? id : $"{id}_{characterId}";
        if (!usedKeys.Add(key))
        {
            var suffix = 2;
            while (!usedKeys.Add($"{key}_{suffix}"))
                suffix++;
            key = $"{key}_{suffix}";
        }

        return new RunModifierEntry(tickbox, modifier, id, key, characterId);
    }

    // Keys of every currently ticked run modifier, in screen order.
    private static List<string> TickedModifierKeys(IEnumerable<RunModifierEntry> entries)
    {
        var keys = new List<string>();
        foreach (var entry in entries)
        {
            TryDo(() =>
            {
                if (entry.Tickbox.IsTicked)
                    keys.Add(entry.Key);
            });
        }
        return keys;
    }
}
