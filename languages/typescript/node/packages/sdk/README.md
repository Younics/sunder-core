# @sunder/sdk

The published tarball describes its release; this repository documents current source.
Use the matching `sdk/v*` tag when auditing an existing release.

Sunder packages author ordinary services in TypeScript or checked JavaScript. The
package tool derives their portable contracts and generates registration, Worker
bootstrap, validated value conversion and package-local clients. The main SDK has
no runtime dependencies. React is an optional peer used only by `@sunder/sdk/react`.

## Define a service

Configure one `services.module` in `sunder.package.json`, then export definitions:

```ts
import { defineService, type ServiceContext } from "@sunder/sdk";

export const greeting = defineService({
  async greet(name: string, context: ServiceContext): Promise<string> {
    context.signal.throwIfAborted();
    return `Hello, ${name}!`;
  },
}, {
  constraints: { "greet.name": { minLength: 1, maxLength: 200 } },
  runtimeUse: {},
});
```

JavaScript uses exactly the same definition with checked JSDoc:

```js
import { defineService } from "@sunder/sdk";
export const greeting = defineService({
  /** @param {string} name @returns {Promise<string>} */
  async greet(name) { return `Hello, ${name}!`; },
});
```

The compiler does not execute source to discover a contract. It requires complete
supported parameter/result types and rejects ambiguous `any`, `unknown`, dynamic
method inventories, generic/overloaded methods and unsupported values. Ordinary
method bodies can use normal Node APIs. A final `ServiceContext` or `AbortSignal`
parameter is supplied out of band. Unary methods return a value, promise or `void`;
async iterables are streams. An uniterated generated stream performs no RPC.

Supported values include primitives, ordinary records, optional/nullable fields,
lists, string-keyed maps, supported literal enums/tagged unions, productive named
recursion, `bigint`, `Uint8Array`, explicit `JsonValue`/`JsonObject`, natural
`SunderContent`, and typed `ServiceReference<T>` values. `number` means finite binary64; use a semantic `format: "integer"` or
`"float32"` constraint when those semantics are intended. Formatted strings and exact
decimal constraints remain explicit. Absence, null and present values stay distinct.
Other native classes, tuples, arbitrary unions and custom codecs are not inferred.

Constraints name `method.parameter`, `method.result`, or `Type.property` paths.
They supply facets, not a second schema. Public exposure, exceptional stable wire
names, provider IDs for multiple implementations, idempotency and errors likewise
remain explicit semantic intent. All values still pass the existing RPC V1 codecs
and validators at the process boundary.

Private services default to package exposure and use the complete owning package
SemVer, including metadata. They need no independent Accept/Freeze or owned baseline.
Public services use `exposure: "public"` and retain reviewed contract history. See
[the tool workflow](../package-tool/README.md).

An ordinary **owned concrete** `defineService` also declares its owning App's
intent: private defaults are required with all applicable actions; public defaults
are optional (`required: false`) with all applicable actions. Making that service
public therefore keeps its own UI callable without adding `appUse`. Explicit
`appUse` readiness/actions override the default. Public declaration-only services
still need explicit App intent; binding implementations to local declarations or
imported contracts creates no additional consumer grant. The compiler preserves
declaration-owned policy across those bindings. Runtime broker use remains a
separate, explicit `runtimeUse` policy.

## Use generated APIs

`#sunder/api` exports `Self` and Pascal-case selected-package aliases:

```ts
import { Self, Weather } from "#sunder/api";

const own = context.api(Self);
const weather = context.api(Weather);
const message = await own.greeting.greet("Sunder", { signal });
```

`Self` uses the normalized source definition keys, even when distinct contracts
preserve the same wire service name. Selected packages with one public contract
expose its services directly. Multiple contracts get full-contract-ID camel-case
scopes: for `example.dependency.first` and `example.dependency.second`, both with
service `messages`, use `dependency.exampleDependencyFirst.messages.send(request)`
and `dependency.exampleDependencySecond.messages.send(request)`. Scope and service
name collisions are checked within their actual object scope; no suffix or hash
chooses a winner. Imported calls take the descriptor's request value (including an
explicit `{}` for an empty request) and return its result value. They do not guess
the producer language's original parameter list or `void` spelling.

Acquiring an API performs no discovery. Calls lazily resolve exactly one matching
active provider; ambiguity, catalog reset and stale identities fail without retry.
Each call/stream owns cancellation, deadline and terminal cleanup. Generated tokens
are bound to the package's current binding generation, not a global transport.
Tokens grant no authority. In particular, providing a service does not grant Runtime
broker use: declare `runtimeUse` explicitly when needed.

