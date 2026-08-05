# Testing And CI

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 5.

Package tests should cover package-owned behavior at the narrowest boundary, then prove that Release output can be activated and packaged. Do not reference App or Runtime implementation projects just to make a unit test easier.

## Test Layers

| Layer | What to verify |
| --- | --- |
| Pure unit | DTO validation, schema construction, parsing, domain logic, and cancellation. |
| Handler/service | Runtime operation, stream, callback/auth, Stack, and background-service behavior using small test implementations of SDK interfaces. |
| App view model | State transitions and commands without constructing the Sunder shell. Keep most UI behavior outside the control. |
| Compiled package | Both module roles, contribution registration, Avalonia XAML, and SDK member names still compile. |
| Artifact | `PackSunderPackage` emits one `.sunderpkg` and the exact archive passes `sunder dev package validate`. |
| Host smoke | The package activates as a development package; primary App-to-Runtime behavior and unload/reload work. |

The repository's [quickstart project](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/) is the compiled-package layer for these guides. `Sunder.Core.slnx` builds it in normal CI. Generated-template CI separately builds and publishes headless, Avalonia, Stack, and combined variants so the template and `Sunder.Package.Build` path are exercised with packed NuGet dependencies.

## Unit-Test Package Code

Test handlers directly. Keep host-neutral logic behind package-owned services and pass only the SDK abstractions it uses:

```csharp
[Fact]
public async Task Greet_IncrementsPersistedCount()
{
    var state = new InMemoryKeyValueStore();
    var context = new TestPackageContext(state);
    using var handler = new GreetHandler(context);

    var result = await handler.HandleAsync(new GreetRequest("Ada"));

    Assert.Equal("Hello, Ada!", result.Message);
    Assert.Equal("1", await state.GetValueAsync("greeting.count"));
}
```

Implement only the public interfaces needed by the subject. Match the documented semantics: ordinal key comparison, immutable snapshots, bounded values, missing values as `null`, cancellation, and thread safety. A permissive mock that accepts invalid keys or ignores cancellation can hide package bugs.

Test concurrent calls for handlers documented as concurrent. Test cancellation before and during I/O. For streams, verify ordered events, normal completion, cancellation, and disposal. For callback/auth handlers, test invalid state, duplicate/stale values, cancellation, and secret-store failure. For Stack importers, assert that preview performs no mutation and that partial outcomes enumerate every committed item.

## Test Activation Code

Module configuration should be deterministic and side-effect free. A small registry spy can verify contribution ids and instances after building a service collection. Useful failures to cover include:

- duplicate operation, stream, callback, view, settings, exporter, or importer ids;
- malformed settings schemas and package-owned metadata;
- missing DI registrations and reserved host-service replacement;
- App/Runtime assumptions made during pre-publication composition; and
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

## Host Smoke Checklist

Launch the exact `sunder-dev` directory produced by the test commit and exercise:

- clean activation of every declared role;
- the primary view and its first navigation;
- one App-to-Runtime request and any primary stream;
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

Changes to public package contracts must also run:

```powershell
dotnet test languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/Sunder.Sdk.PublicApi.Tests.csproj -c Release
dotnet test languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj -c Release
dotnet test languages/csharp/dotnet/tests/Sunder.Package.Templates.Tests/Sunder.Package.Templates.Tests.csproj -c Release
```

Public API baselines are an intentional compatibility gate, not snapshots to regenerate automatically after a failure. Review every API difference against [Sunder SDK Compatibility](../SUNDER-SDK-COMPATIBILITY.md).
