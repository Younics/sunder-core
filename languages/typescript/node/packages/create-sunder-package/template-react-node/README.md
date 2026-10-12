# SUNDER_PACKAGE_NAME

React App and Node service package `SUNDER_PACKAGE_ID`.

The configured `src/services` module exports ordinary `defineService` functions.
TypeScript or checked JavaScript supplies types; Sunder generates contracts, clients,
dispatch and Worker bootstrap. The browser uses `useApi(Self)` from the current root's
`ApiProvider`, and calls `api.messages.send(...)`. Root/component lifetimes manage
cancellation and cleanup. Browser code does not import Worker implementations.

```sh
npm install
npm test
npm run sunder:dev
npm run build
npm run package
```

Private services follow the complete owning package version: no Accept/Freeze or
owned baseline is needed. Public services retain explicit `contracts:accept`, reviewed
owned history and `contracts:freeze` before release. Commit the package contract lock;
keep `.sunder/` untracked. Normal builds never modify either tracked artifact.

This App receives the default private-service grants. Runtime consumers receive no
permission from the App. External package selection records reviewed consumer roles;
App/Runtime grants are generated independently. The browser waits for the document's
existing bridge readiness event without polling or eager discovery.

`npm test` checks source/generated Worker and browser types plus service behavior.
`sunder:dev` watches and emits the current-RID development package. `build` produces
separate Node and Vite App leaves. `package` emits twelve exact-RID leaves in one
deterministic archive with adjacent ownership evidence. React is optional outside
this preset; framework-neutral Apps use `createBrowserContext(Self)` directly.

`smoke` delegates to the managed Host. Select an approved Node execution runtime first
with `sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1`.
Native addons belong to exact-RID `nativeAssets`; Node/runtime distributions are not
package payload. Add optional capabilities and real `configure(context)` lifecycle
hooks as the package needs them.
