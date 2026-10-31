# 0002: One config surface, four port sources

- Context: `jehubba` added `McpPortResolver` (command-line override), `Lucien2714` moved
  the config file into a typed `Config.ModConfig`, and `AI-Ascension` added `.env`
  parsing. Three loaders for one value.
- Decision: `McpMod.Config.cs` is the single loader. Precedence, highest first:
  `--sts2-mcp-port=` on the command line, then dotenv (`STS2_PORT` in the environment, a
  `.env` next to the assembly, a `.env` in the game working directory), then `"port"` in
  `Sts2Mcp.conf`, then `DefaultPort` (15526). `WithResolvedPort` applies it and is the
  only place that decides.
- Consequences: a second game process can pick its own port without editing a file, and
  the file stays the durable setting. The precedence is documented in code, not only
  here.
- Debt: `Saves`/`Settings` reads inside `ApplyInstantMode` still run on the main-thread
  queue via `RunOnMainThread`, which is correct but means config load has two phases.
