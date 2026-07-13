# Sunder 1.1 Breaking Baseline

`1.1.0` intentionally replaces the unused public `1.0.0` developer package line. It is not binary compatible with 1.0, and no aliases, forwarding types, obsolete members, or package compatibility shims are provided.

## Breaking Inventory

- Package identity/version/range primitives moved to `Sunder.Sdk.Packaging`; duplicate Format implementations were removed.
- Package configuration and session abstractions were replaced by settings, role-local workspace, installed/development session controls, callbacks, and typed Runtime operations.
- App-to-Runtime calls now require the authenticated `/api/handshake` protocol negotiation before `/api/v1` use.
- Runtime package operations and streams are lease-bound to a package generation. Streams use bounded newline-framed `event`, `completed`, and `error` envelopes; EOF or a partial frame before a terminal envelope is a protocol failure.
- Registry DTOs, Runtime DTOs, `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, and `Sunder.Sdk.Stacks` have new immutable API snapshots under `tests/Sunder.Sdk.PublicApi.Tests/PublicApi`.
- Public developer packages (`Sunder.Sdk*`, `Sunder.Package.Build`, and `Sunder.Package.Templates`) are coordinated at `1.1.0`.

## Mixing Rule

Every 1.1-built manifest includes `sdk-baseline-1-1.v1`. A 1.1 Host accepts `sdkPackageVersion >=1.1.0 <1.2.0` and requires that capability before assembly load. Consequently, a 1.0 Host rejects 1.1 packages as an unknown requirement, while a 1.1 Host rejects 1.0 packages with a rebuild diagnostic. A mixed family must never reach assembly loading or fail as `TypeLoadException`.

Future compatible 1.1 patches update the shipped snapshots additively. Any further intentional contract break requires a new coordinated baseline, capability, package range, and breaking inventory; do not re-add 1.0 shims.
