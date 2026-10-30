# Repository standards

Adapted from [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture)
(MIT). That template is an ASP.NET Core solution; what transfers is the build
hygiene, the naming and file layout, the one-endpoint-one-shape rule, the
exception-to-status mapping, and the per-layer test layout. EF Core, MediatR,
AutoMapper, Aspire, SPA front ends, and the ASP.NET host are not adopted.

Layer name mapping, so the two upstream templates stay readable:

| jasontaylordev | ardalis | here |
| --- | --- | --- |
| `Domain` | `Core` | `Core/` |
| `Application` | `UseCases` | `UseCases/` |
| `Infrastructure` | `Infrastructure` | `Infrastructure/` |
| `Web` | `Web` | `Transport/` |

## 1. Build and toolchain

`global.json` and `Directory.Packages.props` do not exist in this repository yet.
`Directory.Build.props`, `Directory.Build.targets`, and `.editorconfig` are committed.
Adding or extending any of them is a small, standalone change — allowed, but
never inside a feature PR:

| File | Standard |
| --- | --- |
| `global.json` | Pin the SDK family the project targets (`net9.0` today) with `rollForward: latestFeature`; do not float across major versions |
| `Directory.Build.props` | Configuration layer: resolve `STS2GameDir` from `-p:STS2GameDir`, `STS2_GAME_DIR`, or gitignored `build.local.props`, and derive the platform data directory; never commit a machine path. Do **not** set `ArtifactsPath` without updating the Makefile, `build.ps1`, and the README install steps |
| `Directory.Build.targets` | Analyzer packages, `AnalysisMode`/`AnalysisLevel`, the warnings-as-errors gate, and the banned-API list |
| `Directory.Packages.props` | Only when the first NuGet dependency appears; game DLLs stay direct `Reference` items with `Private=false` |
| `.editorconfig` | 4-space C#, LF, final newline, system usings first; `dotnet_style_readonly_field = true:warning` |

Rules that hold with or without those files:

- The build stays at **zero warnings**. Do not silence one with
  `#pragma warning disable` unless the comment names the warning and the reason.
- Never commit `bin/`, `obj/`, `out/`, `mcp/.venv/`, or extracted game content.
- A new dependency needs a written reason. Game assemblies are never copied into
  the repository.

## 2. Naming and file layout

- One public type per file, file name equal to the type name. The existing
  `McpMod.*.cs` partials are the grandfathered exception; new files follow the
  rule.
- Folders are layers: `Core/Contracts/<Area>.cs`, `UseCases/<Area>/<Verbs>Handler.cs`,
  `Infrastructure/<Area>/`, `Transport/`. Namespaces match the folder path.
- Private fields `_camelCase`, constants `PascalCase`, interfaces `I`-prefixed,
  async methods `...Async`, booleans read as assertions (`isReady`, not `flag`).
- No `#region` to hide size. A file that needs regions needs splitting.
- JSON is `snake_case` and documented in `docs/raw-*.md`; C# members stay
  `PascalCase`. Pick one mapping mechanism (naming policy or attributes) and never
  hand-write the same name twice.

## 3. Endpoint shape

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

## 4. Validation split

| Where | What | Failure |
| --- | --- | --- |
| Transport | Shape, types, required fields, route/body match | `400` |
| Use case | Whether the command is legal in the current state | `409` |
| Inside | Invariants the caller cannot violate | Exception |

Never trust `format`, `index`, `target`, or `seed` without parsing them. A default
value is a decision: write down which value is chosen when the field is absent.

## 5. Errors and logging

- One exception-to-outcome mapping lives in the transport. The 482 scattered
  `try/catch` blocks that build their own error dictionaries are legacy, not a
  pattern to copy.
- Log at the boundary once per failure: `[STS2 MCP] <operation>: <reason>`. Do not
  log the same exception again deeper in the call chain.
- Clients get the documented error message; the log gets the exception. Never the
  other way around.
- Treat every request as untrusted input even though it comes from localhost.

## 6. Composition and lifetime

- One composition root: the transport startup constructs use cases with their
  port implementations. No service locator, no hidden static wiring for new code.
- The static `McpMod` surface is the legacy exception. New shared mutable state
  goes through the main-thread queue, not into another `static` field.
- A per-request object must never be captured by a long-lived static; that is a
  data leak and a cross-request mutation bug.

## 7. Tests by layer

Reserved layout, once a test project is justified:

| Project | Covers | Notes |
| --- | --- | --- |
| `tests/STS2_MCP.UnitTests` | `Core` contracts and `UseCases` through port fakes | No game types, no Godot |
| `tests/STS2_MCP.IntegrationTests` | Transport status/response mapping over a fake request | No socket required |

There is no automated project for `Infrastructure`: it is verified in a running
game. The framework choice is a separate decision (the upstream templates use
NUnit or xUnit); do not reopen it in a feature PR. The name of a test says the
behavior: `Returns409_WhenRunIsMultiplayer`, not `Test2`.

## 8. Docs and decisions

- `docs/raw-full.md` and `docs/raw-simplified.md` are the contract; a surface
  change updates both plus the `mcp/server.py` docstrings.
- `docs/adr/NNN-title.md` records structural decisions, new ports, new shared
  state, API-surface changes, and every violation deliberately deferred.
- The README "Tested against" line names the game build; keep it true.
- Do not invent a changelog or a versioning scheme that the project does not use.

## 9. Git hygiene

- One logical change per PR, squash merge, message states why / what / how proven.
- No generated files, game content, absolute local paths, or secrets.
- Discussion first for anything that changes the API surface.

## 10. Compliance checklist

Run before claiming a change is done:

- [ ] `dotnet build` (or `./build.ps1 -GameDir ...`) is clean, zero warnings.
- [ ] `grep -rn "stack_trace\|exception_type" McpMod*.cs` returns nothing for
      response bodies; the log may still carry the exception.
- [ ] `grep -c "Dictionary<string, object?>" McpMod*.cs` did not grow; a touched
      response was converted.
- [ ] Boundary `grep` checks from `references/architecture.md` are clean.
- [ ] A new endpoint has a request type, a result type, a handler, and a docs row.
- [ ] New names and files follow section 2.
- [ ] `docs/raw-*.md` and `mcp/server.py` docstrings match the response.
- [ ] Structural changes and deferred violations are in `docs/adr/`.

## Known violations (observed 2026-10-30, re-verify before acting)

These are real and already in the tree. They are listed so that whoever touches
them repairs them, not so that they get copied:

1. `McpMod.cs:320` and `McpMod.cs:421` return `exception_type` and `stack_trace`
   to any caller; neither field is documented in `docs/raw-full.md`.
2. 482 `Dictionary<string, object?>` occurrences: there is no typed response
   contract, so field drift is invisible to the compiler.
3. No `global.json`: the SDK is whatever is on PATH. `Directory.Build.props`,
   `Directory.Build.targets`, and `.editorconfig` now carry the build rules.
4. The headless harnesses under `tests/` cover the contract and pure seams; most
   of the mod still has no automated test.
5. No `docs/adr/`, so the deferred split and the legacy exceptions are unwritten.
6. Transport blocks on async work (`GetAwaiter().GetResult()`); new code must not
   add another blocking wait.
