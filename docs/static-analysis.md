# Static analysis baseline

What runs on every `dotnet build` in this repo, why each piece is there, and what to do when a
rule fires. Everything here is build-time only: no analyzer assembly is copied to `out/` and
nothing here changes the mod's runtime behaviour.

## Where the settings live

| File | Holds |
| --- | --- |
| `Directory.Build.targets` | Analyzer packages, `AnalysisMode`/`AnalysisLevel`, the strict-mode switch, the banned-API list registration |
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

Only rules that cannot apply are silenced (see `.editorconfig`); a rule with many hits is a
backlog item, not a wrong rule.

- Globalization (`CA1304/1305/1307/1308/1310/1311/...`): the mod emits JSON for an MCP client and
  log lines. Nothing formats values for a locale, and `CultureInfo` overloads would be dead API.
- `S3011` ("reflection should not be used to increase accessibility"): reading private game
  members through reflection is the mod's mechanism (`McpMod.Il.cs`).

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

The build fails while the backlog below is non-empty. To keep working on something unrelated,
relax the gate for one invocation instead of editing files:

```sh
dotnet build STS2_MCP.csproj -p:STS2StrictWarnings=false   # findings are warnings again
```

## Outstanding work

The gate is on, so this list is a list of build breakers. Numbers are from the baseline commit and
will move as the code does; re-measure with `-p:STS2StrictWarnings=false` and count warnings rather
than trusting them.

| Bucket | Count | Note |
| --- | --- | --- |
| `MA0002` use an explicit `StringComparer`/`StringComparison` | ~400 | Mostly dictionary and `string.Equals` calls over identifier-like strings; `StringComparer.Ordinal` is the right default here |
| `CA1031` catch a more specific exception | ~270 | Deliberate in the state builders (a renamed game member must not kill a state read) - needs per-site judgement or a documented suppression helper, not a blanket `none` |
| `ERP022` swallow exception without observing it | ~160 | Same defensive style; the recorder/state builders should log through `McpMod.Warnings` instead of dropping |
| `MA0006` use `string.Equals` instead of `==` | ~150 | Mechanical |
| `MA0051` method too long | ~70 | Concentrated in `McpMod.StateBuilder.cs`; extract only where it buys clarity |
| `S108` empty `catch` block | ~43 | Same family as `ERP022` |
| `MA0011` pass `IFormatProvider` | ~40 | JSON payload building; `CultureInfo.InvariantCulture` |
| formatting drift | 419 whitespace + 6 imports + 2 final newline | One `dotnet format STS2_MCP.csproj` pass fixes all of it; do it as its own commit |

Then delete this line from `Directory.Build.targets` once the list is empty, so the gate can no
longer be relaxed by a property:

```xml
<TreatWarningsAsErrors Condition="'$(STS2StrictWarnings)' != 'false'">true</TreatWarningsAsErrors>
<CodeAnalysisTreatWarningsAsErrors Condition="'$(STS2StrictWarnings)' != 'false'">true</CodeAnalysisTreatWarningsAsErrors>
```

replaced by the unconditional pair. Until then the opt-out exists on purpose: a contributor
blocked by an unrelated backlog item should not have to choose between committing a `#pragma` and
not committing at all.

## Adding a ban

Add one line to `BannedSymbols.txt`: `documentation-id;message shown to the author`. Keep the
file free of blank lines and comment lines - the analyzer treats an empty entry as a duplicate
and reports `RS0031` for it. Rationale for the current entries (no raw pointers, no
`GCHandle`, no forced `GC.Collect`, no hand-rolled unmanaged memory) belongs in the commit that
adds them, not in the file.
