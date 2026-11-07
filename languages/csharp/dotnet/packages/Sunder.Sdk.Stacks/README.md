<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# Sunder.Sdk.Stacks

**Let users save, share, and restore your package's setup.**

A Sunder **Stack** is a portable collection of selected setup from one or more
packages. Use this SDK to make your package's presets, workspace configuration,
or reusable workflows participate in Stack export and import.

Your contributor decides which data to export and how to apply it. Sunder presents
the selection and import preview, validates the archive, and coordinates the operation.

## Install and prerequisites

Use a .NET 10 Sunder package, the Sunder CLI, and a Core platform catalog containing
`sunder.stack.contributor` (or a Registry that publishes that catalog).

```sh
dotnet add package Sunder.Sdk.Stacks
```

For generated single-project packages, use the same reference shape as the template:

```xml
<PackageReference Include="Sunder.Sdk.Stacks" Version="1.0.2" ExcludeAssets="buildTransitive" SunderPackageAssetRole="worker" />
```

The explicit platform selection supplies the contract used by generated registration.

## Tutorial: share a greeting preference

This contributor exports one non-secret greeting prefix, shows an import action,
and writes the prefix only after the user selects that action.

### 1. Create the package and select the platform contract

```sh
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name HelloStacks --packageId example.preferences --packageName "Hello Stacks" --withStacks --output HelloStacks
cd HelloStacks
dotnet restore
dotnet msbuild -t:ImplementSunderPlatformApi -p:SunderContractId=sunder.stack.contributor -p:SunderPlatformCatalogPath=/path/to/sunder-platform-contracts.v1.json
```

Replace the catalog path with the matching Core catalog supplied for your Sunder
environment. Alternatively, use `-p:SunderRegistryOrigin=https://your-registry.example/`
instead of the catalog property. See [platform selection](../../../../../docs/package-development/PLATFORM-CONTRACT-IMPORTS.md)
for catalog sources and restore instructions. Commit the resulting contract lock.

### 2. Implement the contributor

Replace `Stacks/StackSupport.cs` with:

```csharp
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;
using Sunder.Sdk.Stacks;

namespace HelloStacks;

[SunderServiceProvider(typeof(IPackageStackContributor), ProviderId = "example.preferences.stack")]
public sealed partial class PackageStackContributor(IPackageContext context)
    : IPackageStackExporter, IPackageStackImporter
{
    private const string FragmentId = "example.preferences.greeting";
    private const string SchemaId = "example.preferences.greeting";
    private const string ActionId = "restore-prefix";

    public string ContributorId => "example.preferences.stack";
    public string DisplayName => "Greeting preferences";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext discovery, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>(
            [new("greeting", "Greeting prefix", "preferences")]);

    public async ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.IsItemSelected("greeting")) return new([], [], []);
        var prefix = await context.Storage.State.GetValueAsync("greeting.prefix", cancellationToken)
            ?? "Hello";
        return new(
            [new StackFragmentExport(FragmentId, SchemaId, 1, "Greeting prefix",
                JsonSerializer.Serialize(new { prefix }), SourceItemId: "greeting")],
            [new StackPackageRequirement(context.PackageId, CreatedWithVersion: context.Version)],
            []);
    }

    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _ = ReadPrefix(request.Fragments);
            return ValueTask.FromResult(new StackImportPreview(
                [new(ActionId, "Restore greeting prefix", StackImportActionKind.Update)], [], [], []));
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(new StackImportPreview([], [],
                [new("unsupported-greeting", "Expected one valid version-1 greeting fragment.",
                    StackImportConflictSeverity.Error, request.Fragments.FirstOrDefault()?.FragmentId)], []));
        }
    }

    public async ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SelectedActionIds.Count == 0)
            return new(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []);
        if (request.SelectedActionIds.Count != 1 || request.SelectedActionIds[0] != ActionId)
            return new(StackImportOutcome.Failed, [], new Dictionary<string, string>(), [], ["Unknown import action."]);

        string prefix;
        try { prefix = ReadPrefix(request.Fragments); }
        catch (InvalidOperationException)
        {
            return new(StackImportOutcome.Failed, [], new Dictionary<string, string>(), [], ["Invalid greeting fragment."]);
        }

        await context.Storage.State.SetValueAsync("greeting.prefix", prefix, cancellationToken);
        return new(StackImportOutcome.Completed,
            [new("greeting", "Greeting prefix", "preferences")], new Dictionary<string, string>(), [], []);
    }

    private static string ReadPrefix(IReadOnlyList<StackFragmentImport> fragments)
    {
        if (fragments.Count != 1 || fragments[0].FragmentId != FragmentId
            || fragments[0].SchemaId != SchemaId || fragments[0].SchemaVersion != 1)
            throw new InvalidOperationException();
        try
        {
            using var document = JsonDocument.Parse(fragments[0].JsonPayload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("prefix", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= 80 } prefix)
                return prefix;
        }
        catch (JsonException) { }
        throw new InvalidOperationException();
    }
}
```

The preview validates data and describes an action without changing state. Import
validates again, honors the selected action, and reports the item it committed.

### 3. Try the round trip

```sh
dotnet build
sunder runtime ensure sunder.runtime.dotnet ">=10.0.0 <11.0.0" sunder.dotnet-worker-module.v1
dotnet run --no-build
```

In Sunder's Stack export flow, select **Greeting prefix** from **Hello Stacks**.
Import the resulting Stack: the preview offers **Restore greeting prefix**. Apply
it to store the value in this package's `greeting.prefix` state key. Your services
can read that key using the same package context.

## Contributor responsibilities

| Interface | Responsibility |
| --- | --- |
| `IPackageStackExporter` | List selectable items and export the requested data. |
| `IPackageStackImporter` | Preview changes, then apply the selected actions. |
| `IPackageStackImportAppliedHandler` | Refresh derived caches after committed changes. |

Keep schema IDs stable and version your fragment format when its shape changes.
Support older fragment versions explicitly or return an error conflict. The
fragment's schema version is independent of your package version.

For several actions, report `Partial` if some changes committed and others failed;
`Failed` means nothing committed. Import preview is read-only. Include only portable
non-secret configuration in exported JSON; request credentials at import using
`StackRequiredInputDescriptor` with `StackValueSensitivity.Secret`.

## Troubleshooting and more resources

- **`platform-api.import-not-found`:** select the platform catalog before building.
- **Contributor is absent:** confirm its worker activated, its provider ID is unique,
  and the selected contract matches your Sunder environment.
- **Import cannot proceed:** inspect the fragment conflicts returned by preview.
- [Stack lifecycle, payload files, and import rules](../../../../../docs/package-development/STACKS.md)
- [Platform contract selection](../../../../../docs/package-development/PLATFORM-CONTRACT-IMPORTS.md)
- [Package storage](../../../../../docs/package-development/DATA-AND-LOGGING.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [GPL-3.0](../Sunder.Sdk/LICENSE).