Framework-neutral browser owners use `createBrowserContext(Self)` from
`@sunder/sdk/browser` and call `context.api(Self)`. Dispose the context when its root
is removed. It waits for the immutable document's `sunder:ready` event and respects
the existing bridge lifetime signal; it does not poll or introduce a new protocol.

React roots provide that context through `ApiProvider` from `@sunder/sdk/react`.
Components use `useApi(Self)`. Each hook's effect owns only that component's calls;
StrictMode cleanup/remount creates a fresh child owner. Call services in effects or
event handlers, not during render. The browser SDK and generated declarations do not
require Node ambient types. Worker code does not import React.

Each service has a generated `Providers` catalog with `list`, `get`, `watch`, and
`resolve`. A provider exposes its immutable `snapshot`, exact-bound `.Service`, and
`.Reference`. The selected-package resolver retains the reviewed package range;
list/watch can report other visible exact-contract providers. No catalog operation
grants permissions. Advanced `Catalogs` and the raw RPC APIs remain available for
deliberate low-level integrations.

## Natural content and provider references

```ts
import { defineService, SunderContent, type ServiceReference } from "@sunder/sdk";
import { Self, Services } from "#sunder/api";

export const files = defineService({
  download(): SunderContent {
    return SunderContent.fromBytes(new Uint8Array([1, 2, 3]), { fileName: "data.bin" });
  },
}, { runtimeUse: {} });

type Work = { greeting: ServiceReference<typeof greeting>; attachment: SunderContent };
const provider = await context.api(Self).greeting.Providers.resolve();
await provider.Reference.Service.greet("Sunder");

const content = await context.api(Self).files.download();
try {
  const stream = await content.openRead(signal); // ReadableStream<Uint8Array>
  // Consume the stream and release its reader when finished.
} finally {
  await content.dispose();
}
```

`fromBytes` privately copies bytes and exposes a known SHA-256. `fromStream` accepts
a `ReadableStream<Uint8Array>` or `AsyncIterable<Uint8Array>`, consumes its current
position once, and owns the source unless `leaveOpen: true`. Dispose stream content
even if it was never opened. Node's `contentFromFile(path, options)` convenience is
lazy and locally repeatable and never deletes the source file. It is exported only
from the Node entry point. Browser authors import `SunderContent` from
`@sunder/sdk/browser`.

Generated visitors register nested/optional/union/list/map resources asynchronously
and coalesce aliases per value. A returned content object retains its call scope
after unary return, stream completion, or early iteration exit. Disposing the last
received content closes that scope. Either the original call signal or the original
enumeration signal, the Host-clamped deadline, and API/document/component owner
retirement revoke retained content earlier, even after the iterator has ended.
`disposeAsync()` drains an API owner's outstanding resources. Borrowed request
content cannot extend a provider invocation. Forwarding content reads through the
receiver's authority and registers a new reference for the new target; it never
forwards an old token as a grant. Local repeatability does not change the existing
default single-use transport policy. Sending does not dispose author-owned content.

Generated streaming methods return a lazy `ServiceStream<T>`. Supply independent
enumeration cancellation with `.withCancellation(signal)` or
`stream[Symbol.asyncIterator](signal)`; the method's `{ signal }` remains separate:

```ts
const stream = context.api(Self).documents.watch({ signal: caller.signal });
for await (const reply of stream.withCancellation(enumerator.signal)) {
  heldContent = reply.content[0];
  break;
}
// Either cancellation still revokes heldContent after this early exit.
enumerator.abort();
```

Use `ServiceReference<typeof sourceDefinition>` for owned declarations and
`ServiceReference<typeof Services.Weather.forecast>` for an imported service token
(the `Services` tree follows the same package/contract/service scopes as `api`).
JSDoc uses the identical types through `import("@sunder/sdk")` or the browser entry.
`SelfModels` and selected `<Token>Models` remain type-only namespaces.

Checked JavaScript uses ordinary implementation inference for content returns and
JSDoc for service-reference identity; it requires no authored schemas or codecs:

```js
import { defineService, SunderContent } from "@sunder/sdk";

export const greeting = defineService({
  /** @param {string} name */
  greet(name) { return `Hello, ${name}`; },
}, { runtimeUse: {} });

export const documents = defineService({
  download() { return SunderContent.fromBytes(new Uint8Array([1, 2, 3])); },
  /** @param {import("@sunder/sdk").ServiceReference<typeof greeting>} reference */
  greetThrough(reference) { return reference.Service.greet("Sunder"); },
});
```

