# Conventional Commits: AI Reference

Use this reference when generating, reviewing, or classifying Git commit
messages for Sts2Mcp.

`CONTRIBUTING.md` requires no convention — a readable human sentence is enough,
and `main` is squashed on merge, so branch commits do not need to be polished.
Agents still use this shape by default so the history stays machine-readable and
reviewable. Human contributors may ignore it entirely.

## 1. Required Header Format

```text
<type>(<optional scope>): <description>
```

Examples:

```text
feat(compendium): add compendium endpoint
fix(state): report 'map' after Crystal Sphere proceed
docs: clarify local setup steps
```

Rules:

- `type` is required.
- `scope` is optional and gives local context; use only the scopes defined in
  section 2.
- Do **not** use issue identifiers as scopes. Use a footer instead, for example
  `Fixes #123`.
- `description` is required.
- Add `!` immediately before `:` when the change is breaking:

```text
feat(api)!: rename the card-select preview field
```

## 2. Choosing the Type

Classify the change by its primary intent. Apply this decision order:

1. Did it fix a bug? → `fix`
2. Did it change functionality or the API surface? → `feat`
3. Did it add or change tests? → `test`
4. Did it only change style or formatting? → `style`
5. Did it only change documentation? → `docs`
6. Did it change build tooling, the csproj, NuGet packages, or `build.ps1`? →
   `build`
7. Was it maintenance or another non-code task? → `chore`
8. Was it rewritten specifically for performance? → `perf`
9. Otherwise → `refactor`

| Type       | Use when                                                                                              | Do not use when                                    |
| ---------- | ----------------------------------------------------------------------------------------------------- | -------------------------------------------------- |
| `feat`     | Adding, changing, or removing an endpoint, state field, action, or screen control                      | The change merely corrects an existing defect      |
| `fix`      | Correcting a bug in the API, the state payload, an action, or the Python bridge                        | Adding a new capability                            |
| `refactor` | Restructuring C# or Python without changing the HTTP contract or game behavior                          | The primary intent is performance improvement      |
| `perf`     | Reducing serialization cost, allocations, or payload size without intended behavior change              | The change is a general cleanup                    |
| `test`     | Adding or correcting tests that need no live game, such as pure transforms in `mcp/server.py`           | Product code changes are the primary purpose       |
| `style`    | Formatting, whitespace, lint-only changes, import ordering, with no behavior change                     | Code restructuring or functional changes           |
| `docs`     | `README.md`, `CONTRIBUTING.md`, `AGENTS.md`, `docs/raw-*.md`, or skill text only                        | Any code or configuration behavior change          |
| `build`    | `Sts2Mcp.csproj`, `Sts2Mcp.sln`, `build.ps1`, NuGet references, `mod_manifest.json`, packaging         | Game behavior or API changes                       |
| `chore`    | Repository maintenance such as `.gitignore`, tooling config, or skill-file housekeeping                 | A more specific type accurately applies            |

Scopes in this repository: `state`, `actions`, `multiplayer`, `profile`,
`compendium`, `wiki`, `mcp`, `build`, `docs`, `agents`. Omit the scope when none
of these fits; do not invent a new one for a one-off change.

## 3. Description Rules

Write a concise command-style description.

- Use imperative present tense: `add`, `fix`, `remove`, `update`, `prevent`.
- Imagine the phrase: "This commit will …".
- Start with lowercase.
- Do not end with a period.
- State the outcome or intended change, not implementation trivia unless that is
  the relevant outcome.
- Keep the header focused on one logical change.

Good:

```text
fix(state): report 'map' after Crystal Sphere proceed
feat(actions): expose card-select preview cards
perf(state): drop duplicate relic lookups per frame
```

Avoid:

```text
Fixed bug.                         # past tense, capitalized, period, vague
feat: Changes stuff                # capitalized and vague
update files                       # no type and lacks context
fix: fixed the issue               # redundant and vague
```

## 4. Optional Body and Footer

Use blank lines to separate the header, body, and footer.

```text
<type>(<scope>): <description>

<optional body>

<optional footer>
```

### Body

Use a body when a short header cannot explain the motivation, important tradeoff,
or difference from previous behavior. Write it in imperative present tense.

```text
fix(state): report 'map' after Crystal Sphere proceed

Return the map room once the Crystal Sphere screen closes so clients stop
polling a stale phase.
```

### Footer

Use footers for issue references, breaking-change details, and AI-generation
metadata.

```text
feat(api)!: rename the card-select preview field

Rename `preview_cards` to `card_select_preview` so the payload matches
`docs/raw-full.md`.

BREAKING CHANGE: clients must read `card_select_preview`
Fixes #112
AI-Generated: true
Generated-At: 2026-10-30T09:15:00Z
```

