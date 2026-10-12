# Imports In Unpublished Development Packages

Unpublished packages can be created, consume local package APIs and implement local
platform contracts without a Registry. For C# packages, select and restore exact local
inputs, build, then pass the generated `sunder-dev` directory to the App as usual:

```sh
Sunder.App --dev-package ./bin/Debug/net10.0/sunder-dev
```

`Sunder.Package.Build` emits `sunder-dev.imports.v1.json` beside that directory. It carries
the reviewed origin-free selection's complete inventories and original verified catalog
bytes, bound to the generated manifest hash. The Host validates that local handoff before
retaining development evidence on first load. Build and Runtime need no Registry or
running service during compilation. Rebuild replaces the package and handoff together;
the handoff is generated output and is never included in a `.sunderpkg` archive.

For other producers, older outputs, or explicit preparation, use the managed Runtime:

```powershell
sunder dev package prepare-imports ./dist/sunder-dev --candidate ./owner.sunderpkg --platform-catalog ./core-catalog.json
sunder dev package smoke ./dist/sunder-dev
sunder dev package run ./dist/sunder-dev --watch
```

Use your actual canonical output folder. Node normally emits `dist/sunder-dev`; a
.NET canonical project emits `bin/<configuration>/<target-framework>/sunder-dev`.
The same Runtime operation consumes either language's validated manifest.
Repeat `--candidate` for every selected package artifact. Omit `--platform-catalog`
when the package has no platform constraints. For published imports, use
`prepare-imports <folder> --registry <https-origin>` instead of local inputs.

## Preparation

`prepare-imports` requires a complete, valid exploded package. It authenticates to
the local Runtime and requires the negotiated `dev-package-imports.v1` feature.
Local preparation inspects the supplied canonical archives/catalog without network
access. Explicit Registry preparation retrieves the selected published inventories.
Both paths verify:

- Selected package version/content identity, complete inventory count/hash, and
  reviewed package ranges and prerelease policy.
- Exact contract ID/version, descriptor hash, ABI hash, and ultimate authority.
- Explicit reviewed selections for distinct ultimate package authorities.
- Exact original platform catalog bytes,
  including platform constraints retained only in inventory metadata.

Registry preparation additionally requires current activation policy for its catalog
subjects. Local evidence is explicitly recorded as local; it creates no Registry
publication or policy lease. Missing, extra or duplicate local inputs fail without
a network fallback.

Preparation retains a separate development receipt bound to the exact generated
manifest hash. It installs no dependencies, changes no source lock, and assigns no
published package identity to local code. Source selection/restore and Runtime import
preparation are separate operations: see [contract restore](CONTRACT-RESTORE-CACHE.md).

HTTPS is required when Registry preparation is explicitly selected. Local preparation
needs neither a Registry URL nor a Development-Registry transport flag. Both local and
published [platform imports](PLATFORM-CONTRACT-IMPORTS.md) retain exact catalog pins.

## Offline Iteration

For an automatic edit/build/reload loop, open two terminals in your package project's
directory:

```sh
# Compile source edits and publish a complete development package.
dotnet watch build

# Load the first successful build and keep watching its generated output.
Sunder.App --dev-package ./bin/Debug/net10.0/sunder-dev --watch
```

The CLI equivalent for the second command is
`sunder dev package run ./bin/Debug/net10.0/sunder-dev --watch`.
Start it after the first build has produced the directory. `--watch` on the App/CLI
enables output reload; `dotnet watch` compiles source changes. This performs a staged
package/Worker/App replacement, so an in-memory view or Worker session may be recreated.
With the pinned .NET 10 SDK, run `dotnet watch build` from the project directory:
`--project` is forwarded to `dotnet build`, which does not accept that switch.

The build tooling includes Avalonia XAML, linked UI resources, package assets, copied
content and the selected contract lock/baseline in the source-watch list, alongside
the .NET SDK's C# and embedded resources. `dotnet watch --project ./MyPackage.csproj --list`
shows the exact inputs. Ordinary `Watch="false"` resource metadata opts out a resource.
Generated role/output directories are not automatically added to that list.

Output reload is debounced and waits for both `sunder-dev` and its adjacent
`sunder-dev.imports.v1.json` to stabilize. The generated-output transaction journal
fences publication of that pair. Directory replacement and handoff-only changes are
observed. A failed build leaves the previous published output; a rejected reload keeps
the previous active generation. Fix the source/evidence and build again to retry.
Releasing the owning development session stops its output watch.

Run, smoke, owner sessions, and hot reload consume retained evidence without acquiring
it from Registry. Implementation/payload changes can reuse the receipt while the
manifest remains byte-identical. Changes to identity, dependencies, imported contracts,
uses, providers, or other manifest metadata require evidence for the new manifest.
C# local builds regenerate their handoff automatically. Outputs without that handoff
and Registry-backed imports require another explicit preparation.
Ordinary Build/Rebuild, generation, and Pack do not call this preparation endpoint.

The receipt lives under the Runtime V1 root at
`cache/development-import-authority/v1/<manifest-sha256>.json`. Repeated successful
preparation preserves identical receipt bytes and timestamps. Startup-cache cleanup
preserves these receipts; a full Runtime reset includes them in the existing cache
category. Missing, malformed, linked, or mismatched evidence fails closed with
preparation guidance. Failed acquisition preserves previously retained evidence.
Installing a .NET/Node execution runtime does not prepare package imports. A
`package.policy-required` import error is resolved by rebuilding with the current local
tooling or running `prepare-imports` with the exact selected inputs.

Registry-backed platform activation also needs a valid retained continuous-clock policy lease. The
Runtime's existing policy synchronizer refreshes those leases independently of loading.
Expiry, discontinuity or Registry catalog revocation can block an offline
load even when the original receipt remains intact. Loaded App platform obligations
remain enforced until whole-process exit is proven, including after source removal.
Local catalog evidence has no Registry lease or Registry expiry. Installed packages
and execution-runtime artifacts retain their own provenance and lifecycle checks.

## Dependencies And Readiness

Preparation does not start or install dependencies. The actual development/installed
session graph must satisfy reviewed package ranges, prerelease intent, exact RPC
identities, and ultimate ownership. A compatible actual dependency may have a different
version or implementation content hash from the restore-selected artifact.

An unavailable or incompatible optional selection is omitted and grants no consumer
uses. Package presence and required-provider readiness remain independent:

| Package presence | Required provider | When the selected package is unavailable |
| --- | --- | --- |
| Required | No | The dependency gate fails. |
| Required | Yes | The dependency gate fails. |
| Optional | No | The package can load with the selection omitted and its uses disabled. |
| Optional | Yes | Omission supplies no authorization; an unsatisfied required predicate blocks activation. |

An independently included selection can still authorize the same exact contract, and
an independent eligible provider can satisfy it. Restore content hashes do not rank or
pin RPC providers. Provider-only imports remain required dependencies and create no
consumer uses.

## Failure Boundaries

Local preparation permits at most 128 candidate archives, 512 MiB per archive and
1 GiB total input archives. It validates complete archive inventories and exact
manifest/catalog identities before retaining evidence. Local and Registry inputs
cannot be mixed in one preparation request.

Registry acquisition rejects redirects, response-origin drift, inconsistent metadata, changed
catalog content, and length/ETag mismatches. JSON is strict and bodies are bounded:
1 MiB per metadata response, 64 MiB aggregate downloaded evidence, and 96 MiB per
retained receipt. Each request and its body share a 15-second deadline; caller
cancellation remains cancellation. A manifest edit during preparation fails its
content fence. A failed development reload preserves the prior active session.
