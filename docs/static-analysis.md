# Static analysis baseline

What runs on every `dotnet build` in this repo, why each piece is there, and what to do when a
rule fires. Everything here is build-time only: no analyzer assembly is copied to `out/` and
nothing here changes the mod's runtime behaviour.

## Where the settings live

| File | Holds |
| --- | --- |
| `Directory.Build.targets` | Analyzer packages, `AnalysisMode`/`AnalysisLevel`, the strict gate, the banned-API list registration |
| `.editorconfig` | Formatting conventions the tree already follows, plus the handful of rules that cannot apply to this project |
| `BannedSymbols.txt` | API allow/deny list enforced by `Microsoft.CodeAnalysis.BannedApiAnalyzers` (rule RS0030) |

`Directory.Build.props` is **not** used and must stay that way: it is gitignored because it holds
each developer's local `STS2GameDir`. `Directory.Build.targets` is the other MSBuild convention
file, is imported for every project in the repo, and is where shared settings can be committed.

## What is enabled

| Component | Rule prefix | Notes |
| --- | --- | --- |
| `Microsoft.CodeAnalysis.NetAnalyzers` (SDK) | CA | `AnalysisLevel=latest` + `AnalysisMode=All` turns the whole set on at warning severity |
| `Meziantou.Analyzer` | MA | Correctness, culture, performance, threading details |
| `SonarAnalyzer.CSharp` | S | Bug patterns and security hotspots |
| `ErrorProne.NET.CoreAnalyzers` | ERP/EPC | Exception-handling mistakes |
| `Microsoft.VisualStudio.Threading.Analyzers` | VSTHRD | async/await and blocking-call misuse - relevant because the mod runs a server thread next to the game's main thread |
| `AsyncFixer` | AsyncFixer | Fire-and-forget and `async void` mistakes |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | RS0030 | Enforces `BannedSymbols.txt` |
| `dotnet format` | WHITESPACE/IMPORTS/IDE | Not part of the build; see below |

`EnforceCodeStyleInBuild=true` makes IDE style rules visible in `dotnet build` instead of only in
an editor, so a CI build and a local build agree.

### Deliberately not included

- **StyleCop.Analyzers** - `SA1201` throws `AD0001` on this codebase, and the package has been
  unmaintained since 2019. A crashing analyzer silently disables itself, which is worse than
  having no rule.
- **ClrHeapAllocationAnalyzer** - works, but reports ~2.2k allocation warnings, is unmaintained,
  and overlaps with the JIT allocation work already visible through `dotnet-counters`. Worth
  reconsidering only if allocation churn becomes a real problem.
- **Roslynator.Analyzers** - installs cleanly but its rules are opt-in, so out of the box it adds
  four diagnostics for the cost of another package. Add it *together with* an explicit rule set if
  it is ever wanted.
- **PublicApiAnalyzers / NetArchTest** - no stable public surface or layer boundary to protect
  yet; the mod is a single assembly that talks to the game through reflection.

### Rules switched off, and why

Only rules that cannot apply are silenced (see `.editorconfig` and the per-site
`SuppressMessage` attributes on the handful of methods that need one); a rule with many hits is a
backlog item, not a wrong rule.

- Globalization (`CA1304/1305/1307/1308/1310/1311/...`): the mod emits JSON for an MCP client and
  log lines. Nothing formats values for a locale, and `CultureInfo` overloads would be dead API.
- `CA1707` ("underscores in identifiers"): the assembly and namespace name `STS2_MCP` is the mod's
  shipped identity (`mod_manifest.json`, the game's mod folder, every documented path).
- `S3011` ("reflection should not be used to increase accessibility"): reading private game
  members through reflection is the mod's mechanism (`McpMod.Il.cs`).
- `S1144` on Harmony patch methods: Harmony reaches `TargetMethods`/`Prefix`/`Postfix` through the
  attributes on the patch class, not through a C# call, so they are live but look unused.
- `CA2007`/`MA0004`/`VSTHRD003` on the reward-observation awaits: they must resume on the thread
  that owns the reward session, so `ConfigureAwait(false)` would be wrong there.

## Building and running the gates on Linux

The project references `sts2.dll`, `GodotSharp.dll` and `0Harmony.dll` out of the game install,
so the game directory has to be supplied. On this machine:

```sh
export STS2GameDir="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"

dotnet build STS2_MCP.csproj          # analyzer + compiler warnings are errors here
dotnet format STS2_MCP.csproj --verify-no-changes   # formatting gate
```

`dotnet format` needs the same game path; because it has no `-p:` passthrough in this SDK, the
`STS2GameDir` environment variable is the portable way to give it one.

The gate is unconditional: there is no property that turns findings back into warnings. A
contributor who hits a finding fixes the code or the rule, because a `#pragma` or a relaxed build
committed to make one finding go away hides the next one too.

## Outstanding work

Nothing. The last measurement of the full design-rule set is zero findings, so `dotnet build` is
green on a fresh checkout; the counts below are history.

The backlog was cleared in this order. First (measured at `9122b64`): `MA0002` (442), `MA0006` (352),
`MA0011` (41), `S1144` (27, via the Harmony justification), `S1172` (12),
`S1481` (7), `S6444` (3), `S8969` (18), `VSTHRD002` (12), `S3358` (28), `S3267`
(20), the one-line findings `MA0004`/`MA0009`/`MA0074`/`S1125`/`S1862`/`S1905`/
`S4136`/`S6610`/`CA1805`/`CA1820`/`CA1847`/`CA1865`/`CA2244`, and all formatting
drift (419 whitespace, 6 import orders, 2 final newlines) - 1538 warnings at the
baseline commit down to 361.

The transport handlers no longer carry their own catch pair: `RunHandler` and
`RunActionHandler` own the 503/500 mapping, one call per handler. Reads whose
failure means "not there" rather than "broken" (a freed Godot node, a member this
game version does not have) go through `TryReadQuiet`, which states that once
instead of at thirteen sites.

The rest of the backlog closed the same way, one named rule per call site instead of a blanket
suppression: the defensive-catch family (`CA1031`/`ERP022`/`S108`/`S2486`/`EPC12`) went through
`TryDo`/`TryRead`/`TryReadQuiet`, and the long methods (`MA0051`, 74 at the start) were split into
private methods named after the step they perform - never into local functions, which the rule does
not count but a reader cannot reuse. The rules that stay silenced are the ones listed under "Rules
switched off, and why" above; `docs/adr/0005-full-refactor-phases.md` records why each is a
property of this mod rather than a finding.

## Adding a ban

Add one line to `BannedSymbols.txt`: `documentation-id;message shown to the author`. Keep the
file free of blank lines and comment lines - the analyzer treats an empty entry as a duplicate
and reports `RS0031` for it. Rationale for the current entries (no raw pointers, no
`GCHandle`, no forced `GC.Collect`, no hand-rolled unmanaged memory) belongs in the commit that
adds them, not in the file.
