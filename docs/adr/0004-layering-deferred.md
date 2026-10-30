# 0004: The layered split stays deferred after the absorption

- Context: `references/architecture.md` records that the `Core`/`UseCases`/
  `Infrastructure`/`Transport` split is its own discussed change. This branch just
  tripled the surface it would have to split, and no automated tests exist to pin the
  behaviour while files move.
- Decision: do not restructure now. The rules still apply to new code, and the violations
  touched by this branch were repaired (the error bodies no longer return `error_type` /
  `stack_trace`). The first slice, when it happens, is the typed contracts
  (`Core/Contracts`) for the responses that `docs/raw-*.md` already documents, with the
  existing HTTP responses compared before and after.
- Consequences: the branch stays reviewable as a feature merge; the refactor does not
  ride along unverified.
- Debt: 903 `Dictionary<string, object?>` bags; no `Core/`, `UseCases/`,
  `Infrastructure/` or `Transport/` folders yet; the boundary `grep` checks are therefore
  vacuous until the folders exist. `docs/raw-full.md` now carries Japanese-localised
  sections from `cat12079801` and needs a language pass.
