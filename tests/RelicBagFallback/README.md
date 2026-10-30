# Multiplayer relic fallback export

Build and run against the repository-matching game assemblies using
`dotnet run --project Sts2Mcp/tests/RelicBagFallback -c Release -p:STS2GameDataDir=...`.
The harness invokes the compiled mod against an actual game `RelicGrabBag` and
`Player`. It checks empty, missing, and unreadable fallback queue exports.
