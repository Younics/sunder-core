# Stacks

> **Applies to:** Sunder SDK `1.1.x`, `Sunder.Sdk.Stacks` `1.1.x`, Stack format V1, .NET 10, and Runtime protocol revision 1.

Stacks export selected setup from active packages into a portable archive and import it through a reviewable, contributor-owned plan. `Sunder.Sdk.Stacks` contains package-author contracts; Sunder Runtime owns archive validation, plan identity, and orchestration.

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md#stack-archive-validation) is the normative source for Stack archive paths, JSON/media/payload limits, feature handling, extraction safety, and secret scanning. This guide covers contributor behavior and intentionally does not restate those format rules.

## Content Identity And System Versions

A Stack has a stable `stackId`, not a content SemVer. Republishing advances its current
Registry content. Registry publication revisions pin reviewed downloads and retain prior
bytes; they are not package-style versions that authors choose. A downloaded local Stack
is a snapshot, not a subscription to subsequent edits.

System compatibility has three separate boundaries:

- The archive's `schemaVersion`, content-index version, `minReaderVersion`, and required
  features describe the Stack format. Core owns these readers and writers.
- Each fragment's `schemaId` and `schemaVersion` describe package-owned content. The
  contributor decides which versions it understands and converts them to its current model.
- The exact contributor RPC contract (`sunder.stack.contributor` `2.0.0`) describes the
  calls between Runtime and packages. It is independent of both archive and fragment versions.

Core's current writer emits format 1, and its implemented reader set is `{1}`. Reader
admission is independent of the current writer: introducing format 2 must add its real
reader while retaining reader 1, rather than changing one equality constant. Raising the
minimum supported schema/index version retires new import/publication of an old format.
Known retained Registry metadata and immutable download bytes are not rewritten or deleted.
An unknown future format is rejected before interpreting its version-specific fields.

A contributor can handle several fragment versions in its existing importer; no new
global Stack format is needed when only that package's JSON changes. An unsupported or
invalid selected fragment must return an error-severity `StackImportConflict` identifying
the fragment, not an empty successful preview. Reject it again at apply rather than
reporting a successful no-op. Users may explicitly deselect unsupported content.

## Add Stack Support

Generate a contributor stub:

```powershell
dotnet new sunder-package `
  --name MyPackage `
  --packageId my.company.package `
  --packageName "My Package" `
  --withStacks
```

Or reference `Sunder.Sdk.Stacks` with the same bounded minor range as `Sunder.Sdk`,
using `ExcludeAssets="buildTransitive"` while its legacy automatic bundle asset
remains. Select `sunder.stack.contributor` with `ImplementSunderPlatformApi` and
`SunderPlatformCatalogPath` for Registry-independent local authoring, or an explicit
`SunderRegistryOrigin` for a published catalog. Select before first Accept when the
source already contains a contributor. See [platform imports](PLATFORM-CONTRACT-IMPORTS.md).

## Contributor Contracts

| Contract | Responsibility |
| --- | --- |
| `IPackageStackContributor` | Common contributor identity (`ContributorId` and `DisplayName`) required by typed registration. |
| `IPackageStackExporter` | Discover user-selectable items and export selected fragments/files/requirements. |
| `IPackageStackImporter` | Produce a side-effect-free preview, then apply selected actions. |
| `IPackageStackImportAppliedHandler` | Refresh derived state after committed imports for one contributor id. |

Mark the partial contributor class with
`[SunderServiceProvider(typeof(IPackageStackContributor), ProviderId = "my.company.package.stack")]`
from `Sunder.Sdk.Services`.
Register its generated provider from Worker V1:

```csharp
var contributor = new MyStackContributor();
providers.Add(contributor.CreateSunderRpcProviderRegistration());
```

A single class may implement multiple local contracts, which all derive from `IPackageStackContributor`. Its marker selects the local semantic adapter, not platform authority. Generation uses the authenticated platform lock to produce private wire types, dispatch and registration for `sunder.stack.contributor` `2.0.0`; authors do not copy descriptor versions or hashes. Runtime discovers only exact descriptor-compatible endpoints. Contributor ids are stable and unique only within the owner package. Duplicate exporter/importer ids for one package are rejected case-insensitively.

The generated template's [Stack stub](../../languages/csharp/dotnet/packages/Sunder.Package.Templates/templates/sunder-package/Sunder.Package.Template/Stacks/StackSupport.cs.in) has packed-template bootstrap checks and Registry-catalog generation/behavior coverage. Production Registry publication/bootstrap and Runtime acceptance remain separate gates; catalog signing setup is not required.

## Export Lifecycle

