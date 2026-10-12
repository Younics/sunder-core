# Core platform engineering tool

The non-public `languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build`
project is Core's platform-catalog and Stack-transport engineering executable.
Its existing project identity is retained because the Core build graph uses it.

## Platform catalog lifecycle

`platform-contracts check|accept|freeze|export|import-publication --core-root <root>`
derives contracts from current Core source and manages the reviewed platform baseline.
See the [platform command reference](../platform-contracts/README.md) for exact
invocations and the [catalog specification](../../specs/rpc/v1/authoring/platform-catalog/README.md)
for canonical content and Registry authority.

| Action | Options in addition to required `--core-root` |
| --- | --- |
| `check` | Read-only complete source/baseline validation. |
| `accept` | Optional `--key`, `--current-version`, `--next-version`, `--acknowledge-breaking-change` (`true` or `false`). |
| `freeze` | Required `--catalog-release`; optional `--key`. |
| `export` | Required `--catalog-release` and `--output`; emits a complete canonical catalog at a new path. |
| `import-publication` | Required `--catalog` and `--registry`; verifies an existing Registry publication. |

Options are closed per action. Unknown/duplicate options fail. Existing-version
acceptance requires the exact key and old/new version pair; breaking acceptance
also requires acknowledgement. Bootstrap permits only key
`stack-contributor-rpc-service-adapter`, next version `2.0.0`, and the reviewed
source descriptor/ABI. Catalog releases use exact stable SemVer.

Export requires complete frozen/published current source and a new output path
with an existing parent outside Core `languages/`. Leases, bounded input reads,
source/reference/baseline revalidation, staged writes and final byte checks protect
publication. The selected Registry establishes authority; there is no catalog
signing service. `CheckSunderPlatformContracts` is an explicit MSBuild target;
ordinary Build does not accept, freeze, export, or publish a catalog.

## Stack transport source generation

SDK Stack builds use `core-stack-transport generate`; the Core Host build uses
`core-stack-transport generate-host`. Both consume an evaluated request through
`--core-root`, `--request`, `--request-sha256`, and `--request-stamp`. Source and
references are revalidated, and generated output is immutable, configuration/SHA-keyed
`obj` content. Generated transport introduces no Worker/Build/tool runtime dependency.

The historical descriptor check remains a separate read-only engineering oracle:

```sh
dotnet run --project languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build -- check-platform-catalog-migration \
  --descriptor languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json
```

It verifies `sunder.stack.contributor` `2.0.0`, 48 definitions, six methods, descriptor
SHA-256 `38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898`
and ABI SHA-256 `6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8`.
This oracle is not a production authoring input or publication authorization.

## Execution runtime ownership

Host acquires .NET Runtime and Node directly from configured official vendor
sources under the [official-runtime contract](../../docs/OFFICIAL-RUNTIME-HANDOFF.md).
Packages continue to declare their logical runtime requirement and exact RID;
package manifests, module ABIs and six-RID projections are unchanged.
