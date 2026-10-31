# 0001: Absorb the twelve active forks onto one branch

- Context: 114 forks exist; 12 carry new commits (453 commits, ~58k inserted lines) and
  none of them had been absorbed upstream. Most touch the same two files
  (`McpMod.StateBuilder.cs` 11/12, `McpMod.Actions.cs` 9/12), so they cannot be merged
  independently and left unreviewed.
- Decision: absorb all twelve onto `codex/absorb-forks`, in ascending order of blast
  radius, one commit (or one merge commit for the large branches) per fork, with the
  zero-warning build as the gate after each step. Order: `timothyhioe` (required for the
  build at all), `WhoNeedsRAMAnyways`, `EternityQAQ2`, `cicero225`, `enderzcx`,
  `evangambit`, `jehubba`, `Lucien2714`, `Ryson-32` (code commit only; its `.agents/`
  toolchain is not imported), then `cat12079801`, `Zephreo`, `AI-Ascension`.
- Consequences: the tree gains the reward contract, the combat recorder, the generation
  pool, crystal sphere, run modifiers, the debug action set, the typed mod config, the
  dotenv port source, the ordered-pile state and the timeline/epoch work. Reviewing this
  branch is now a full-project review, not a feature review.
- Debt: `Dictionary<string, object?>` grew from 482 to 903. The JSON contract is still a
  bag, so field drift remains invisible to the compiler.
