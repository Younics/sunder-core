# Stacks

> **Applies to:** Sunder SDK `1.1.x`, `Sunder.Sdk.Stacks` `1.1.x`, Stack format V1, .NET 10, and Runtime protocol revision 3.

Stacks export selected setup from active packages into a portable archive and import it through a reviewable, contributor-owned plan. `Sunder.Sdk.Stacks` contains package-author contracts; Sunder Runtime owns archive validation, plan identity, and orchestration.

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md#stack-archive-validation) is the normative source for Stack archive paths, JSON/media/payload limits, feature handling, extraction safety, and secret scanning. This guide covers contributor behavior and intentionally does not restate those format rules.

## Add Stack Support

Generate a compiled stub:

```powershell
dotnet new sunder-package `
  --name MyPackage `
  --packageId my.company.package `
  --packageName "My Package" `
  --withStacks
```

Or reference `Sunder.Sdk.Stacks` with the same bounded minor range as `Sunder.Sdk`.

## Contributor Contracts

| Contract | Responsibility |
| --- | --- |
| `IPackageStackExporter` | Discover user-selectable items and export selected fragments/files/requirements. |
| `IPackageStackImporter` | Produce a side-effect-free preview, then apply selected actions. |
| `IPackageStackImportAppliedHandler` | Refresh derived state after committed imports for one contributor id. |

Register one manifest-declared RPC provider from the Runtime module. The SDK adapter keeps the convenient interfaces package-local:

```csharp
var contributor = services.GetRequiredService<MyStackContributor>();
registry.RegisterStackContributor("my.company.package.stack", contributor, services);
```

A single class may implement multiple local contracts. The adapter publishes capability metadata over `sunder.stack.contributor` v1.0.0, and Runtime discovers only exact descriptor-compatible RPC endpoints. Contributor ids are stable and unique only within the owner package. Duplicate exporter/importer ids for one package are rejected case-insensitively.

The generated template's [compiled Stack stub](../../src/Sdk/Sunder.Package.Templates/templates/sunder-package/Sunder.Package.Template/StackSupport.cs) is exercised by template tests and CI.

## Export Lifecycle

1. Runtime discovers active Stack RPC endpoints and obtains Host-stamped package identities and contributor metadata.
2. `ListExportItemsAsync` returns an immutable discovery snapshot without mutating package state.
3. App shows items/details, honoring `DefaultSelected` while allowing the user to change selections, editable values, and sensitivity.
4. Runtime calls `ExportAsync` with selected item/detail ids.
5. The package-local adapter converts fresh-stream payload handles into bounded, audience- and generation-fenced content references.
6. Runtime consumes those references, validates and builds the archive, then validates the exact produced archive before exposing it.

Item ids must be nonblank and case-insensitively unique within one contributor. Detail ids enable independent selection; details without ids are informational. Use `StackExportSelectionExtensions` to apply default selection/value/sensitivity behavior consistently.

Each `StackFragmentExport` needs a stable archive-wide `FragmentId`, stable `SchemaId`, positive contributor-defined `SchemaVersion`, JSON object payload, and optional files/required inputs. A payload handle exposes a relative path, optional declared length, and a factory that returns a **fresh readable stream** for each call. Do not expose local paths or return a reused/disposed stream.

`StackValueSensitivity.Secret` means the value should be excluded, replaced, or converted into an import input. It does not encrypt a value placed in the archive.

## Package Requirements

Every `StackPackageRequirement` identifies:

- package id;
- `InstallTag` used only when installation is needed, defaulting to `latest`;
- optional version used to create the export;
- optional minimum accepted import version; and
- whether absence blocks import.

A compatible installed version satisfies the requirement without resolving the dist tag. An optional absent requirement may be skipped, but any installed or selected version must satisfy the minimum. Requirements for the same package are merged: conflicting install tags or invalid minimum versions fail export, the strongest minimum wins, and any required declaration makes the merged requirement required.

## Import Lifecycle

1. Runtime validates and extracts the uploaded Stack into bounded temporary storage.
2. The user selects fragments; Runtime groups them by owner package and contributor.
3. `PreviewImportAsync` receives immutable fragments, input values, id remaps, and read-only payload handles. Preview **must be side-effect free**.
4. The importer returns selectable actions, still-required inputs, conflicts, and warnings.
5. Runtime creates an expiring, single-use plan bound to the archive hash, Runtime generation, exact provider endpoints, selected fragments, values/remaps, schema versions, available actions, and conflicts.
6. Import consumes that plan, verifies each exact endpoint is still active, and calls it with fresh fenced content references and only selected local action ids.
7. Runtime aggregates contributor outcomes and invokes `import-applied` on the same endpoint when items committed.

Plans expire after 15 minutes and are consumed even when apply fails. They become stale if the Runtime generation, archive, exact provider activation, or selected fragment set changes. Apply may choose any subset of the plan's known actions; unknown action ids are rejected. Preview actions and required-input ids must be nonblank and case-insensitively unique within that contributor.

Runtime scopes action ids, required-input ids, and remap keys by owner package and contributor before exposing them to App. Importers receive their original local ids. Identical local ids from different contributors remain distinct; package code must not parse or persist host-scoped ids.

An error-severity preview conflict blocks plan creation. Warnings require review but do not inherently block it.

## Outcomes And Atomicity

Atomicity is contributor-local. Runtime cannot roll back another package's committed changes and continues collecting results after an individual contributor fails.

Return:

- `Completed` when every selected action committed;
- `Partial` when at least one selected action committed and at least one did not; or
- `Failed` only when no selected action committed.

Report every committed item and final local id remap. If a contributor reports `Failed` with committed items, Runtime converts it to `Partial`. A non-completed result without an error receives a generic failure diagnostic.

Post-import handlers run after that contributor reports committed items. Their failures, including cancellation that was not requested by the host-linked operation token, become warnings and do not roll back import. Host cancellation still propagates. Use applied handlers only for derived caches or synchronization, not for the primary commit.

## Lifecycle And Safety

Exporter, importer, and post-import calls hold Runtime session, caller, and provider activation leases and may run concurrently. Content references are additionally fenced by owner, audience, generation, expiry, hash, and use count. Observe cancellation before additional mutations and close all streams. Keep preview deterministic for the same immutable input; never reserve ids, write files, create records, or consume one-time credentials during preview.

Do not export `IPackageSecrets`, access tokens, private keys, cookies, or environment secrets. Required inputs are the replacement mechanism. Runtime's Stack secret scan is defense in depth, not proof that an archive is safe. Review [Package Trust And Security](SECURITY.md) before handling third-party Stacks.
