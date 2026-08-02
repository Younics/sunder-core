# @sunder/package-tool

> **Release/source channel:** The npm tarball README describes that published version. The repository copy tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

Builds canonical development trees and exact-RID Node 24.18.1 Single Executable Application target leaves for Sunder `process` Runtime packages. It also builds framework-agnostic static `web` App targets; Vite is the current adapter and React is only a scaffold preset.

This tool is not the canonical package format itself and is not a general Python, Rust, Go, or arbitrary-process packager. Other toolchains may emit the same canonical manifest, content index, payload layers, and host-supported `process` protocol without depending on this npm package.

Production builds always use a SHA-256-verified pinned upstream Node distribution and revalidate cached downloads before reuse. The developer's current Node is used only by `sunder-package dev`, whose external launch metadata is adjacent to, and never inside, the canonical package tree.

Upstream Node SEA CI skips macOS x64 coverage. Sunder therefore requires its own native `osx-x64` protocol-handshake smoke test and does not infer compatibility from the arm64 build.

`fetchRegistryContract` is the read-only integration for immutable Registry RPC descriptors. It accepts a Registry origin plus an exact contract ID/version, rejects redirects and any download URL other than the exact same-origin descriptor endpoint, bounds reads to 4 MiB, verifies size, SHA-256, ETag when present, identity, and canonical bytes, and reparses the descriptor locally. `fetchRegistryContractBindings` performs the same checks and passes the validated descriptor to the TypeScript binding generator. Those checks establish descriptor integrity relative to Registry metadata; they do not attest that provider code is safe.

Target-leaf producers and recursive aggregators coordinate through an explicit discovery root. The built-in `build` and `package` commands use `dist/targets`; custom `package --leaf` selections must also pass `--discovery-root <path>`, and programmatic `LeafBuildInput` and `LeafDiscoverySource` values must name the same containing root.
