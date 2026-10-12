# Browser Bridge V1: SDK Binding Handoff

The native App owns this document-lifetime contract. The Node SDK/React adapter
owns typed declarations, readiness waiting, generated clients and React-root leases.
The protocol remains exactly `sunder.bridge.v1`, version `1`; there are no aliases,
fallback bridges, Registry configuration or new permission grants.

## Ready Contract

After successful origin/native-callback validation, the App installs the immutable
`window.sunder` object, including these fields:

```text
protocol: "sunder.bridge.v1"
version: 1
signal: AbortSignal             # this document/bridge lifetime, not a call signal
resourceProfile, rpc, openExternal, getNavigationState, navigate  # existing APIs
```

It then synchronously dispatches exactly one window `CustomEvent("sunder:ready")`
whose `detail` is that same API object. Repeating the same live bootstrap preserves
object identity and emits no second event. A retired bootstrap cannot become ready
again in the same document. The API object is the opaque generation identity; SDK
code does not receive or invent a native nonce or endpoint.

SDK readiness must register its window listener **before** checking
`window.sunder`, then use the same validation path for the event and the immediate
check. Require exact protocol/version, `candidate === window.sunder`, and a live
`candidate.signal`. Remove listeners when settled, caller-aborted or page-hidden.
For late registration, the immediate check supplies the already-installed object.
An existing incompatible/retired object fails explicitly rather than waiting for
replacement. No polling, sleeps or application-owned startup retries are needed.
The event is a wake-up notification, not authorization: native admission continues
to validate the exact origin, nonce, role, manifest, grants and activation.

The SDK's adapted API must retain the **same signal**. Link it to each root/operation
lifetime. Root/component cleanup aborts only its own lease, never the document
signal or the activation's shared native RPC client. Different native views have
different bridge lifetimes even when they share an activation-owned caller session.

## Retirement And Cancellation

Native navigation/disposal sends a nonce-fenced lifecycle envelope through the
existing `window.__sunderReceive` callback:

```json
{"protocol":"sunder.bridge.v1","version":1,"nonce":"<native-owned>","requestId":null,"type":"retired","value":null,"error":null}
```

The document also retires on `pagehide` (including a persisted page). Retirement is
idempotent: it rejects pending requests/iterators, removes call timers/listeners and
queued events, then aborts `api.signal`. Its reason is a `SunderRpcError` with
`kind: "unavailable"`, `code: "rpc.bridge.retired"`. Pending dispatched operations
retain conservative `possibly-dispatched`/`unsafe` metadata. Later calls fail
`not-dispatched`/`safe` without allocating request state or invoking native code.
Old-nonce retirement/results cannot affect another document. Native cancellation
does not depend on successful delivery into a document that is already disappearing.

A signal already aborted at call entry yields `kind: "cancelled"`,
`code: "rpc.bridge.cancelled"`, `not-dispatched`/`safe`, before serialization,
request-ID allocation, timers, abort listeners, pending state or native dispatch.
For streams the returned rejected iterable reports this failure when iterated and
retains no native call. Generated high-level stream laziness remains SDK-owned.

No browser content API or automatic content-scope closure is added here. Existing
native/Worker content authority must not be revoked merely because a unary method
returned a content reference.
