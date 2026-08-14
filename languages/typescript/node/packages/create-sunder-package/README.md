# create-sunder-package

> **Release/source channel:** The npm tarball README describes that published version. The repository copy tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

Run `npm create sunder-package@latest` to scaffold a TypeScript Node `worker` Runtime package. Add `-- --template react-node` for a combined hosted web App target and full-trust pinned Node SEA Runtime target.

The shipped presets are `node` and `react-node`. React/Vite is one web template, not a format requirement. This package does not scaffold managed .NET/Avalonia packages or arbitrary Python, Rust, Go, or other process toolchains; those authoring routes can emit the same canonical `.sunderpkg` format through their own tooling.

Both presets author package schema V2, emit Package Manifest V3, use an explicit empty `workerCapabilities` allowlist, and include a strict RPC descriptor V2 with package-only contract exposure, an owner-only Runtime provider locked to contract version `2.0.0`, and build-integrated typed RPC bindings. Add the exact optional Worker V3 capabilities when worker code starts using settings, storage, storage migrations, workspace, secrets, logging, callbacks, or auth. `storage.key-migration.v1` closes over `storage.v1`, and `auth.v1` closes over `callbacks.v1`. The React preset also declares its App caller role and exact required `discover`/`invoke` actions; it does not use an RPC version range.

`sunder-package generate` writes bindings under `.sunder/generated/rpc/`; template typechecking runs generation before `tsc`, while development and production builds generate them as part of the build. The generated `.sunder/` directory is local output and is not scaffolded or committed.
