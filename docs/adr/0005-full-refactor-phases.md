# 0005: Full refactor runs in phases, starting with the analyzer gate

- Context: The user asked for a full refactor. `references/architecture.md` and
  ADR 0004 defer the layer split, the analyzer baseline (`bf0c92a`) turns 1538
  findings into build errors, and no automated test can pin the HTTP surface
  while files move. One branch cannot absorb both the analyzer clean-up and the
  `Core`/`UseCases`/`Infrastructure`/`Transport` split without becoming
  unreviewable.
- Decision: four phases, each its own branch and commit series, no remote
  changes of any kind (no push, no Discussion, no upstream issue):
  1. **Baseline** — commit the analysis setup, then formatting
     (`dotnet format whitespace`, `IDE0005`). Done.
  2. **Analyzer gate** — drive `docs/static-analysis.md` to zero so the
     `STS2StrictWarnings` opt-out can be deleted. Mechanical buckets first,
     the defensive-catch family as one documented helper, `MA0051` only where
     extraction buys clarity.
  3. **Typed contracts** — `Core/Contracts` for the responses `docs/raw-*.md`
     already documents; each endpoint converted with its before/after response
     captured and compared. This is the first slice ADR 0004 named.
  4. **Layer migration** — `UseCases`, `Infrastructure`, `Transport` folders
     and namespaces inside the single assembly; project split still deferred.
- Consequences: every phase ends on a clean build and an exercised in-game path,
  so a phase can be reverted alone. The strict analyzer gate stays off until
  phase 2 finishes, which means a new warning can still reach the branch.
- Debt: Phase 2 is not finished. 592 warnings remain at `c401b2b`
  (`CA1031` 216, `ERP022` 103, `MA0051` 73, `EPC12` 44, `S3358` 25, `S108` 25,
  `S2486` 21, `S3267` 20, the rest single-digit). Two findings need a decision
  recorded below before phase 2 can proceed.

## Orphans found while clearing `S1144`

Two symbols had lost their only caller in the fork merge, which means a code path
lost its wiring and the audit script no longer proves what it claims:

- `SendActionResultJson` maps `error_code` to a non-2xx status for action
  dispatch. Nothing calls it; the action paths send plain `SendJson`.
  `scripts/audit_endpoints.py` still asserts the symbol exists, so the check
  passes on a dead method. Re-wiring it would change the HTTP status of action
  errors (400/409 instead of 200), which is a contract change and needs its own
  decision. Until then the method stays, with a justification on the suppression.
- `GetLoadLobbyPlayers` returned connected ids and readiness, but its only
  consumer assigned the result to a local and never read it. The method is
  deleted and the per-player breakdown still derives readiness its own way.


## Decisions taken during phase 2

### `S1144` on Harmony patch methods is a false positive

`TargetMethods`, `Prefix` and `Postfix` are reached by Harmony through the
attributes on the patch class, not by a C# call, so Sonar's "unused private
method" cannot see the edge. They are not dead code and must not be deleted.
The remaining `S1144` hits (for example `SendActionError`,
`ResolveCurrentRunPath`, `TimelineUnlocksNeedManualRevealFromOpenTimeline`) are
candidates for deletion only after a build-and-patch run, because the same rule
is wrong here. The fix is a scoped suppression for patch classes plus deletion
of the genuinely dead helpers, not a blanket removal.

### The defensive-catch family gets one helper, not 590 suppressions

`CA1031`, `ERP022`, `S108`, `S2486` and `EPC12` are 591 of the remaining
findings and all describe the same deliberate style: a game member that
vanished must not kill a state read. The plan is a single adapter helper in
`McpMod.Helpers.cs`:

```csharp
[SuppressMessage("Design", "CA1031:Do not catch general exception types")]
private static T? TryRead<T>(Func<T> read, [CallerMemberName] string member = "",
    [CallerLineNumber] int line = 0)
{
    try { return read(); }
    catch (Exception ex) { Warn($"{member}:{line}", ex); return default; }
}
```

Call sites lose their `try`/`catch` entirely, so the analyzers stop firing at
the site and the exception is recorded in `McpMod.Warnings` instead of silently
dropped. Sites that need a fallback value, `continue`, or two statements keep a
small named wrapper method next to them; they do not get a `#pragma`.
`CA2007` stays open on purpose: the reward-observation awaits must resume on
the thread that owns the reward session, so `ConfigureAwait(false)` is wrong
there and the four fixer hunks it produced were reverted.
