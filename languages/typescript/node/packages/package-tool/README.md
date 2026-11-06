<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# @younics/sunder-package-tool

**Turn TypeScript or JavaScript services into a Sunder package.**

This tool generates typed service APIs, builds Node workers and optional web
interfaces, watches source changes, and creates distributable `.sunderpkg` archives
for [Sunder](https://sunderapp.io).

Use it alongside `@younics/sunder-sdk` when developing an integration, automation
service, or React-backed desktop extension. The initializer sets up the normal
scripts; you can also run the CLI directly in an existing package project.

## Install and prerequisites

Use **Node 24.18.1 or newer within Node 24** and npm. Sunder and its CLI are required
to run, smoke-test, inspect selected package archives, and install your extension.

```sh
npm install --save-dev @younics/sunder-package-tool
```

For a new project, use [the initializer](../create-sunder-package/README.md) rather
than assembling the configuration by hand.

## Tutorial: build and package a Node service

### 1. Create a project

```sh
npm create @younics/sunder-package@latest hello-worker -- --package-id example.worker --package-name "Hello Worker"
cd hello-worker
npm install
```

The default preset contains a headless service in `src/services.ts`, its behavior
test, and `sunder.package.json`. Add `--preset react-node` when creating the
project to include a React App.

### 2. Generate and check the API

```sh
npm test
```

The generated script runs API generation, TypeScript checking, and the starter's
service tests. `#sunder/api` resolves generated clients through the project's
standard package-import mapping.

For generation alone:

```sh
npx sunder-package generate --project .
```

### 3. Build a development package

```sh
npm run sunder:dev:once
sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1
sunder dev package run dist/sunder-dev --watch
```

The Node preset starts a headless worker; it does not add a workspace view. The
React preset also adds a view connected to the worker's service.

In a second terminal, run `npm run sunder:dev`. Source changes regenerate APIs and
rebuild `dist/sunder-dev`; Sunder's output watcher applies complete updates.

### 4. Build and smoke-test

```sh
npm run build
npm run smoke
```

`build` produces the current platform's package output. `smoke` uses the Sunder
Host to exercise startup of that built package. If the CLI is outside PATH, set
`SUNDER_CLI` to its executable path.

### 5. Produce the universal archive

```sh
npm run package
```

The package command builds the declared targets for Windows, macOS, and Linux on
x64 and arm64, then reports the archive path. Use that path with:

```sh
sunder dev package validate <archive.sunderpkg>
sunder package install --file <archive.sunderpkg>
```

Workers contain your bundled code and package-owned assets. The shared Node
execution runtime is managed by Sunder.

## Command guide

| Command | Result |
| --- | --- |
| `sunder-package generate` | Generated contracts, typed APIs, and worker registration |
| `sunder-package dev` | Current-platform `dist/sunder-dev` package |
| `sunder-package dev --watch` | Continuous source rebuilds |
| `sunder-package build --rid linux-x64` | Output for one specified platform |
| `sunder-package smoke` | Host-backed smoke check of built output |
| `sunder-package package` | Universal `.sunderpkg` archive |

Use `--project <directory>` to target another project. Run these through npm
scripts or `npx` so the project's installed tool version is used.

## Configure your package

The initializer creates a `sunder.package.json` with this basic shape:

```json
{
  "schemaVersion": 3,
  "id": "example.worker",
  "name": "Hello Worker",
  "version": "1.0.0",
  "workerCapabilities": [],
  "services": { "module": "src/services.ts" }
}
```

Use `src/services.js` for checked JavaScript. Add optional worker capabilities
when using Host-provided state, settings, files, secrets, or logging. The React
preset adds the web-App configuration for you.

Commit source, configuration, and `sunder-contracts.lock.json`. Generated files
under `.sunder/` and `dist/` are outputs. Run generation before invoking standalone
TypeScript or Vite commands.

## Consume another package

Select a local archive using its package ID and a compatible version range:

```sh
npx sunder-package api add example.weather --version ">=1.0.0 <2.0.0" --candidate ./weather.sunderpkg
npx sunder-package api list
npm run typecheck
```

This example requires an actual `example.weather` archive exposing a public API.
For a published dependency, replace `--candidate` with your explicit
`--registry https://your-registry.example/`. Generated clients then appear under
the selected package token in `#sunder/api`.

Use `api update` to review a changed selection and `contracts restore` to restore
the exact existing selection. See the
[selection and cache reference](../../../../../docs/package-development/NODE-TOOL-REFERENCE.md#package-selection-and-consumer-roles).

## Private and public versions

The starter's private services follow your extension's version in
`sunder.package.json`. They build directly. Public service APIs use an explicit
reviewed lifecycle:

```sh
npm run contracts:accept
npm run contracts:check
npm run contracts:freeze
```

Use those commands when deliberately exposing public contracts, review and commit
their baseline, and consult the
[public contract workflow](../../../../../docs/package-development/NODE-TOOL-REFERENCE.md#private-and-public-versions)
when changing a released API. The extension version, public contract versions,
and npm tool version have distinct purposes.

## Troubleshooting and advanced usage

- **Unresolved generated import:** run `generate` and check the `#sunder/api` mapping.
- **Node engine mismatch:** use the required Node 24 version.
- **Missing contract cache:** explicitly restore the committed selection using its
  original archive/catalog or Registry.
- **Native dependency fails on another platform:** declare and supply the matching
  exact-RID native assets.
- [Tooling reference](../../../../../docs/package-development/NODE-TOOL-REFERENCE.md)
- [SDK examples](../sdk/README.md)
- [Create a new package](../create-sunder-package/README.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [MIT](LICENSE).
