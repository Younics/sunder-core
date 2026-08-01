# @sunder/package-tool

Builds exact-RID Node 24.18.1 Single Executable Application target leaves for Sunder process Runtime packages.

Production builds always use a SHA-256-verified pinned upstream Node distribution and revalidate cached downloads before reuse. The developer's current Node is used only by `sunder-package dev`, whose external launch metadata is adjacent to, and never inside, the canonical package tree.

Upstream Node SEA CI skips macOS x64 coverage. Sunder therefore requires its own native `osx-x64` protocol-handshake smoke test and does not infer compatibility from the arm64 build.

`fetchRegistryContract` is the read-only integration for immutable Registry RPC descriptors. It accepts a Registry origin plus an exact contract ID/version, rejects redirects and untrusted download URLs, bounds reads to 4 MiB, verifies SHA-256 and canonical bytes, and reparses the descriptor locally. `fetchRegistryContractBindings` performs the same checks and passes the validated descriptor to the TypeScript binding generator.
