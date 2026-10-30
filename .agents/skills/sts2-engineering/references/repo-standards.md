# Repository standards

Adapted from [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture)
(MIT). That template is an ASP.NET Core solution; what transfers is the build
hygiene, the naming and file layout, the one-endpoint-one-shape rule, the
exception-to-status mapping, and the per-layer test layout. EF Core, MediatR,
AutoMapper, Aspire, SPA front ends, and the ASP.NET host are not adopted.
Section 3 (C# coding style) is adapted from the
[dotnet/runtime coding guidelines](https://github.com/dotnet/runtime/tree/main/docs/coding-guidelines)
(MIT); that document is the tie-breaker for a case this section does not cover.

Layer name mapping, so the two upstream templates stay readable:

| jasontaylordev | ardalis | here |
| --- | --- | --- |
| `Domain` | `Core` | `Core/` |
| `Application` | `UseCases` | `UseCases/` |
| `Infrastructure` | `Infrastructure` | `Infrastructure/` |
| `Web` | `Web` | `Transport/` |

## 1. Build and toolchain

`global.json`, `Directory.Packages.props`, `Directory.Build.props`,
`Directory.Build.targets`, and `.editorconfig` are committed. Adding or extending
any of them is a small, standalone change — allowed, but never inside a feature
PR:

| File | Standard |
| --- | --- |
| `global.json` | Pin the SDK band the build uses (`10.0.100` today) with `rollForward: latestFeature`; the TFM stays `net9.0` because that is the runtime the game hosts, and ADR 0007 records the split. Do not float across major versions |
| `Directory.Build.props` | Configuration layer: resolve `STS2GameDir` from `-p:STS2GameDir`, `STS2_GAME_DIR`, or gitignored `build.local.props`, and derive the platform data directory; never commit a machine path. Do **not** set `ArtifactsPath` without updating the Makefile, `build.ps1`, and the README install steps |
| `Directory.Build.targets` | Analyzer references, `AnalysisMode`/`AnalysisLevel`, the warnings-as-errors gate, and the banned-API list |
| `Directory.Packages.props` | Central NuGet versions, currently the analyzer set; game DLLs stay direct `Reference` items with `Private=false` |
| `.editorconfig` | 4-space C#, LF, final newline, system usings first; `dotnet_style_readonly_field = true:warning` |

Rules that hold with or without those files:

- The build stays at **zero warnings**. Do not silence one with
  `#pragma warning disable` unless the comment names the warning and the reason.
- Never commit `bin/`, `obj/`, `out/`, `mcp/.venv/`, or extracted game content.
- A new dependency needs a written reason. Game assemblies are never copied into
  the repository.

## 2. Naming and file layout

- One public type per file, file name equal to the type name. The existing
  `McpMod.*.cs` partials are the recorded exception (see the deferred project
  split); new files and new types follow the rule.
- Folders are layers: `Core/Contracts/<Area>.cs`, `UseCases/<Area>/<Verbs>Handler.cs`,
  `Infrastructure/<Area>/`, `Transport/`. Namespaces match the folder path.
- Identifier names follow section 3: private fields `_camelCase`, static fields
  `s_name`, constants and methods `PascalCase`, interfaces `I`-prefixed, async
  methods `...Async`, booleans read as assertions (`isReady`, not `flag`).
- No `#region` to hide size. A file that needs regions needs splitting.
- JSON is `snake_case` and documented in `docs/raw-*.md`; C# members stay
  `PascalCase`. Pick one mapping mechanism (naming policy or attributes) and never
  hand-write the same name twice.

## 3. C# coding style

Adapted from [dotnet/runtime: C# Coding Style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)
(MIT). Upstream's baseline is "Visual Studio defaults"; the rules below are the
ones that actually decide a review here. `.editorconfig` is the executable copy —
when a rule and that file disagree, fix the file in a standalone change, never by
ignoring the rule inside a feature PR.

### 3.1 Layout and formatting

- **Allman braces**: every brace on its own line, 4 spaces of indentation, no
  tabs. At most one blank line between members; no stray whitespace.
- **One statement per line.** The single-line form `if (x) return y;` is banned.
  Braces may be omitted only when every block in an `if`/`else if`/`else` chain
  (and in a `for`, `while`, `using`, or `try`) has a one-line body written on its
  own line; the moment one block uses braces or a body spans lines, every block in
  that chain gets braces.
- **Usings** stay at the top of the file, outside the namespace, alphabetically
  sorted with `System.*` first. The mod uses **file-scoped namespaces**
  (`namespace Sts2Mcp;`); a test harness still on block-scoped syntax is
  converted when the change touches it.
- **One public type per file**, named after the type; the `McpMod.*.cs` partials
  remain the recorded exception. No `#region` to hide size.
- **Non-ASCII source text** is written as `\uXXXX` escapes rather than literal
  characters: a tool on this pipeline garbles them, and JSON/log strings are the
  usual casualty.
- `dotnet format` on a touched file must be a no-op. If a file you touch formats
  wrong, fix it in the same change; when the reformat would drown the behavior
  diff, give it its own commit inside the same PR so the reviewer sees both.

### 3.2 Declarations, visibility, fields

- **Always state the visibility**, even where it is the default, and put it first:
  `private string _foo`, `public abstract`, never `abstract public` or bare
  `string _foo`.
- **Fields sit at the top of the type**, before properties, constructors, and
  methods.
- **Field naming**: private and internal instance fields `_camelCase`, private and
  internal static fields `s_name`, thread-static fields `t_name`. Use `readonly`
  wherever it fits and write it after `static` (`private static readonly`, never
  `readonly static`). Public fields are rare and `PascalCase` with no prefix;
  prefer a property.
- **No `this.`** unless a parameter or local shadows the member and the code
  cannot be renamed.
- **`PascalCase` for constants and all method names**, including local functions
  and constant locals. The only exception is interop where the upstream name and
  value must match exactly (P/Invoke entry points, raw game member strings).
- **`nameof(...)` instead of repeating an identifier as a string literal** in an
  argument, attribute, or exception message.
- **Keywords, not BCL type names**: `int`, `string`, `float`, `int.Parse`, not
  `Int32`, `String`, `Single`.
- **`sealed` or `static` for internal and private types** when nothing derives
  from them: a helper with no subclass is `sealed`, a bag of pure helpers is
  `static`.
- **Primary constructor parameters** are `camelCase` and unprefixed. If the type
  is large enough that a parameter's use is not obvious, assign it to a
  `_`-prefixed field in the body (`private readonly X _x = x;`).

### 3.3 `var`, `new()`, and types at the call site

- **`var` only when the type is named on the right-hand side** — `new`, an
  explicit cast, or `default(T)`: `var stream = new FileStream(...)` yes,
  `var stream = OpenStandardInput()` no.
- **Target-typed `new()` only when the type is named on the left-hand side** of
  the same variable or field declaration: `FileStream stream = new(...);` yes,
  `stream = new(...);` on a later line no.
- Prefer the concrete type in a signature and in a `Dictionary`/`List`
  declaration; `object`/`dynamic` is a boundary decision, not a convenience (see
  "Type discipline" in `SKILL.md`).

### 3.4 No grandfathering: fix it when you see it

- **Wrong names and wrong structure are repaired immediately.** A file that has
  been `_`-prefixed on a static field, block-scoped in its namespace, brace-style
  inconsistent, or shaped against section 3 is fixed in the change that touches
  it. "It was already like that" is not an argument, and the fix is not deferred
  to a later cleanup PR.
- **A breaking rename is the expected cost.** Rename the type, field, or member
  to the correct name and update every call site in the same change; let the
  zero-warning build prove the rename is complete. Do not keep the wrong name,
  add an alias, or leave a `// old name` comment to avoid touching call sites.
  If the rename is too large for one commit, split it into commits that each
  compile, with the last one removing the old name — the PR finishes the job.
- **Contract and interop names are not style violations.** Reflection-reached
  game members, `snake_case` JSON, and `docs/raw-*.md` field names stay exactly
  as the external contract requires. Renaming one of those is a contract change
  with its own schema/version decision, not a cleanup.
- **No style rule justifies a warning.** The gate is `TreatWarningsAsErrors` plus
  the analyzers in `Directory.Build.targets`; a finding is fixed in code or the
  rule is fixed in `.editorconfig` with its reason written down — never silenced
  with `#pragma` in a feature change.

## 4. Endpoint shape

One endpoint = one request type + one result type + one handler + one row in
`docs/raw-*.md`. The transport layer does exactly five things: parse, validate the
shape, call the use case, map the outcome to a status, serialize.

| Outcome | Status |
| --- | --- |
| Success | `200` |
| Malformed body, wrong type, missing required field | `400` |
| Valid request the current state refuses (wrong mode, locked option, not actionable) | `409` |
| Unknown route | `404` |
| Unexpected exception | `500` |

- The documented error shape is `{"status": "error", "error": "<human message>"}`.
  Do **not** add `exception_type`, `stack_trace`, or any internal path to a
  response: the API is unauthenticated on localhost, and those fields are not part
  of the contract.
- Do not return `200` with an `error` member for a failure a status code can
  express. The documented exceptions (for example `inventory` still loading) stay
  as documented, and new endpoints do not invent more.
- Omit absent optional fields instead of writing `null`, matching the current
  documented contract.
- Validate before mutating. Never leave half-applied state behind a `400`/`409`.

## 5. Validation split

| Where | What | Failure |
| --- | --- | --- |
| Transport | Shape, types, required fields, route/body match | `400` |
| Use case | Whether the command is legal in the current state | `409` |
| Inside | Invariants the caller cannot violate | Exception |

Never trust `format`, `index`, `target`, or `seed` without parsing them. A default
value is a decision: write down which value is chosen when the field is absent.

## 6. Errors and logging

- One exception-to-outcome mapping lives in the transport. The 482 scattered
  `try/catch` blocks that build their own error dictionaries are legacy, not a
  pattern to copy.
- Log at the boundary once per failure: `[STS2 MCP] <operation>: <reason>`. Do not
  log the same exception again deeper in the call chain.
- Clients get the documented error message; the log gets the exception. Never the
  other way around.
- Treat every request as untrusted input even though it comes from localhost.

## 7. Composition and lifetime

- One composition root: the transport startup constructs use cases with their
  port implementations. No service locator, no hidden static wiring for new code.
- The static `McpMod` surface is the legacy exception. New shared mutable state
  goes through the main-thread queue, not into another `static` field.
- A per-request object must never be captured by a long-lived static; that is a
  data leak and a cross-request mutation bug.

## 8. Tests by layer

Reserved layout, once a test project is justified:

| Project | Covers | Notes |
| --- | --- | --- |
| `tests/Sts2Mcp.UnitTests` | `Core` contracts and `UseCases` through port fakes | No game types, no Godot |
| `tests/Sts2Mcp.IntegrationTests` | Transport status/response mapping over a fake request | No socket required |

There is no automated project for `Infrastructure`: it is verified in a running
game. Until a test project is justified, the headless harnesses under `tests/`
compile production sources against stand-in game types and are the gate
(`make test`); they are not the reserved `Sts2Mcp.UnitTests` layout yet.
The framework choice is a separate decision (the upstream templates use
NUnit or xUnit); do not reopen it in a feature PR. The name of a test says the
behavior: `Returns409_WhenRunIsMultiplayer`, not `Test2`.

## 9. Docs and decisions

- `docs/raw-full.md` and `docs/raw-simplified.md` are the contract; a surface
  change updates both plus the `mcp/server.py` docstrings.
- `docs/adr/NNN-title.md` records structural decisions, new ports, new shared
  state, API-surface changes, and every violation deliberately deferred.
- The README line that names the game build ("Builds against STS2 `vX.Y.Z`") stays
  true; it is this repository's "tested against" statement.
- Do not invent a changelog or a versioning scheme that the project does not use.

## 10. Git hygiene

- One logical change per PR, squash merge, message states why / what / how proven.
- No generated files, game content, absolute local paths, or secrets.
- Discussion first for anything that changes the API surface.

## 11. Compliance checklist

Run before claiming a change is done:

- [ ] `dotnet build` (or `./build.ps1 -GameDir ...`) is clean, zero warnings.
- [ ] `grep -rn "stack_trace\|exception_type" McpMod*.cs` returns nothing for
      response bodies; the log may still carry the exception.
- [ ] `grep -c "Dictionary<string, object?>" McpMod*.cs` did not grow; a touched
      response was converted.
- [ ] Boundary `grep` checks from `references/architecture.md` are clean.
- [ ] A new endpoint has a request type, a result type, a handler, and a docs row.
- [ ] New names and files follow section 2.
- [ ] New and edited C# follows section 3: explicit visibility, `_`/`s_` field
      prefixes, fields at the top, Allman braces, `var`/`new()` only where the
      type is named on the other side, no `this.`.
- [ ] A naming or structure violation in code you touched is fixed in this
      change, with every call site renamed and the build proving it — not
      inherited into the next file.
- [ ] `dotnet format --verify-no-changes --include <touched files>` is clean, or
      the reason it was not run is stated.
- [ ] No new `#pragma warning disable`; a rule that had to change is in
      `.editorconfig` with its reason written down.
- [ ] `docs/raw-*.md` and `mcp/server.py` docstrings match the response.
- [ ] Deliberate structural exceptions (the deferred project split, the
      single-assembly layering) are in `docs/adr/`.

## Known violations (observed 2026-10-30, re-verify before acting)

These are real and already in the tree. They are listed so that whoever touches
them repairs them in that same change, not so that they get copied or deferred:

1. 1,351 `Dictionary<string, object?>` occurrences: only `/api/v1/settings` has a
   typed contract, so field drift stays invisible to the compiler. Recorded in
   ADR 0004/0005; convert one endpoint per change with its before/after response.
2. `UseCases/`, `Infrastructure/` and `Transport/` do not exist yet, so their
   boundary `grep` checks stay vacuous; `Core/` itself is clean. Recorded in
   ADR 0004/0005.
3. The headless harnesses under `tests/` pin the contract shapes and the pure
   seams; most of the mod still has no automated test.
4. One transport handler still blocks on async work
   (`GetAwaiter().GetResult()`); new code must not add another.
5. `scripts/audit_endpoints.py` is a static gate: it compares routes, docs and
   helpers, and never observes a running game.
6. `.editorconfig` does not pin `charset`: some `.cs` files carry a UTF-8 BOM and
   normalizing them is its own commit.
