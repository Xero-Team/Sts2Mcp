# Wire-format contract checks

Compiles the shipped contract types (`Core/Contracts/`) with the shipped serializer options and
asserts the exact JSON the transport emits. No game, no Godot, no game DLLs, no NuGet packages: the
only inputs are `System.Text.Json` and the source files themselves.

```sh
dotnet run --project tests/ContractShape/ContractShape.csproj -c Release
```

On a machine whose shared runtime is newer than `net9.0`, add `DOTNET_ROLL_FORWARD=Major`.

What it pins, and why each one is load-bearing:

- **The failure body** is `{"status":"error","error":"..."}`, with `error_code` present only when
  the call site has one. An absent code is omitted, never written as null, so `SendError` has to
  normalise a blank code to null.
- **Action-level and transport-level failures are byte-identical.** `Error()` still builds a
  dictionary; the contract type must produce the same bytes, or a client would need to know which
  path produced a failure.
- **The 503 body keeps its own key order** (`error`, `status`, `retry`) and always carries
  `retry: true`: nothing ran, so resending is safe.
- **`?pretty=1` changes whitespace only.** Same keys, same order.
- **C# members stay PascalCase** and the naming policy produces snake_case; `<`, `>` and `&` stay
  literal because the payload is read by an MCP client, not a browser.
- **A null property is dropped, a null dictionary value is kept.** The root endpoint relies on the
  second half: `commit` has to stay present (as null) for clients that read it blindly.

This harness cannot check the transport itself (HTTP status codes, `?pretty` parsing, the 405/409
routing); those need the game running. It pins the shape those paths share.
