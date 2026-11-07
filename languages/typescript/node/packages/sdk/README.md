<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# @younics/sunder-sdk

**Build Sunder extensions with TypeScript, JavaScript, and optional web UI.**

[Sunder](https://sunderapp.io) is an extensible desktop workspace. This SDK lets
your package define services in Node, call them through generated typed APIs, and
connect a browser or React interface to its backend.

Use ordinary functions and business types. Sunder's package tooling generates the
service contracts, clients, registration, and worker bootstrap. The main SDK has
**zero runtime dependencies**; React is an optional peer for the React entry point.

## What can I build?

- A React panel backed by a Node service.
- A headless integration, automation tool, or data-processing service.
- A package that streams progress or returns downloadable content.
- A reusable API consumed by other Sunder packages.

## Install and prerequisites

Use **Node 24.18.1 or newer within Node 24**, npm, and Sunder with the `sunder` CLI.
The generated project specifies its npm/toolchain versions.

```sh
npm install @younics/sunder-sdk
```

For a new extension, use the initializer in the tutorial. It configures both this
SDK and `@younics/sunder-package-tool`.

## Tutorial: call a Node service from React

### 1. Create a package

```sh
npm create @younics/sunder-package@latest hello-sunder -- --preset react-node --package-id example.hello --package-name "Hello Sunder"
cd hello-sunder
npm install
```

The project has Node service code in `src/services.ts` and React code in
`app/src/`. Its generated API is available through `#sunder/api`.

### 2. Add a greeting service

Append this definition to `src/services.ts`, below the starter `messages` service.
The file already imports `defineService` and `ServiceContext` from this SDK.

```ts
export const greeting = defineService({
  async greet(name: string, context: ServiceContext): Promise<string> {
    context.signal.throwIfAborted();
    return `Hello, ${name}! Your Node service is connected.`;
  },
}, {
  constraints: {
    "greet.name": { minLength: 1, maxLength: 120 },
    "greet.result": { minLength: 1, maxLength: 256 },
  },
});
```

The service is private to your package by default. Its owning App can call it
through the generated API. Constraints describe accepted values, and cancellation
comes from the current invocation's `ServiceContext`.

### 3. Display its result

Replace `app/src/App.tsx` with:

```tsx
import { useEffect, useState } from "react";
import { useApi } from "@younics/sunder-sdk/react";
import { Self } from "#sunder/api";

export function App() {
  const api = useApi(Self);
  const [message, setMessage] = useState("Connecting...");

  useEffect(() => {
    const cancellation = new AbortController();
    void api.greeting.greet("Sunder", { signal: cancellation.signal }).then(
      result => { if (!cancellation.signal.aborted) setMessage(result); },
      () => { if (!cancellation.signal.aborted) setMessage("Check the package logs and try again."); },
    );
    return () => cancellation.abort();
  }, [api]);

  return <main><h1>Hello Sunder</h1><p role="status">{message}</p></main>;
}
```

Keep the generated `main.tsx`. It creates a browser context and provides it through
`ApiProvider`. The React component uses the generated client rather than importing
the worker implementation.

### 4. Check and run

```sh
npm test
npm run sunder:dev:once
sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1
sunder dev package run dist/sunder-dev --watch
```

Open **Hello Sunder** in the workspace. The view should display
**Hello, Sunder! Your Node service is connected.**

For live development, run `npm run sunder:dev` in a second terminal. It rebuilds
the development package while Sunder watches the resulting output.

## JavaScript works too

Create the same React preset with `--language js`. Services use checked JSDoc,
and the App uses `.jsx`. For example, append this service to `src/services.js`
(which already imports `defineService`):

```js
export const greeting = defineService({
  /**
   * @param {string} name
   * @param {import("@younics/sunder-sdk").ServiceContext} context
   * @returns {Promise<string>}
   */
  async greet(name, context) {
    context.signal.throwIfAborted();
    return `Hello, ${name}! Your Node service is connected.`;
  },
}, {
  constraints: {
    "greet.name": { minLength: 1, maxLength: 120 },
    "greet.result": { minLength: 1, maxLength: 256 },
  },
});
```

The React example above also works as `App.jsx`. Both languages use the same
service generation and validation workflow.

## Use generated APIs

| Import | Purpose |
| --- | --- |
| `@younics/sunder-sdk` | Node service authoring, worker APIs, and natural content values |
| `@younics/sunder-sdk/browser` | Framework-neutral browser context and browser-safe APIs |
| `@younics/sunder-sdk/react` | `ApiProvider` and `useApi` for React |
| `#sunder/api` | Generated `Self`, selected-package tokens, models, and clients |

Worker code uses `context.api(Self)` when it has explicitly declared Runtime
consumer use. Browser code uses `createBrowserContext(Self)` and React components
use `useApi(Self)`. Call services in effects or event handlers. The owning context
controls call lifetimes and should be disposed when its UI root is removed.

Select another package through the package tool before using its generated token.
Selection records its API and consumer roles in `sunder-contracts.lock.json`.
See [package selection](../../../../../docs/package-development/NODE-TOOL-REFERENCE.md#package-selection-and-consumer-roles).

## Add richer behavior

- **Streaming:** use an async generator to report progress or watch changes.
- **Files and content:** return `SunderContent`; consumers open and dispose it.
- **Settings, state, secrets, and logs:** declare the required `workerCapabilities`
  and use the Host-backed worker context.
- **Initialization and cleanup:** export `configure(context)` for lifecycle hooks;
  generated bootstrap continues to own service registration.
- **Public APIs:** opt into public exposure and use the tool's contract-versioning workflow.

The [SDK reference](../../../../../docs/package-development/NODE-SDK-REFERENCE.md)
contains examples for content, provider selection, errors, cancellation, and lifetimes.

## Package and share

```sh
npm run package
```

This builds the declared platform targets and reports a `.sunderpkg` archive.
Use `sunder dev package validate <archive.sunderpkg>` to validate it, then
`sunder package install --file <archive.sunderpkg>` to install it locally.

## Troubleshooting

| Symptom | Next step |
| --- | --- |
| `#sunder/api` cannot be resolved | Run `npm run typecheck`; it generates APIs before checking source. |
| Browser context is unavailable | Load the packaged view in Sunder; it needs the Sunder bridge. |
| Worker cannot start | Ensure the Sunder-managed Node runtime is installed. |
| Calls are denied | Check the selected API's consumer roles and package permissions. |

## Learn more

- [Project initializer](../create-sunder-package/README.md)
- [Build and package tooling](../package-tool/README.md)
- [Node package development](../../../../../docs/SUNDER-NODE-PROCESS-PACKAGES.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [GPL-3.0](LICENSE).
