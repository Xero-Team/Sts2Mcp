# 0007: Pin the SDK band, centralize package versions

- Context: `global.json` and `Directory.Packages.props` were both absent, so the build
  floated to whatever SDK was on `PATH` and the analyzer versions lived inline in
  `Directory.Build.targets`. The reference standard asks for `global.json` to pin the
  SDK family the project targets (`net9.0`) with `rollForward: latestFeature`, but
  the machines this repository is built on carry only 10.x SDKs and .NET 9 is out
  of support; pinning
  `9.0.100` would fail before the compiler runs, and `latestMajor` would allow exactly
  the cross-major drift the rule exists to stop.
- Decision: `global.json` pins the 10.0 SDK band (`10.0.100`, `latestFeature`,
  `allowPrerelease: false`); the assembly keeps `TargetFramework=net9.0` because that
  is the runtime the game hosts. All NuGet versions move to
  `Directory.Packages.props` with `ManagePackageVersionsCentrally=true`; the
  `PackageReference` items in `Directory.Build.targets` keep only their identity and
  `PrivateAssets`. `README.md` now names the .NET 10 SDK and the unchanged `net9.0`
  target.
- Consequences: every checkout resolves the same SDK band and analyzer versions, and
  adding a package is one `PackageVersion` line instead of editing shared targets.
  A contributor with only a .NET 9 SDK can no longer build; installing the 10 SDK is
  the documented requirement. Moving the TFM to `net10.0` is a separate decision that
  waits for the game to host that runtime.
- Debt: none added. `Directory.Build.props`/`targets` and `.editorconfig` stay in the
  configuration layer; no machine path or version is duplicated in a project file.
