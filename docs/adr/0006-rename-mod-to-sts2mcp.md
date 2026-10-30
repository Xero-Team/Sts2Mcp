# 0006: Rename the mod to `Sts2Mcp`

- Context: the assembly, root namespace, project, solution, manifest id and config
  filename used `STS2_MCP`. That has an embedded underscore, which C# naming rules
  reject, and the repository already spells the project `STS2MCP`; a `CA1707`
  suppression in `.editorconfig` was the only thing keeping the old name alive.
  Replacing it with `STS2MCP` removes the underscore but keeps two all-caps
  acronyms, which the .NET casing guidance (and Sonar `S101`, already enabled
  here for type names) reads as `Sts2` + `Mcp`. The game's own namespaces
  (`MegaCrit.Sts2.*`) use the same casing.
- Decision: name every code identifier `Sts2Mcp` — project (`Sts2Mcp.csproj`),
  solution (`Sts2Mcp.sln`), assembly and root namespace (`Sts2Mcp`), manifest id,
  config file (`Sts2Mcp.conf`), HTTP server thread name, docs, scripts, Makefile
  and `build.ps1`. No alias is kept for the old names. Names that come from the
  game or the wire contract — reflection-reached game members and `snake_case`
  JSON keys — keep their exact spelling, and the GitHub repository stays
  `STS2MCP` because it is the published project name, not a C# identifier. The
  in-game display name stays "STS2 MCP".
- Consequences: breaking for existing installs. The game loads `Sts2Mcp.dll` with
  `Sts2Mcp.json`, so the old `STS2_MCP.dll` / `STS2_MCP.json` must be removed from
  the mods directory; the manifest id changed, so leaving both would register two
  mods. An existing `STS2_MCP.conf` is no longer read: port and `instant_mode`
  move to `Sts2Mcp.conf` (or `STS2_PORT` / `.env`, unchanged). No HTTP endpoint,
  field name or `schema_version` changes.
- Debt: none added. The `CA1707` suppression is gone, so a new embedded
  underscore in an externally visible identifier fails the build instead of being
  documented as an exception.
