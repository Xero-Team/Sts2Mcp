# Provenance

This skill fuses four upstreams and adapts them to Sts2Mcp.

## Clean Architecture (jasontaylordev)

- Source: https://github.com/jasontaylordev/CleanArchitecture (template, ASP.NET Core 10)
- License: MIT
- Handling: **no scaffold or file was copied.** Extracted and re-scoped:
  `Directory.Build.props` / `global.json` / `.editorconfig` hygiene,
  `TreatWarningsAsErrors`, one-type-per-file naming, the request/response/handler
  endpoint shape, the exception-to-status mapping (`400` validation, `404`
  missing, `409`-style state conflicts, `500` unexpected), the ban on leaking
  internal exception detail to clients, per-layer test project naming, and ADRs.
  Rejected: EF Core, MediatR, AutoMapper, FluentValidation packages, Aspire, SPA
  front ends, ASP.NET hosting, and the `Domain/Application/Infrastructure/Web`
  rename (the skill keeps `Core/UseCases` and documents the mapping).
- Layer naming is mapped explicitly in `references/repo-standards.md` so both
  upstream templates stay readable.

## Clean Architecture (ardalis)

- Source: https://github.com/ardalis/CleanArchitecture (template, ASP.NET Core 10)
- License: MIT
- Handling: **no scaffold or file was copied.** The extracted rules are the
  dependency direction, the REPR endpoint shape (one request, one response, one
  handler), validation at the boundary with invariant exceptions inside, a
  testable core, and ADRs. Everything ASP.NET Core specific — the Core/UseCases/
  Infrastructure/Web project split, DI container, Mediator, EF Core, FastEndpoints,
  Aspire, test-project layout — was deliberately rejected for this repository and
  the reason is recorded in `references/architecture.md`.
- The repository's end-state layering is recorded as a decision, not applied: new
  code obeys the rules today, existing violations are repaired when touched, and
  the project split is its own planned change.

## ub-mode

- Source: https://github.com/unclebob/ub-mode
- `SKILL.md` upstream blob: `9ac940b23b61d77e8e5192721d7b6066357f892c`, repo HEAD `57a8bdd` (2026-10-25)
- License: none declared upstream (the license API returns 404, the repository
  contains only `README.md` and `SKILL.md`)
- Handling: the principles are **paraphrased and re-scoped**, not copied. The
  three passes (First make it work, then make it right, follow practice) and the
  discipline names are credited to ub-mode; the wording, examples, and gates in
  this skill are written for this repository. Do not paste upstream text in later
  —- paraphrase again, and keep this attribution.
- Upstream companion tools (`crapper`, `dryer`, `mutator`, `uml-viewer`) are
  **not** vendored or installed here. This skill only references them
  conditionally, because none of them is wired into this repository.

## REA / reverse-engineer-anything

- Source: https://github.com/morluto/rea/tree/main/.agents/skills/reverse-engineer-anything
- `SKILL.md` upstream blob: `9da1eed639fc08d1e8063dd2f19f7dfbd5086a1c` (metadata version `55`)
- License: MIT (upstream `LICENSE`; this repository is MIT as well)
- Handling: the routing, evidence rules, and the three copied reference pages are
  upstream material, MIT-licensed and attributed here. The `SKILL.md` itself was
  rewritten and merged into the engineering passes rather than copied.

## dotnet/runtime coding guidelines

- Source: https://github.com/dotnet/runtime/tree/main/docs/coding-guidelines
  (`coding-style.md`, the C# style page)
- License: MIT
- Handling: **no text or file was copied.** The rules were paraphrased and
  re-scoped to the decisions that actually come up in this repository: Allman
  braces and single-statement chains, explicit visibility, field placement and
  `_camelCase` / `s_` / `t_` prefixes, `PascalCase` constants and methods,
  `nameof` over repeated literals, `var`/target-typed `new()` only when the type
  is named on the other side, `sealed`/`static` internal types, `\uXXXX` for
  non-ASCII source. Upstream's "existing style in that file takes precedence"
  rule is deliberately **not** adopted: a wrong name or structure found in
  touched code is repaired in that change, breaking rename included. The rules
  live in `references/repo-standards.md` section 3; `.editorconfig` is the
  executable subset. Upstream's tooling claims (`dotnet format` in CI) are not
  imported as gates: this repository has no CI workflow.

## What changed for Sts2Mcp

- One skill replaces the earlier `reverse-engineer-anything` copy: the ub-mode
  working discipline and the REA evidence route are now a single workflow, so
  "route the question first" can refuse an unnecessary reverse-engineering
  session before any work starts.
- Repository-specific adaptations:
  - TDD is replaced by the honest check this repository actually has: reproduce
    with the running mod's API or a screen, then show the changed observation.
    `CONTRIBUTING.md` states the manual in-game check is the test.
  - `crapper` / `dryer` / `mutator` gates are reported as unavailable instead of
    being installed; the real gates are a zero-warning build, docs sync, and an
    exercised in-game path.
  - "One writer" and "separate shared state" map onto the existing main-thread
    queue in `McpMod.cs` (HTTP worker threads never touch game objects).
  - "Model the domain" and "type discipline" map onto the documented JSON
    contract in `docs/raw-*.md` and the thin Python pass-through rule.
  - "Guard the context window" names the actual large files
    (`McpMod.StateBuilder.cs`, `McpMod.Formatting.cs`, `docs/raw-full.md`).
  - Commit/practice guidance follows this project's contributor rules
    (discussion-first for API surface, focused PRs, squash merge, no upstream
    pushes).
  - Game-build routing keeps the STS2 target map, path resolution, and
    version-local evidence rules from the previous copy.
- Added `references/repo-standards.md`: build hygiene targets, naming and file
  layout, the dotnet/runtime-derived C# coding style, endpoint and error-body
  standards, validation split, logging, one composition root, test project
  layout, git hygiene, a runnable compliance checklist, and the known violations
  observed in the tree on 2026-10-30.
- Added `references/architecture.md`: the layered target, the dependency rule,
  runnable boundary checks for the single-assembly era, the ADR-lite template,
  tests by layer, and how to handle a violation found in touched code.
- Added `references/sts2-game-artifacts.md`: task-to-evidence-to-file recipes.
- Copied from upstream REA, with one relative link to the upstream JavaScript
  reference retargeted to an absolute URL:
  `references/native-and-artifacts.md`, `references/evidence-workflows.md`,
  `references/connection-and-recovery.md`.

## Upstream references not copied

- https://github.com/morluto/rea/blob/main/.agents/skills/reverse-engineer-anything/references/android-applications.md
- https://github.com/morluto/rea/blob/main/.agents/skills/reverse-engineer-anything/references/javascript-applications.md
- https://github.com/morluto/rea/blob/main/.agents/skills/reverse-engineer-anything/references/runtime-observation.md
- https://github.com/morluto/rea/blob/main/.agents/skills/reverse-engineer-anything/references/environments.md

## Updating

Re-apply the adaptation instead of overwriting this directory.

1. Diff upstream `SKILL.md` against the blobs recorded above.
2. Re-merge changed principles into the matching pass in this `SKILL.md`, in this
   repository's wording.
3. Refresh the three copied REA reference files if they changed.
4. Re-check the tool claims in `references/sts2-game-artifacts.md` against the
   tool set the installed `rea-agents` release actually advertises, and re-check
   the gate claims against what this repository really runs.
