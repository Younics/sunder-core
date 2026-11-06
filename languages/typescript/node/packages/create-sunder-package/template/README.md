# SUNDER_PACKAGE_NAME

A Node service extension for Sunder. Package ID: `SUNDER_PACKAGE_ID`.

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

This is a **headless** package: it runs a service without adding a desktop view.
The starter tests exercise its greeting, streaming, and cancellation behavior.

## Customize the service

Open the `src/services` file selected by `services.module` in `sunder.package.json`.
It exports a `messages` service with `send` and `watch` methods.

1. Change the greeting returned by `send`.
2. Update the expected greeting in `test/package.test.cjs`.
3. Run `npm test` to check source types and service behavior.
4. Add typed methods for your own integration or automation logic.

TypeScript signatures or JavaScript JSDoc describe the values. Sunder generates
contracts, typed APIs, registration, and Worker bootstrap.

## Daily commands

| Script | Purpose |
| --- | --- |
| `npm test` | Generate APIs, check types, and run service tests. |
| `npm run sunder:dev` | Watch source and rebuild `dist/sunder-dev`. |
| `npm run sunder:dev:once` | Build the development package once. |
| `npm run build` | Build current-platform output. |
| `npm run smoke` | Check startup using the Sunder Host. |
| `npm run package` | Build the universal `.sunderpkg` archive. |

Run `npm run sunder:dev` in a second terminal while Sunder watches the output.
The build watcher and the running development package are separate processes.

## Package and install

Set the extension's version in `sunder.package.json`, then run `npm run package`.
Use the reported archive path:

```sh
sunder dev package validate <archive.sunderpkg>
sunder package install --file <archive.sunderpkg>
```

## Configuration and source control

- Edit `sunder.package.json` for package identity, version, service entry, and capabilities.
- Add `workerCapabilities` before using Host settings, state, files, secrets, or logging.
- Export `configure(context)` when you need startup or cleanup behavior.
- Commit source, npm lockfile, and `sunder-contracts.lock.json`.
- Keep generated `.sunder/` and `dist/` output untracked.

The starter's private services follow your extension version. Public APIs use
the `contracts:accept`, `contracts:check`, and `contracts:freeze` scripts. Calling
services through the Runtime broker requires explicit consumer use; providing a
service does not itself declare that use.

## Learn more

- [SDK service examples](https://github.com/Younics/sunder-core/blob/main/languages/typescript/node/packages/sdk/README.md)
- [Build and packaging tutorial](https://github.com/Younics/sunder-core/blob/main/languages/typescript/node/packages/package-tool/README.md)
- [API selection and capabilities](https://github.com/Younics/sunder-core/blob/main/docs/package-development/NODE-TOOL-REFERENCE.md)