1. Runtime discovers active Stack RPC endpoints and obtains Host-stamped package identities and contributor metadata.
2. `ListExportItemsAsync` returns an immutable discovery snapshot without mutating package state.
3. App shows items/details, honoring `DefaultSelected` while allowing the user to change selections, editable values, and sensitivity.
4. Runtime calls `ExportAsync` with selected item/detail ids.
5. The package-local adapter converts fresh-stream payload handles into bounded, audience- and generation-fenced content references.
6. Runtime consumes those references, validates and builds the archive, then validates the exact produced archive before exposing it.

Item ids must be nonblank and case-insensitively unique within one contributor. Detail ids enable independent selection; details without ids are informational. A selection with `Details == null`, or without a matching detail choice, uses that discovery detail's `DefaultSelected` value. Pass the descriptor default explicitly to `StackExportSelectionExtensions`; the helpers do not guess it.

Each `StackFragmentExport` needs a stable archive-wide `FragmentId`, stable `SchemaId`, positive contributor-defined `SchemaVersion`, JSON object payload, and optional files/required inputs. Every `StackRequiredInputDescriptor` explicitly classifies its value as `Public` or `Secret`. Public inputs may declare a portable default; secret inputs may not. A payload handle exposes a relative path, optional declared length, and a factory that returns a **fresh readable stream** for each call. Do not expose local paths or return a reused/disposed stream.

`StackValueSensitivity.Secret` means the value should be excluded, replaced, or converted into an import input. It does not encrypt a value placed in the archive.

## Package Requirements

Every `StackPackageRequirement` identifies:

- package id;
- `InstallTag` used only when installation is needed, defaulting to `latest`;
- optional version used to create the export;
- optional minimum accepted import version; and
- whether absence blocks import.

A compatible installed version satisfies the requirement without resolving the dist tag. An optional absent requirement may be skipped, but any installed or selected version must satisfy the minimum. Requirements for the same package are merged: conflicting install tags or invalid minimum versions fail export, the strongest minimum wins, and any required declaration makes the merged requirement required.

Install tags are case-sensitive. Runtime adds an owner/selected-package fallback only
when no contributor supplied that package's requirement; it never invents a `1.0.0`
minimum or overrides an authored tag. App editing uses the same merge rules as export.
The current RPC requirement model expresses a minimum, not a bounded package range;
`CreatedWithVersion` is descriptive, not an exact pin or an upper compatibility bound.

## Import Lifecycle

1. Runtime validates and extracts the uploaded Stack into bounded temporary storage.
2. The user selects fragments; Runtime groups them by owner package and contributor.
3. `PreviewImportAsync` receives immutable fragments, input values, id remaps, and read-only payload handles. Preview **must be side-effect free**.
4. The importer returns selectable actions, still-required inputs, conflicts, and warnings.
5. Runtime creates an expiring, single-use plan bound to the archive hash, Runtime generation, exact provider endpoints, selected fragments, values/remaps, schema versions, available actions, and conflicts.
6. Import consumes that plan, verifies each exact endpoint is still active, and calls it with fresh fenced content references and only selected local action ids.
7. Runtime aggregates contributor outcomes and invokes `import-applied` on the same endpoint when items committed.

Runtime enforces package availability/minimum versions before preview and again at apply.
It does not silently install packages inside a contributor call. App and CLI use the
existing package-plan APIs first, with their normal fingerprints and capability consent.
Package installation and contributor import remain separate transactions: installed
packages remain if later setup is rejected or cancelled.

Expected preview rejections retain their structured conflicts, inputs, warnings, and
errors over HTTP. A false `Success` cannot be applied. App refreshes the setup review
after installing packages, and rebinds previously entered values to the contributor's
Host-scoped input ids before creating the applied plan. Changed actions require review
again. Runtime rejects missing required values for contributors whose actions are selected.

Plans expire after 15 minutes and are consumed even when apply fails. They become stale if the Runtime generation, archive, exact provider activation, or selected fragment set changes. Apply may choose any subset of the plan's known actions; unknown action ids are rejected. Preview actions and required-input ids must be nonblank and case-insensitively unique within that contributor.

Runtime scopes action ids, required-input ids, and remap keys by owner package and contributor before exposing them to App. Importers receive their original local ids. Identical local ids from different contributors remain distinct; package code must not parse or persist host-scoped ids. App masks only `Secret` inputs; public values remain visible for review. Never include secret input values in logs, warnings, errors, conflict messages, notifications, telemetry, or imported-item display text.

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

Do not export `IPackageSecrets`, access tokens, private keys, cookies, or environment secrets. Secret-classified required inputs are the replacement mechanism. Runtime's Stack secret scan is defense in depth, not proof that an archive is safe. Review [Package Trust And Security](SECURITY.md) before handling third-party Stacks.
