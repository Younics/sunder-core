<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# @younics/create-sunder-package

**Start a Sunder extension in TypeScript or JavaScript.**

Create a ready-to-build Node service package or a React interface with its own
Node backend. The initializer sets up the SDK, build scripts, generated API
imports, sample services, and tests for [Sunder](https://sunderapp.io).

Use it for a headless integration, a personal automation tool, or an interactive
panel inside the Sunder workspace.

## Choose a preset

| Preset | Best for | Includes |
| --- | --- | --- |
| `node` (default) | Headless services and integrations | Node worker, sample service, tests |
| `react-node` | Desktop panels backed by Node | Everything above plus React, Vite, and a connected App view |

Both presets support TypeScript (`--language ts`, the default) and checked
JavaScript (`--language js`). JavaScript projects use `.js`/`.jsx` with JSDoc.

## Prerequisites

- **Node 24.18.1 or newer within Node 24** and npm.
- **Sunder and its CLI** to run, smoke-test, and install the package.
- A Sunder-managed **Node 24 runtime**, selected before running the package.

## Tutorial: your first React and Node extension

### 1. Create the project

```sh
npm create @younics/sunder-package@latest hello-sunder -- --preset react-node --package-id example.hello --package-name "Hello Sunder"
cd hello-sunder
npm install
```

The name before `--` selects the destination. Options after `--` are forwarded to
the initializer. Use an empty destination directory.

```text
hello-sunder/
  src/services.ts
  app/src/App.tsx
  app/src/main.tsx
  app/src/styles.css
  test/package.test.cjs
  package.json
  sunder.package.json
  sunder-contracts.lock.json
```

### 2. Understand the connection

`src/services.ts` exports a `messages` service with `send` and streaming `watch`
methods. `app/src/App.tsx` calls it through the generated API:

```tsx
import { useApi } from "@younics/sunder-sdk/react";
import { Self } from "#sunder/api";

// Inside your component:
const api = useApi(Self);
// Inside an effect or event handler:
const reply = await api.messages.send("Sunder App");
```

The generated `main.tsx` supplies `ApiProvider`. The starter component includes
loading, success, error, and cancellation handling. Keep calls in effects or
event handlers so rendering stays side-effect free.

### 3. Test and build

```sh
npm test
npm run sunder:dev:once
```

Tests generate APIs, check worker/browser types, and verify the starter service's
greeting, stream, and cancellation behavior. The development build creates
`dist/sunder-dev`.

### 4. Load it in Sunder

```sh
sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1
sunder dev package run dist/sunder-dev --watch
```

Open **Hello Sunder** in the workspace. Its view displays
**Hello from Hello Sunder: Sunder App** after the worker responds.

In a second terminal, run `npm run sunder:dev`. Change the heading in
`app/src/App.tsx` or add a service method in `src/services.ts`. The producer
rebuilds the package and Sunder loads completed updates. If you change the starter
greeting behavior, update its expected result in `test/package.test.cjs` too.

### 5. Package it

```sh
npm run build
npm run smoke
npm run package
```

Use the `.sunderpkg` path printed by the package command:

```sh
sunder dev package validate <archive.sunderpkg>
sunder package install --file <archive.sunderpkg>
```

The archive contains your worker and App output for the declared platforms.
Sunder provides the execution runtime.

## Other starting points

Headless TypeScript service:

```sh
npm create @younics/sunder-package@latest my-service -- --package-id example.service --package-name "My Service"
```

React plus JavaScript:

```sh
npm create @younics/sunder-package@latest my-panel -- --preset react-node --language js --package-id example.panel --package-name "My Panel"
```

## Initializer options

| Option | Purpose |
| --- | --- |
| Positional name | Destination folder. |
| `--output <directory>` | Explicit destination; takes precedence over the positional name. |
| `--preset node\|react-node` | Headless service or React plus Node. |
| `--language ts\|js` | TypeScript or checked JavaScript. |
| `--package-id <id>` | Stable lowercase dotted package identity, such as `example.panel`. |
| `--package-name <name>` | User-facing display name. |

If omitted, the package ID and display name are derived from the destination.
Choose an explicit ID for a package you plan to distribute.

## Your daily commands

| Script | Purpose |
| --- | --- |
| `npm test` | Generate APIs, type-check, and run service tests. |
| `npm run sunder:dev` | Watch source and rebuild the development package. |
| `npm run sunder:dev:once` | Build the development package once. |
| `npm run build` | Build current-platform output. |
| `npm run smoke` | Verify startup through the Sunder Host. |
| `npm run package` | Build the universal distribution archive. |

Commit your source, configuration, lockfile, and `sunder-contracts.lock.json`.
Generated `.sunder/` and `dist/` content stays untracked. The extension's version
lives in `sunder.package.json`; it is independent of the initializer's npm version.

## Troubleshooting and next steps

- **Destination is not empty:** choose a new folder or `--output` path.
- **Unsupported Node version:** install Node 24 within the declared engine range.
- **View cannot connect in a regular browser:** load it inside Sunder to provide the bridge.
- **No view appears for the Node preset:** that preset is headless; choose `react-node` for a UI.
- [Add your own typed service](../sdk/README.md)
- [Tool commands and package configuration](../package-tool/README.md)
- [Node development guide](../../../../../docs/SUNDER-NODE-PROCESS-PACKAGES.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [GPL-3.0](LICENSE).
