# SUNDER_PACKAGE_NAME

A React desktop view backed by a Node service. Package ID: `SUNDER_PACKAGE_ID`.

## Get started

Use Node 24.18.1 or newer within Node 24, the npm version in `package.json`, and
Sunder with its CLI on PATH.

```sh
npm install
npm test
npm run sunder:dev:once
sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1
sunder dev package run dist/sunder-dev --watch
```

Open this package's view in Sunder. After connecting to the worker, it displays
a greeting from the `messages.send` service.

## Customize your first feature

1. Edit the heading in the `app/src/App` component.
2. Edit the greeting returned by `send` in the configured `src/services` module.
3. Update the expected greeting in `test/package.test.cjs`.
4. Run `npm test`, rebuild, and open the view again.

The App uses `useApi(Self)` to call generated services. Its root creates a browser
context and supplies `ApiProvider`; keep that setup when adding components. Make
calls in effects or event handlers and handle loading, errors, and cancellation.

Sunder generates typed APIs and Worker bootstrap. Browser code imports
`#sunder/api`, not the worker implementation. TypeScript projects use `.ts`/`.tsx`;
JavaScript projects use `.js`/`.jsx` with checked JSDoc.

## Daily commands

| Script | Purpose |
| --- | --- |
| `npm test` | Generate APIs, check worker/browser types, and test services. |
| `npm run sunder:dev` | Watch source and rebuild `dist/sunder-dev`. |
| `npm run sunder:dev:once` | Build the development package once. |
| `npm run build` | Build current-platform Node and web output. |
| `npm run smoke` | Check startup using the Sunder Host. |
| `npm run package` | Build the universal `.sunderpkg` archive. |

For live development, keep Sunder's output watcher running and launch
`npm run sunder:dev` in a second terminal. The web UI runs inside Sunder, which
supplies its service bridge.

## Package and install

Set your extension version in `sunder.package.json`, then run `npm run package`.
Use the archive path printed by the command:

```sh
sunder dev package validate <archive.sunderpkg>
sunder package install --file <archive.sunderpkg>
```

## Configuration and source control

- Edit services, components, styles, and package configuration as ordinary source.
- Keep the private starter service for your own App/backend interaction.
- Select external package APIs explicitly before using their generated tokens.
- Add worker capabilities when using Host settings, state, files, secrets, or logging.
- Commit the npm lockfile and `sunder-contracts.lock.json`; keep `.sunder/` and `dist/` untracked.

Public service APIs have an explicit accept/check/freeze workflow. The App and
worker declare their consumer use independently. Use the tool reference when
adding cross-package calls or public extension contracts.

## Learn more

- [Add a service and call it from React](https://github.com/Younics/sunder-core/blob/main/languages/typescript/node/packages/sdk/README.md)
- [Build and packaging tutorial](https://github.com/Younics/sunder-core/blob/main/languages/typescript/node/packages/package-tool/README.md)
- [API selection and lifecycle reference](https://github.com/Younics/sunder-core/blob/main/docs/package-development/NODE-TOOL-REFERENCE.md)
