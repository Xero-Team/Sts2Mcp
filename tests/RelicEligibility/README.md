# Relic eligibility export

`dotnet run --project Sts2Mcp/tests/RelicEligibility` compiles the production
party predicate with small input types. It covers first-run Ironclad eligibility,
party order, mixed profile run counts, and absent unlock state. Build the full mod
against the matching game assemblies separately to check API compatibility.
