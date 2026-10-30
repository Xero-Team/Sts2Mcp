# AI contribution reference

Policy sources: `AGENTS.md` (agent behaviour), `CONTRIBUTING.md` (what can land),
`CODE_OF_CONDUCT.md` (conduct). Commit-message shape:
`.agents/shared/conventional-commit/REFERENCE.md`. Cite them; do not paste them
into a prompt.

## Load this when

The agent will review its own change, commit, push a branch, open an Issue or
Pull Request, or decide whether work is ready to hand off.

## Default posture: review and commit on your own

Self-review is not a permission request. When the user asked for a change and the
work is one logical unit:

1. Read the full diff (`git diff`, not just the files you remember editing).
2. Run the gates that apply: `dotnet build` with zero warnings, `docs/raw-full.md`
   and `docs/raw-simplified.md` synced for any API change, `mcp/server.py`
   docstrings updated, and the manual in-game check for behavior changes.
3. Check the change against `.agents/skills/sts2-engineering/SKILL.md` — layer
   rule, idempotency, main-thread game access, one-writer state.
4. Commit with a message from
   `.agents/shared/conventional-commit/REFERENCE.md`, one concern per commit.

Do this without asking. A commit that is wrong is cheaper to fix than a stalled
task, and the record of the review belongs in the commit body and PR text.

Ask before acting only when:

- the change alters the API surface — `CONTRIBUTING.md` requires a Discussion or
  issue first for new endpoints, state fields, and actions;
- the only honest check needs a live game launch or the game install directory;
- the change reaches outside this repository, or needs a push, merge, or release;
- the intent, scope, or file set is ambiguous enough that guessing would waste
  the user's review time.

"Ask before acting" never means "ask before thinking". State what you verified,
what you could not, and the exact blocker.

## Locked

- Agents **may** edit, build, run gates, commit, and — when the user names the
  remote and branch — push a feature branch. Agents may open development Issues
  and Pull Requests on `Gennadiyev/STS2MCP` when the user asks for them. Confirm
  the created URL before reporting it.
- Agents **must not** push `main`, force-push any branch, merge a PR, tag,
  publish a release, change branch protection or secrets, or file a public
  security report. A human maintainer does those. A verbal exception is not
  authorization.
- English Conventional Commits. Agent-finalized messages carry `AI-Generated:
  true` and a UTC `Generated-At:` footer from `date -u '+%Y-%m-%dT%H:%M:%SZ'`.
- Human authors add `## Human note` in their own words; agent authors add
  `## Agent note`. Never fabricate the other. This repository has no PR template,
  so keep that section short and inside the normal PR body.
- Cite `AGENTS.md`, the named skills, and the named docs. Do not dump them.

## Open

Which branch, how many commits, and whether a push or PR happens in this session
are open unless the user already decided. Prefer one concern per commit and a
subject that states the outcome.

## Do not

- Merge, even when the build is green.
- Write inside the game install directory, or launch the game as a substitute for
  a check you could have run on source.
- Claim a gate passed unless this session ran it.
- Add tests that mock the game to look green; `CONTRIBUTING.md` says the manual
  in-game check is the test.
- Bump `mod_manifest.json` or the README "Tested against" line for a change that
  does not affect what players install.

## Handoff

- Commit message shape: `.agents/shared/conventional-commit/REFERENCE.md`.
- Code rules and layer gates: `.agents/skills/sts2-engineering/SKILL.md`.
- Contribution and review rules: `CONTRIBUTING.md`.
- Security review of a change or the repository:
  `.agents/skills/security-audit/SKILL.md`.
- Merge decision: stop and name the human maintainer. Do not self-merge.

## Upstream notes

Adapted from `Xero-Team/AstrBot`'s `.agents/shared/ai-contribution/REFERENCE.md`
(MIT). Rules that only made sense there — `AI_POLICY.md`, upstream-sync skill,
plugin packages, bilingual docs, typed PR templates — are omitted because this
repository has no such surface.
