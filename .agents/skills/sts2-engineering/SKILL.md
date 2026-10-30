---
name: sts2-engineering
description: >-
  Use for any change to STS2_MCP (the Slay the Spire 2 mod and its MCP bridge),
  for deciding where new code belongs, and for any claim about game behavior
  that has to come from the shipped build. Fuses ub-mode discipline — smallest
  working change, honest checks, root causes, deterministic gates, small commits
  — with a layered dependency rule and repository standards adapted from Clean
  Architecture (layers, one endpoint per request/response/handler, error bodies,
  naming, build hygiene) and REA game-build evidence: managed members in
  sts2.dll, Harmony patch targets, Godot .pck resources. Skip REA when this
  repository, docs/raw-*.md, or a running game's localhost API already answers
  the question.
metadata:
  version: "3"
  adapted-for: STS2MCP
  fuses: "unclebob/ub-mode@9ac940b (principles paraphrased; upstream has no license file), morluto/rea reverse-engineer-anything@9da1eed (MIT), ardalis/CleanArchitecture (MIT; layer and REPR rules extracted, no scaffold copied), jasontaylordev/CleanArchitecture (MIT; build hygiene, naming, endpoint and error standards extracted, no scaffold copied)"
---

# STS2 engineering mode

Every change runs three passes: **First make it work**, **Then make it right**,
**Follow practice**. Before the first pass, route the question: most work in this
repository needs no reverse engineering, and reaching for REA when the source or
the running game already answers is the first laziness violation.

## Route the question before you work

| Question | Route | Not this |
| --- | --- | --- |
| How does this repo build, parse, or document something? | Read the code: `McpMod*.cs`, `mcp/server.py`, `build.ps1`, `docs/raw-*.md` | REA |
| What is the game doing right now? | The mod's HTTP API on `localhost:15526` | Static analysis |
| Which game type, field, method, node, or patch target exists in this build? | REA (see "Evidence from the build") | Guessing from memory or another STS2 version |
| Is a library or engine API shaped a certain way? | The library's own docs or metadata | REA on the game |

Game internals change between builds. A remembered class name is a hypothesis,
not evidence, and a finding from another build is not evidence for this one.

## First make it work

- **One decision in one place.** Ship the smallest edit that solves the problem.
  A new state field belongs in the builder that already owns that room; do not
  open a new partial file for one field, and do not add a fourth way to read the
  same value.
- **Get the shape right before the logic.** The JSON contract is `docs/raw-*.md`
  plus the `mcp/server.py` docstrings: settle field names (snake_case), value
  types, and null semantics there first, then write the code that produces them.
- **Honest checks, not decorative tests.** The C# mod runs inside the game and
  cannot be unit-tested; `CONTRIBUTING.md` is explicit that the manual in-game
  check is the test. Make that check concrete: reproduce the current behavior
  with a real request or a real screen, then show the changed response or
  transition. In `mcp/server.py`, test only pure transforms that need no live
  game, and never add a harness that mocks the game to look green. If an honest
  test is impractical, do not write a bad one — say so and name the manual check.
- **Redesign from first principles.** When a requirement fights the current
  design, ask what you would have built if the requirement had been there from
  the start, then deliver that in pieces instead of bolting it on.
- **Attack the premise.** If two fixes that share one assumption fail the same
  way, write the assumption in one sentence and count which actors hold it, then
  change that. Do not start a third fix first.
- **Subtract before you add.** Remove the reflection fallback your typed binding
  just made unnecessary; check `McpMod.Helpers.cs` before writing another helper;
  delete the dead branch while you are in the function.
- **Aim at a checkable end state.** A change is done when you can name the
  command and the observation: a clean build, a specific request and response,
  and the in-game path you actually exercised.
- **Build the lever.** If a change repeats a shape, do one unit by hand, confirm
  it, then make the existing handler or builder match that unit. Do not
  generalize from an unverified guess.

### Architecture: the layered target

The target is a layered solution, recorded in
[references/architecture.md](references/architecture.md): `Core` (contracts and
the rules the mod owns), `UseCases` (one handler per action or query),
`Infrastructure` (game access behind ports), `Transport` (`McpMod.cs` today:
`HttpListener`, status mapping, main-thread dispatch), and the Python bridge
outside the assembly. Dependencies point inward only.

The split into separate projects is deferred — the mod ships as one
`STS2_MCP.dll`, and the split is its own discussed change. The *rules* apply
now, to new code, inside the single assembly; the folders and namespaces mark the
layers. When a change touches a violation, fix that violation in the same change
or record it in `docs/adr/` and say so. Never introduce a violation because the
surrounding file already has one, and never smuggle a restructure into a feature
PR.

- **Model the domain.** Keep the vocabulary precise — run, act, map node, room,
  combat, card, relic, potion, reward, event, profile, compendium, run history.
  Make illegal states impossible: singleplayer and multiplayer endpoints stay
  mutually exclusive (409), menu and run states stay distinct, the action name
  stays a closed set.
