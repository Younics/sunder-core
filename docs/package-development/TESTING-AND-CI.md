# Testing And CI

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 1.

Package tests should cover package-owned behavior at the narrowest boundary, then prove that Release output can be activated and packaged. Do not reference App or Runtime implementation projects just to make a unit test easier.

## Test Layers

| Layer | What to verify |
| --- | --- |
| Pure unit | DTO validation, schema construction, parsing, domain logic, and cancellation. |
| Handler/service | RPC provider, server-stream, callback/auth, Stack, and Worker lifecycle behavior using small test implementations of SDK interfaces. |
| App view model | State transitions and commands without constructing the Sunder shell. Keep most UI behavior outside the control. |
| Compiled package | Worker V1, generated Protocol bindings, App module registration, Avalonia XAML, and SDK member names still compile. |
| Artifact | `PackSunderPackage` emits one `.sunderpkg` and the exact archive passes `sunder dev package validate`. |
| Host smoke | The package activates as a development package; primary App-to-Runtime behavior and unload/reload work. |

The repository's [quickstart project](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/) is the compiled-package layer for these guides. `Sunder.Core.slnx` builds its current-RID Worker leaf and composed App/Runtime package directly from the canonical project in normal CI. Generated-template CI separately builds and publishes universal headless, Avalonia, Stack, and package-dependency variants so all six Worker leaves and the `Sunder.Package.Build` path are exercised with packed NuGet dependencies.

## Unit-Test Package Code

Test handlers directly. Keep host-neutral logic behind package-owned services and pass only the SDK abstractions it uses:

```csharp
[Fact]
public async Task Greet_IncrementsPersistedCount()
{
    var state = new InMemoryKeyValueStore();
    var context = new TestPackageContext(state);
    using var handler = new GreetingProvider(context);

    var result = await handler.CreateAsync(
        TestRpcInvocationContext.Create(),
        new GreetRequest { Name = "Ada" });

    Assert.Equal("Hello, Ada!", result.Message);
    Assert.Equal("1", await state.GetValueAsync("greeting.count"));
}
```

Implement only the public interfaces needed by the subject. Match the documented semantics: ordinal key comparison, immutable snapshots, bounded values, missing values as `null`, cancellation, and thread safety. A permissive mock that accepts invalid keys or ignores cancellation can hide package bugs.

Test concurrent calls for handlers documented as concurrent. Test cancellation before and during I/O. For streams, verify ordered events, normal completion, cancellation, and disposal. For callback/auth handlers, test invalid state, duplicate/stale values, cancellation, and secret-store failure. For Stack importers, assert that preview performs no mutation and that partial outcomes enumerate every committed item.

## Test Activation Code

Worker configuration and App module configuration should be deterministic and side-effect free. Inspect the immutable Worker options and use a small App registry spy to verify contribution ids. Useful failures to cover include:

- duplicate provider, callback, view, settings, exporter, or importer ids;
- malformed settings schemas and package-owned metadata;
- missing DI registrations and reserved host-service replacement;
- App/Worker assumptions made during pre-publication composition; and
- cleanup of timers, subscriptions, streams, and other owned resources.

Do not assert host-private concrete types, local-state paths, HTTP endpoints, generated manifest JSON layout, or exception implementation classes. Those are not package-author contracts.

## Validate A Release Artifact

Use a strict package version and build from a clean Release output:

```powershell
dotnet restore .\MyPackage.slnx --locked-mode
dotnet test .\MyPackage.slnx -c Release --no-restore
dotnet msbuild .\src\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
sunder dev package validate .\src\MyPackage\bin\Release\net10.0\MyPackage.1.2.3.sunderpkg
```

`Sunder.Package.Build` validates the archive it creates, but the explicit CLI check proves the exact file selected for upload. Fail CI unless exactly one expected archive exists. Do not validate one path and publish another.

The canonical project builds selected Worker/App RIDs sequentially during `PackSunderPackage`.
Native release jobs can instead retain the qualified bundle exported by ordinary Build
and fan it in with `SunderPackageUsePrebuiltLeaves=true`, `BuildProjectReferences=false`
and `SunderPackagePrebuiltRoleInputsFile` selecting exact RID/path/SHA-256 evidence.
Freeze before native Build and test/sign the retained bytes. Fan-in checks current
reviewed source and immutable tooling without Build, Compile, Restore or generator
execution. See [qualified role inputs](PREBUILT-ROLE-INPUTS.md) for commands, selection
grammar and failure-preservation guarantees. Omitted RID selection requires every
declared RID; explicit selections must be complete and exact.

## Host Smoke Checklist

Launch the exact `sunder-dev` directory produced by the test commit and exercise:

- clean activation of every declared role;
- the primary view and its first navigation;
- one owner RPC request and any primary RPC stream;
- settings/state persistence across reload;
- callback/auth cancellation where applicable;
- repeated rebuild with `--watch`;
- package disable/unload with no leaked work; and
- dependency or extension activation as one complete development-package set.

Use a disposable user profile or development Runtime state. Never point destructive smoke automation at a developer's normal Sunder state. Package code must not infer or delete Host-owned state directories.

## GitHub Actions Baseline

The following assumes the Sunder CLI is already available on the runner through your release tooling:

```yaml
name: package-ci

on:
  pull_request:
  push:
    branches: [main]

jobs:
  test-package:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.100
      - run: dotnet restore MyPackage.slnx --locked-mode
      - run: dotnet test MyPackage.slnx -c Release --no-restore
      - run: dotnet msbuild src/MyPackage/MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
      - run: sunder dev package validate src/MyPackage/bin/Release/net10.0/MyPackage.1.2.3.sunderpkg
```

Use maintained major-version Action refs so CI receives compatible upstream fixes. Commit `packages.lock.json`, set `RestorePackagesWithLockFile`, use `--locked-mode` in CI, and keep credentials out of pull-request jobs and command lines. Publication should be a separate protected job that consumes the already validated artifact. Supply a scoped Registry token only through `SUNDER_REGISTRY_PUBLISH_TOKEN` plus `--credential-source environment`, or redirected standard input plus `--credential-source stdin`; human browser auth remains Runtime-owned.

## Sunder Core Maintainers

Use the [project/lane iteration policy](../testing/core-test-oracles.md#local-iteration).
Run changed tests first; add public API/contract checks and affected consumers for a
public surface change. The following are full integration checks for a completed
public-package/template change, not a mandatory sequence after every edit:

```powershell
dotnet test languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/Sunder.Sdk.PublicApi.Tests.csproj -c Release
dotnet test languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj -c Release
dotnet test languages/csharp/dotnet/tests/Sunder.Package.Templates.Tests/Sunder.Package.Templates.Tests.csproj -c Release
```

Public API baselines are an intentional compatibility gate, not snapshots to regenerate automatically after a failure. Review every API difference against [Sunder SDK Compatibility](../SUNDER-SDK-COMPATIBILITY.md).
