# Config and port precedence

Compiles the shipped `McpMod.Config.cs` and `McpPortResolver.cs` with a recorded `Godot.GD` /
`Godot.OS` and runs the real precedence chain. No game, no Godot runtime, no NuGet.

```sh
dotnet run --project tests/Config -c Release
```

The harness sets `STS2_PORT`, writes a `.env` in a fresh temp directory, sets `Godot.OS.GetCmdlineArgs`
and calls the production `WithResolvedPort`. It is deliberately locale-hostile (`tr-TR`) so a
culture-sensitive `int.Parse` would show up.

## What it pins

- Order: `--sts2-mcp-port=`, then `STS2_PORT`, then `.env` in the working directory, then the
  `Sts2Mcp.conf` port, then `McpMod.DefaultPort`.
- Every source falls through on a malformed, blank, `0`, negative or `> 65535` value instead of
  stopping startup. A typo in one layer must not take the server down.
- The argument prefix is case-insensitive, the first matching argument wins, and an unrelated
  argument is ignored.
- `.env` parsing: comments stripped only outside quotes, `export` accepted, single and double
  quotes, case-sensitive key, and an unterminated quote kept raw so the port parse rejects it.
- The first `STS2_PORT` line in `.env` wins; the reader does not scan on for a later one.
- `McpPortResolver.Resolve` on its own, without the file and environment layer.

## What it cannot check

- The startup path that reads `Sts2Mcp.conf`: file discovery, JSON parse failure, missing
  `record`/`instant_mode` keys, and the `GD.PrintErr` message. Those need the real Godot filesystem
  and the manual check: start the game with a missing *and* a corrupt `Sts2Mcp.conf` and confirm the
  mod still listens on the default port, then start again with `"port": 17001` and confirm
  `GET /` answers `{"message": "Hello from STS2 MCP …", "status": "ok"}` there.
- What the HTTP server does after the port is chosen.