- **Boundary discipline.** Validate request bodies where they enter, parse once,
  then trust them inside. The mod owns game semantics; the Python layer stays a
  thin pass-through per the repository rule, and no game logic moves into Python.
  Keep the decision itself in a pure, testable function where the shape allows.
- **Type discipline.** Parse external JSON into typed values at the boundary.
  Do not silence nullable warnings, widen to `object`/`dynamic` to make something
  compile, or leave a `catch` that only hides a shape mismatch.
- **A shape, not a bag.** A new field is a typed member on a DTO in
  `Core/Contracts/`, not another `Dictionary<string, object?>` entry. When you
  extend a response that is still a bag, convert that response in the same
  change.
- **Make operations idempotent.** An action must end in the same state whether it
  runs once, twice, or after a mid-flight failure. Re-read state before mutating
  it, expect hand and reward indices to shift, and never let a retried reward or
  room transition apply twice. If a request can time out, the action behind it
  must be safe to repeat.
- **One writer for shared state.** Game objects are touched on the Godot main
  thread only, through `RunOnMainThread`, the main-thread queue, and the
  `SceneTree.ProcessFrame` drain. HTTP worker threads own their own locals and
  never hold game objects. Any new shared mutable state needs the same queue, not
  a lock around it.
- **Run the boundary gate.** The layer rules are checkable without the game: the
  `grep` checks in [references/architecture.md](references/architecture.md) must
  stay clean for `Core/` and `UseCases/`, and `Transport/` may only keep the
  recorded exceptions. Run them before claiming the change is done.

## Then make it right

- **Fix root causes.** Reproduce with the API response, the mod log, or a
  specific screen before editing. A null guard that stops the crash without
  fixing the state that produced the null is not a fix.
- **Sequence verifiable units.** One field, one action, one endpoint at a time;
  check each before starting the next. A deliberately broken intermediate state
  is allowed only when you planned the undo.
- **Check behavior, not implementation.** The observable contract is HTTP status,
  response fields, and the resulting game state. A check that would still pass
  when the handler returned nothing is not a check.
- **Match the repository standards.**
  [references/repo-standards.md](references/repo-standards.md) holds the full
  list; the ones that get violated first:
  - **Errors are contract.** The documented body is
    `{"status": "error", "error": "..."}`. No `exception_type`, no `stack_trace`,
    no internal path to any caller. The two current responses that leak them
    (`McpMod.cs:320`, `McpMod.cs:421`) are repaired when touched, not copied.
  - **An endpoint is four things.** Request type, result type, handler, and a row
    in `docs/raw-full.md` plus `docs/raw-simplified.md`. A route without all four
    is unfinished.
  - **Status codes are decided once.** `400` for shape, `409` for state refusal,
    `404` for unknown route, `500` for unexpected failure, mapped in the transport
    only.
  - **Names and files.** One public type per file, file named after it, folders
    are layers, JSON is `snake_case` and mapped in one place. The `McpMod.*.cs`
    partials are the grandfathered exception.
  - **Toolchain targets.** `Directory.Build.props` (configuration: game path, no
    literals), `Directory.Build.targets` (analyzer gate), and `.editorconfig` are
    committed. `global.json` is still absent; adding it is a small standalone
    change, not something to smuggle into a feature PR.
- **Use the gates that actually exist here.** The real gates are a clean
  `dotnet build` with zero warnings (the `CONTRIBUTING.md` checklist), the
  boundary `grep` checks, an exercised in-game path, and synchronized docs.
  `crapper`, `dryer`, and `mutator` are not wired into this repository: run one
  only if it is already installed and actually understands the language you
  touched. Never report a gate you did not run, and never install a tool just to
  satisfy a checklist. When they are unavailable, substitute honestly — split
  functions that no longer fit on one screen, reuse existing helpers instead of
  duplicating a block, delete dead fallbacks — and state which gate was
  unavailable.
- **Refactor with the behavior pinned.** Capture the current response first,
  change the structure, then capture it again and compare. If the diff is not
  intended, the refactor is not done.

## Follow practice

- **Keep a log.** Record each action with its reason and its proof, and report it
  at the end. That log becomes the PR description: why, what changed, how it was
  proven.
- **Guard the context window.** `McpMod.StateBuilder.cs` is about 100 KB,
  `McpMod.Formatting.cs` about 51 KB, and `docs/raw-full.md` about 1,540 lines.
  Locate the region with search, read slices, and keep the main thread small;
  send bulk reading to a subagent and keep only the summary.
- **Clean up safely.** Only `out/`, `bin/`, `obj/`, and the extraction or temp
  directories you created are disposable. Untracked files you did not create wait
  for a decision, and nothing in the game install directory is ever touched.
- **Commit in small ordered steps.** Each commit says why, what changed, and how
  it was proven. Keep one logical change per PR, reference the issue, open a
  discussion first when the API surface changes, and do not push to upstream.
  Review and commit without asking; the message shape is
  `.agents/shared/conventional-commit/REFERENCE.md` and the autonomy limits are
  `.agents/shared/ai-contribution/REFERENCE.md`.

## Evidence from the build