Rules for breaking changes:

- A breaking change must include `!` before the header colon.
- A breaking change must include a `BREAKING CHANGE:` footer unless the header or
  body already explains it sufficiently; including the footer is preferred.
- Write `BREAKING CHANGE:` exactly.

## 5. AI Generation Metadata

When an agent generates, proposes, or finalizes a commit message, include the UTC
generation time and label the commit as AI-generated.

Use this command to obtain the timestamp:

```sh
date -u '+%Y-%m-%dT%H:%M:%SZ'
```

Add the result as commit-message footers, after issue references and any
`BREAKING CHANGE:` footer:

```text
AI-Generated: true
Generated-At: <UTC ISO 8601 timestamp>
```

Rules:

- Use UTC only, formatted as ISO 8601: `YYYY-MM-DDTHH:MM:SSZ`.
- Generate the timestamp when the agent prepares the final commit message.
- Keep these fields in the footer, separated from the body by a blank line.
- Do not place timestamps or AI attribution in the header description.

History before this reference does not carry these footers. Do not rewrite old
commits to add them.

## 6. Special Commit Formats

Use Git's standard formats for merge and revert commits rather than forcing the
normal header format.

```text
chore: init
```

```text
Merge branch 'feat/run-control'
```

```text
Revert "feat(actions): expose card-select preview cards"
```

## 7. AI Generation Procedure

Before generating a commit message:

1. Identify every changed file and the user-visible, API-visible, test, or
   maintenance outcome.
2. Select the most specific primary type using the decision flow.
3. Select a scope only from section 2.
4. Determine whether the HTTP contract or `docs/raw-*.md` changed. If clients must
   change, use `!` and a `BREAKING CHANGE:` footer.
5. Write one lowercase imperative description without terminal punctuation.
6. Add a body only for motivation, behavior contrast, constraints, or non-obvious
   consequences.
7. Add issue references only in the footer.
8. Run `date -u '+%Y-%m-%dT%H:%M:%SZ'` and append `AI-Generated: true` plus
   `Generated-At: <result>` in the footer.

Commit only after the change is reviewed against the repository gates, and never
claim a gate ran when it did not: `dotnet build` (zero warnings), `docs/raw-full.md`
and `docs/raw-simplified.md` synced with any API change, `mcp/server.py`
docstrings updated, and the manual in-game check that `CONTRIBUTING.md` requires
for behavior changes.

When a commit contains unrelated changes, split it. If splitting is impossible,
choose the type that best represents the primary purpose and explain secondary
effects in the body.

## 8. Examples

```text
feat(compendium): add compendium endpoint
```

```text
feat(multiplayer): join FastMP localhost lobby
```

```text
fix(actions): guard play-card index against a shifted hand
```

```text
fix(state): report 'map' after Crystal Sphere proceed

Return the map room once the Crystal Sphere screen closes so clients stop
polling a stale phase.

Fixes #73
AI-Generated: true
Generated-At: 2026-10-30T09:15:00Z
```

```text
refactor(state): move run-state assembly behind one builder
```

```text
style: remove extra blank line
```

```text
test(mcp): cover action name coercion
```

```text
build: bump Harmony reference
```

```text
docs(agents): add AI contribution policy
```

```text
chore: ignore local audit output
```

## 9. Validation Checklist

Before returning a message, verify:

- Header matches `<type>(<optional scope>): <description>`.
- Type is one of: `feat`, `fix`, `refactor`, `perf`, `style`, `test`, `docs`,
  `build`, `chore`.
- Scope is optional, from section 2, and not an issue ID.
- Description is lowercase, imperative, concise, and has no final period.
- `!` appears before `:` for breaking changes.
- Breaking changes include a `BREAKING CHANGE:` footer when needed.
- Issue IDs occur in footers, such as `Fixes #123`.
- AI-produced messages include `AI-Generated: true` and a current UTC
  `Generated-At:` footer.
- The chosen type reflects the main intent, not merely the files touched.

## 10. Compatibility and Versioning

This repository has no release automation, so commit types do not drive version
numbers. `mod_manifest.json` holds the mod version and the README carries the
"Tested against" game version; bump them in the same commit that changes what
players install.

Before a commit that changes the API surface (endpoints, state fields, actions),
confirm a Discussion or issue exists, per `CONTRIBUTING.md`. Additive internal
changes do not need one.

## 11. Upstream Notes

This reference is adapted from `Xero-Team/AstrBot`'s
`.agents/shared/conventional-commit/REFERENCE.md` (MIT). Upstream-only rules —
release automation mapping, `ops` type, bilingual docs, PR-type templates — are
intentionally omitted because this repository has no such surface.
