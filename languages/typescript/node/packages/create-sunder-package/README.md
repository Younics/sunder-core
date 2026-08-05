# create-sunder-package

> **Release/source channel:** The npm tarball README describes that published version. The repository copy tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

Run `npm create sunder-package@latest` to scaffold a TypeScript Node `worker` Runtime package. Add `-- --template react-node` for a combined hosted web App target and full-trust pinned Node SEA Runtime target.

The shipped presets are `node` and `react-node`. React/Vite is one web template, not a format requirement. This package does not scaffold managed .NET/Avalonia packages or arbitrary Python, Rust, Go, or other process toolchains; those authoring routes can emit the same canonical `.sunderpkg` format through their own tooling.