Use REA only when the answer depends on the shipped build: a member in
`sts2.dll`, a Harmony target in `TryApplyHarmonyPatches`, a
`MegaCrit.Sts2.Core.Nodes.*` class, or a resource inside a `.pck`.

1. **Resolve the build first.** `make config` (or `$env:STS2_GAME_DIR`, or
   `-p:STS2GameDir`); `Directory.Build.props` resolves the path and no project
   file hard-codes one. Then the platform data directory
   (`data_sts2_windows_x86_64`,
   `SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64`,
   `data_sts2_linuxbsd_x86_64`). List it instead of assuming file names, and
   record the game version with every conclusion. Read-only on the install
   directory; extraction goes to a fresh directory outside the repository.
2. **Route the artifact.** Managed (`sts2.dll`, `GodotSharp.dll`,
   `0Harmony.dll`): `inspect_managed_artifact` with `detail: "summary"`, then
   `inspect_managed_members` with exact `type_names`, then retained views.
   Godot `.pck`: `open_binary`, then `inspect_artifact` with
   `detail: "summary"`. Native host or crash: native provider, last resort.
3. **Stay summary-first.** Keep the `parent_evidence_id`, page
   `inspect_analysis_view` instead of re-running analysis, and do not parse a
   truncated preview as canonical JSON.
4. **Keep the labels honest.** Static inspection never observes execution.
   Report observations, inferences, and unknowns separately, cite Evidence IDs,
   bound every negative search, and treat "the symbol exists" as proof that a
   member is declared — not that the path is reachable or the transition will
   succeed.
5. **Verify at runtime in the mod loop.** Build, install into `mods/`, launch,
   and exercise the path. That run, not the analysis, is the runtime evidence.
   Close native sessions with `close_binary` when the investigation ends.

If the REA server is missing or a call is blocked, read
[connection and recovery](references/connection-and-recovery.md) before running
setup again. This skill is instructions only; it registers nothing.

## Ground truth in this repository

| Area | Files | Rule |
| --- | --- | --- |
| Mod entry, HTTP server, main-thread queue | `McpMod.cs` | Game access only on the main thread |
| State serialization | `McpMod.StateBuilder.cs`, `McpMod.Formatting.cs` | Field shape follows the documented contract |
| Actions | `McpMod.Actions.cs`, `McpMod.MultiplayerActions.cs` | Idempotent, guard-checked, index-safe |
| Profile, compendium, wiki | `McpMod.Profile.cs`, `McpMod.Compendium.cs`, `McpMod.Wiki.cs` | State the source and its limits |
| MCP bridge | `mcp/server.py` | Thin pass-through; no game logic |
| Contract docs | `docs/raw-full.md`, `docs/raw-simplified.md` | Update with any API surface change |
| Build and install | `STS2_MCP.csproj`, `Directory.Build.props`, `Makefile`, `build.ps1`, `mod_manifest.json` | Zero-warning build; config comes from `STS2_GAME_DIR` / `build.local.props`; manifest copied as `STS2_MCP.json` |
| Layer boundaries | `Core/`, `UseCases/`, `Infrastructure/`, `Transport/` (target; today flat partials) | Arrows point inward; grep checks until the project split |
| Standard toolchain | `global.json` (absent); `Directory.Build.props`, `Directory.Build.targets`, `.editorconfig` committed | Add `global.json` in a standalone change; zero-warning build until then |

The automated checks are the headless harnesses under `tests/` (`make test`) and
the endpoint/docs audit (`make audit`); there are no CI workflows. The build, the
boundary `grep` checks, and a manual in-game smoke test are the verification
story, and the README "Tested against" line is the supported game version.
`Core` and the pure formatting seam are the place where a test project would
first earn its keep.

## Finish the task

Report in this order: what changed and why, the evidence it rests on (Evidence
IDs, API responses, or the manual check), which gates ran and which were
unavailable, the result of the compliance checklist in
[references/repo-standards.md](references/repo-standards.md), which boundary
violations you fixed, which you recorded in `docs/adr/`, and what remains
unknown. Keep repository-only observations separate
from game-build findings, and leave the working tree in the state the next person
expects — no stray extraction output, no uncommitted experiments you cannot
explain.

## Read only the relevant guide

- Layer target, dependency rules, boundary checks, and ADR-lite:
  [references/architecture.md](references/architecture.md)
- Build hygiene, naming, endpoint shape, error bodies, logging, test layout, and
  the compliance checklist: [references/repo-standards.md](references/repo-standards.md)
- STS2 recipes and the file that consumes each answer:
  [references/sts2-game-artifacts.md](references/sts2-game-artifacts.md)
- Managed assemblies, packs, extraction, and native targets:
  [references/native-and-artifacts.md](references/native-and-artifacts.md)
- Evidence paging, comparison, and residual unknowns:
  [references/evidence-workflows.md](references/evidence-workflows.md)
- Missing tools, blocked calls, providers, and client deadlines:
  [references/connection-and-recovery.md](references/connection-and-recovery.md)
- Provenance and how to re-apply this adaptation:
  [references/UPSTREAM.md](references/UPSTREAM.md)