A reference carries the full eleven-field provider locator. Send and receive both
validate the exact visible activation, including both increases and decreases in
session generation. The receiver attaches its own typed client and API lifetime;
the sender's service object and temporary decoding scope are not transferred.
References come from existing provider bindings, not public constructors or callback
registration. Ordinary JSON serialization of natural resources rejects use outside
the generated marshaller. Browser resource adapters use the same existing scoped
RPC/content operations, with full activation snapshots; no RPC framing is added.

### Binding retirement and request participants

Providers and references expose the same natural lifecycle API:

```ts
readonly retirementSignal: AbortSignal;
ensureCurrent(signal?: AbortSignal): Promise<void>;
tryReportInvariantViolation(error: unknown, signal?: AbortSignal): Promise<boolean>;
```

One receiver-owned observer starts on actual catalog/reference/service use, using
both cursor components, an initial snapshot, subscription startup, and a second
snapshot before readiness. It retires issued bindings before recovery from EOF,
reset, or invalid observations. Recovery creates fresh bindings; existing references
and already acquired proxies never retarget. `.Reference` and `retirementSignal`
remain inspectable after retirement and disposal. A business run can link that
signal to its own cancellation without watching catalogs or managing RPC leases.

`ensureCurrent` verifies all eleven identity fields through the receiver's broker.
Invariant reporting returns `false` for stale pre-report validation and preserves
structured permission/unavailable/dispatch/retry failures. Successful reporting
retires the exact local binding; its acknowledgement survives that retirement.

Generated request visitors automatically register referenced services as operation
participants on send and provider decode. Participant retirement cancels the call
and the domain method/stream's `ServiceContext.signal`, even before the domain code
invokes that target. Participants and exact-target retirement stay linked through
retained content; output references borrow the receiver lifetime without retaining
their decoding scope. Limits are 128 contracts, 4096 live bindings/cached providers
(including pending binding cleanup), 128 typed entries per provider, and 4096
participants per operation. Recovery waits 250 ms; disposal drains the watch,
queries, reports and resource cleanup.

### Method timeout hints

Declare ordinary call timing once in service semantics:

```ts
export const tasks = defineService({
  async run() { return "done"; },
}, { methods: { run: { timeoutSeconds: 2100 } } });
```

`timeoutSeconds` accepts integer `0..2100`; omission or `0` uses the Host default.
Generated calls request the timeout automatically, bounded by the parent invocation
and the Host's effective deadline. Nonzero policies request the existing
`rpc.extended-deadline.v1` capability. Policies are emitted separately from the RPC
descriptor/ABI, in optional contract-entry `methodPolicies` metadata after
`modelProjection`. Empty maps/defaults are omitted, imported policies retain exact
selection evidence, and TS/checkJs use the same rules.

## Errors and extension implementations

### Explicit imported Runtime use

A bound imported provider can declare a targeted Runtime use with
`ServiceOptions.runtimeUses`. Each `ServiceRuntimeUse<TService>` contains a
canonical generated `service: ServiceToken<TService>` and optional `required`
and `actions` policy:

```ts
import { defineService } from "@sunder/sdk";
import { Implementations, Services } from "#sunder/api";

export const holder = defineService({
  handle(request) { return true; },
}, {
  implements: Implementations.holder,
  runtimeUses: [{
    service: Services.Contracts.CExample_Typed_Target.target,
    actions: ["discover"],
  }],
});
```

This same syntax is checked JavaScript. It adds only optional Runtime discovery
(`required: false` by default) for that exact imported contract. It creates no
whole-package API selection or App use. An omitted `actions` uses the contract's
applicable actions; explicitly use `["discover"]` for reference validation only.
Invoking/subscribing to a target still requires those separately declared actions.

Provider-only imports materialize recursive reference types from the **same pinned
artifact inventory**, with no permission inferred from a field. Unrelated cached
types, authored lookalikes, private/platform targets and conflicting declarations
are rejected. The compiler emits ordered `roleUses` evidence with the exact
contract/version/hash/service tuple and canonical shared projection type identity.
Authors never write that evidence, a new lock field, or a handwritten manifest.
Removing explicit discovery again fails normal generation/Update when the
provider's reachable request/output/error graph still needs the reference.

### Domain errors

`defineServiceError<Details>({ description })` declares an ordinary error-detail
type in `errors`. Each method explicitly lists its allowed error codes in
`methods.<name>.errors`. `Errors` from `#sunder/api` exposes generated, validating
error factories. A detail-bearing factory also exposes `.details(error)` for the
typed result prepared by the receiving operation. Natural content/reference details
use the same generated visitors and lifetime rules. Infrastructure failures remain Host-authenticated; undeclared
errors and unexpected exceptions become sanitized provider faults.

After selecting an extension with `api implement` or `platform implement`, use:

