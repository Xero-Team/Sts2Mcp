# Architecture target and boundary rules

Adapted from [ardalis/CleanArchitecture](https://github.com/ardalis/CleanArchitecture)
(MIT): the dependency rule, the REPR endpoint shape, the validation split, the
testable core, and ADRs are kept; the ASP.NET Core scaffold, extra projects,
DI container, Mediator, EF Core, and Aspire are not.

## Decision recorded

The end state is a layered solution. The code is **not** restructured now: new
code must already obey the rules below, existing violations are pointed out and
repaired when the file is touched, and the project split is its own planned
change, not a side effect of a feature PR.

## Target layers

| Layer | Namespace (now: folder) | Owns | Must not |
| --- | --- | --- | --- |
| Contract + Core | `Sts2Mcp.Core` (`Core/`) | Request/response DTOs, action-name enums, value rules the mod itself owns, the JSON shape mirrored by `docs/raw-*.md` | Reference `Godot`, `MegaCrit`, `System.Net`, or write responses |
| Use cases | `Sts2Mcp.UseCases` (`UseCases/`) | One handler per action or query; orchestration; command validation; decisions ("is this legal state?") | Set HTTP status codes, open sockets, serialize responses, format markdown |
| Infrastructure | `Sts2Mcp.Infrastructure` (`Infrastructure/`) | Game access ports implemented over `MegaCrit`/`Godot` types, node drivers, reflection helpers, save/history scanning, Harmony patches | Decide the contract shape or the business rule |
| Transport | `Sts2Mcp.Transport` (`Transport/`; today the `McpMod` partial) | `HttpListener`, routing, status mapping, main-thread dispatch, JSON serialization | Contain game rules |
| Bridge | `mcp/server.py` | Thin pass-through to the HTTP API | Any game logic (repository rule) |

Dependencies point inward only:

```
Transport → UseCases → Core
     ↓          ↓
Infrastructure ┘   (implements ports declared by Core/UseCases)
```

`Infrastructure` may reference `Core`; nothing references `Transport`. The bridge
is outside the assembly and talks HTTP only.

## Why the split is deferred

Splitting the projects changes more than folders:

- The mod ships as one `Sts2Mcp.dll` copied into `mods/`. Layer DLLs would have
  to ship alongside it (or be ILRepack-merged), which changes the install
  instructions and the README for every user.
- The repository has one project, no CI, and no automated tests; a four-project
  split is a structural change that needs its own discussion and smoke test.
- The dependency graph is cheap to fix while it is still one assembly: a
  violation is a `using` line, not a project reference.

That is why the rules apply now and the split waits for its own issue.

## Single-assembly enforcement (runnable today)

Because everything is one assembly, the compiler cannot stop a violation. Use
these checks instead, and run them on the folders that exist:

```bash
# Core and UseCases must stay ignorant of the game and the transport.
grep -rn "using Godot\|using MegaCrit\|HttpListener\|StatusCode" Core UseCases --include=*.cs

# Infrastructure must not answer HTTP or build response bodies.
grep -rn "StatusCode\|HttpListenerResponse" Infrastructure --include=*.cs

# Transport must not gain new game access; the only allowed holder is the
# existing partial that already reads game state, until it is migrated.
grep -rn "using MegaCrit\|using Godot" Transport --include=*.cs
```

Expected result: no matches for the first two; the third only lists the known,
recorded exceptions. A new match is a violation to fix in the change that
introduced it.

## How to write code under these rules

1. **New behavior starts as a use case, not a handler.** Add a class in
   `UseCases/`, give it a request type and a result type in `Core/Contracts/`,
   and let the transport call it and map the result to a status code.
2. **Game access goes behind a port.** Declare the minimum interface the use case
   needs (`IGameStateReader`, `IRewardScreen`, ...), implement it in
   `Infrastructure/` over `MegaCrit`/`Godot`, and keep the transport ignorant of
   both.
3. **A shape, not a bag.** New fields are typed members on a DTO. Do not add
   another `Dictionary<string, object?>` entry. When you are already editing an
   existing response, replace that endpoint's bag with a DTO in the same change.
4. **Validate at the edge, assume inside.** Parse and validate the request in the
   transport, then pass a validated command object. Invariants inside are
   exceptions, not "return null and let the caller notice".
5. **Status codes live in one place.** The transport maps a known outcome to
   `200`/`400`/`409`, and an unexpected exception to `500`. A use case never
   writes a status code, and an adapter never chooses one.
6. **One writer for game state.** Game objects are touched on the Godot main
   thread through the existing queue; a use case may depend on a port that
   marshals, never on a lock around game objects.
7. **Idempotence is a use-case property.** Decide it there: re-read state, expect
   shifting indices, and make retries converge. Do not push that decision into
   the transport or the adapter.
8. **Split files by responsibility, not by size.** `McpMod.StateBuilder.cs` grows
   because it owns a big response; the fix is a DTO plus room-shaped readers, not
   another 500-line partial.

## Touching existing code

When a change touches a violation, fix that violation as part of the change, or
record it and say so:

- A function you edit that sets a status code from the adapter: move the mapping
  out now.
- A response you extend that is still a dictionary bag: convert that response in
  the same change, or state why not.
- A reflection fallback you replace with a typed call: delete it.
- Anything larger than the change you were asked for: write it down (see debt
  below), report it, and leave it for a dedicated PR.

Do not mix a feature with a broad restructure. Do not introduce a violation
because the surrounding file already has one.

## Tests by layer

| Layer | Test it how |
| --- | --- |
| Core (DTOs, value rules) | Plain unit tests, no game required. This is the first place a test project earns its keep. |
| UseCases | Unit tests only through port fakes; never mock the game object graph |
| Infrastructure | No unit tests; exercise it in a running game |
| Transport | Extract the status/response mapping into a pure function and test that; the socket itself stays untested |

This does not relax the "no bad tests" rule: a test that would pass when every
method returns nothing is still worthless.

## Debt and decisions (ADR-lite)

Write a five-line record in `docs/adr/NNN-title.md` when you:

- add or move a layer, folder, or namespace;
- declare a new port or a new piece of shared state;
- defer a violation you found while touching a file;
- change the HTTP surface.

```markdown
# NNN: <decision>

- Context: <the constraint, in one sentence>
- Decision: <what we do now>
- Consequences: <what becomes easy, what becomes expensive>
- Debt: <known violations or follow-up, or "none">
```

## Review checklist for any change

- [ ] New code sits in the correct layer and the dependency arrows are respected.
- [ ] No new `Dictionary<string, object?>` field bags; touched ones are converted.
- [ ] Status codes and JSON writing exist only in the transport.
- [ ] No game type appears in `Core` or `UseCases`.
- [ ] The change is idempotent or the reason it cannot be is stated.
- [ ] Violations found in touched files were fixed or recorded in `docs/adr/`.
- [ ] A structural change was not smuggled into a feature change.