```ts
import { defineService } from "@sunder/sdk";
import { Implementations } from "#sunder/api";

export const extension = defineService({
  ping(request, context) { context.signal.throwIfAborted(); return request; },
}, { implements: Implementations.extension });
```

Imported types and authority come from the exact lock/cache. No schemas or consumer
grants are redeclared. A declaration-only extension uses
`defineService<OrdinaryInterface>(null, { declarationOnly: true, exposure: "public" })`;
JS supplies a checked JSDoc `ServiceDefinition<OrdinaryInterface>` annotation.
Local implementations bind that definition through the same `implements` option.
Multiple implementations require distinct stable `providerId` values.

## Lifecycle and package APIs

Export an optional `configure(context)` from the service module, returning
`Omit<WorkerOptions, "providers">`. The generated bootstrap owns providers. Real
`onCandidateStarted`, `onGenerationCommitted`, `onBeforeStorageMigrations`,
`onActivated` and `onShutdown` callbacks retain their staged lifecycle and signals.

`WorkerContext` retains Host-mediated settings, state, files, secrets, structured
logging, callbacks/auth, persistent role-local workspace and exact-generation faults.
`PackageStorageKeyMigration` retains exact, opaque-ID, JSON-identity and bounded
cleanup rules. The pre-migration callback receives read-only state for its exact
generation; normal package data becomes available on activation.

Invocation content registration/opening is authority-bound. Discard opened content
when finished; the Host also revokes it at the invocation/scope terminal boundary.
Worker stdout remains reserved for protocol framing. Use Host logging for events and
bounded `safeStderr` diagnostics. Declare optional APIs in `workerCapabilities`;
package code cannot raise the fixed Worker transport limits.
## Explicit optional service-reference occurrences

References are strict by default, including nullable references. To preserve
expected absence at one record property, use a type-only marker:

```ts
import type { OptionalServiceReference } from "@sunder/sdk";

export type ExecutionContext = {
  executionTarget: OptionalServiceReference<typeof executionService> | null;
};
```

Checked JavaScript uses the same type in JSDoc:

```js
/** @typedef {{executionTarget: import("@sunder/sdk").OptionalServiceReference<typeof executionService> | null}} ExecutionContext */
```

The marker is assignable from an ordinary `ServiceReference`; it creates no
runtime wrapper. It is allowed only on a direct, required nullable record
property, not optional (`?`) properties, nonnullable values, collections or
transparent root values. Generated projection metadata records the owner/member
policy; descriptor/ABI bytes and the eleven-field locator stay unchanged.

Only safe, not-dispatched absence/staleness/unavailability during exact reference
lookup becomes explicit wire `null` or a decoded null. Permission, validation,
protocol, deadline, provider-fault and unsafe/possibly-dispatched errors propagate.
Live optional references keep their exact-bound proxy and retirement signal, but
do not enroll the outer operation as a required participant. A nested target call
still cancels on that target's retirement. Caller, main-callee, required dependency,
deadline and owner cancellation remain authoritative. A strict occurrence of the
same object or complete locator wins, regardless of traversal order. Domain
fallbacks must retain nested-call dispatch/retry metadata; this feature does not
make possibly-dispatched failures safely replayable.

## Replayable content streams (provider-local completion)

Local `SunderContent.fromBytes(bytes)` and `SunderContent.fromStream(source)` use
`application/octet-stream` and the display name `content.bin` when options are
omitted, so default resources satisfy Host registration requirements. Received
content retains its exact metadata; its display name is never treated as a path.

Worker registration metadata and logical content references use distinct timestamp
spellings. Host seven-digit UTC registration timestamps are converted losslessly
to RPC V1 canonical UTC at that boundary, without rounding fractional digits.
Received logical references retain their exact text when opened. Ordinary
`date-time` model fields may use profile-valid offsets; content-reference expiry
must remain canonical UTC.

A concrete provider may set `methods.<name>.completeOnResponseContentRetirement: true`
in `defineService` for a server stream whose response contains `SunderContent`.
This defaults to false and is local to that implementation; it does not change
the descriptor, ABI, consumer method policies, or public projection metadata.

Use this only when callers can resume from their last delivered revision or
explicitly resnapshot. During generated response serialization, a Host-authenticated
content-registration retirement can complete the stream without emitting the failed
item. The four accepted kind/code pairs are `unavailable/rpc.content.authority-revoked`,
`deadline-exceeded/rpc.call.deadline-exceeded`, `stale-endpoint/rpc.endpoint.stale`, and
`cancelled/rpc.call.cancelled`, all with safe, not-dispatched metadata. Caller cancellation,
provider-body errors, source open/read errors, and forged or reused errors still fail.
