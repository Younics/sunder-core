# Sunder Package Content Channel

## Purpose

Package Content V1 carries files and other large binary values between an App client and a Sunder Host without embedding bytes in package operation JSON. It is the data plane for package inputs and outputs. JSON operations continue to carry small typed metadata and opaque content references.

The content broker belongs to the stable Host Supervisor so uploads and downloads survive Runtime worker replacement.

## Invariants

1. Binary payloads are never base64-encoded into package operation JSON.
2. Memory usage is bounded by transport buffers and at most one client chunk, not object length.
3. An App path is never sent as a Runtime path.
4. Loopback does not imply a shared filesystem.
5. Every content object is bound to a package and initiating client until adopted.
6. Completed content is immutable and identified by final length and SHA-256.
7. Metadata never records a committed offset beyond durable payload bytes.
8. Cancellation preserves resumability unless cancellation explicitly deletes the session.
9. Completion, cancellation, output publication, and adoption are idempotent.
10. A lost response is recoverable from request ID, content ID, and authoritative Host state.

## Control Reference

Package operation DTOs carry a reference similar to:

```json
{
  "contentId": "01J...",
  "accessGrant": "...",
  "length": 26214400,
  "sha256": "...",
  "fileName": "report.pdf",
  "contentType": "application/pdf"
}
```

The access grant is opaque, package-scoped, purpose-bound, and never placed in a URL. Package code receives a stream from a host service after reference validation.

## Upload Protocol

| Method | Route | Meaning |
| --- | --- | --- |
| `POST` | `/api/v1/packages/{packageId}/content/uploads` | Idempotently create a receiving session |
| `GET`, `HEAD` | `/api/v1/packages/{packageId}/content/uploads/{contentId}` | Read state and authoritative offset |
| `PATCH` | `/api/v1/packages/{packageId}/content/uploads/{contentId}` | Append one sequential chunk |
| `POST` | `/api/v1/packages/{packageId}/content/uploads/{contentId}/complete` | Verify and publish immutable content |
| `DELETE` | `/api/v1/packages/{packageId}/content/uploads/{contentId}` | Explicitly cancel and release content |

Create requests include a stable request ID, normalized file name, content type, optional declared length, required maximum length, and optional expected SHA-256. The request ID is unique within the authenticated client and package. Repeating identical metadata returns the existing session; changing metadata with the same request ID is a conflict.

A chunk request uses:

```text
Content-Type: application/offset+octet-stream
Content-Length: <chunk length>
Upload-Offset: <expected committed offset>
Upload-Checksum: sha256 <base64 digest>
```

The Host returns the new `Upload-Offset`. An offset mismatch returns `409` with the authoritative offset. After an uncertain network outcome, the client probes the offset before retrying.

## Chunking

V1 uses sequential chunks. Recommended policy defaults are 4 MiB chunks and an 8 MiB maximum chunk. Sequential offsets provide restart recovery for known-length files, memory values, seekable streams, reopenable streams, and unknown-length streams with a reserved maximum.

Parallel indexed chunks are deferred. They require a persisted bitmap, out-of-order scratch files, declared total length, final assembly, additional quota accounting, and more complex crash recovery.

## Host Commit Rules

For every chunk the Host:

1. Locks the content record.
2. Validates client, package, state, quota, and exact offset.
3. Streams bytes into the partial payload while hashing and enforcing length.
4. Compares the mandatory chunk checksum.
5. Flushes the appended payload.
6. Atomically advances the metadata offset.

If the process stops after appending bytes but before metadata advances, recovery truncates the payload to the recorded offset.

Completion supplies final length and SHA-256. The Host rehashes the complete payload, writes a finalization intent, atomically publishes an immutable payload, and records the final descriptor. The final pass is required because portable hash internal state cannot be assumed to survive process restarts.

## Downloads

Immutable content supports `HEAD`, full `GET`, and one `bytes=` range in V1. Responses include exact length, SHA-256 ETag, `Accept-Ranges: bytes`, and `Content-Range` when applicable. Transparent compression is disabled because ranges and hashes address stored raw bytes.

The App retains a partial file and a sidecar containing content ID, expected ETag, length, hash, and current offset. Cancellation preserves them. Final publication verifies the complete file before atomic destination replacement.

## States And Leases

Content states are `Receiving`, `Finalizing`, `Available`, `Processing`, `ReservedOutput`, `WritingOutput`, `Adopting`, `Adopted`, `Cancelled`, `Failed`, `Expired`, `Deleting`, and `Deleted`.

Lease classes are:

- Read leases prevent deletion while a stream is open.
- Retention leases preserve content across disconnected clients and resumable downloads.
- Processing leases give a Runtime package exclusive processing ownership.
- Writer leases give a Runtime package exclusive output ownership.

After worker replacement, stale processing leases return their input to `Available`. Incomplete client-reserved outputs return to `ReservedOutput`. Package-created outputs without a recoverable reservation become `Failed`.

## Runtime Access

The Runtime worker validates a reference through private Host IPC and receives an internal read or writer lease. The trusted Runtime host implementation may open the Host-private spool file directly to avoid an additional byte-proxy hop. Package code receives only a `Stream`, content metadata, and a lease-lost cancellation token.

Runtime-generated output is written directly to a Host-reserved partial file and published only after length and hash verification. A still-writing output is not readable in V1; progress remains an NDJSON or SSE concern.

## Adoption

Long-lived content is adopted into `IPackageFileStore`, not retained indefinitely in the transfer broker. Adoption validates the destination as a package-relative path, writes through the package store's atomic stream API, verifies the destination, and then releases transient content unless retention was requested.

Adoption moves content from initiating-client transient ownership into shared package state. Other clients observe it through normal package operations and snapshots.

## Quotas And Retention

Initial configurable defaults:

| Limit | Default |
| --- | ---: |
| Object | 1 GiB |
| Client/package bytes | 2 GiB |
| Package bytes | 4 GiB |
| Global transient bytes | 8 GiB |
| Active uploads per client/package | 16 |
| Available unleased retention | 72 hours |
| Absolute transient lifetime | 7 days |

Known-length uploads reserve their length. Unknown-length streams reserve their declared maximum. Quota is reserved before bytes are accepted. Logical quota failures return `429`; insufficient storage returns `507`.

## Local Optimization

The baseline is raw streaming over authenticated HTTP or local IPC. A future `local-content-bridge.v1` capability may let the App copy a stable snapshot into a Host-owned inbox and register a ticket, length, and hash. It must not register the original user path or infer locality from the URL. Failure to use the bridge falls back to streaming.

## Initial Package Migration

The Agent package is the first acceptance consumer:

1. App attachments upload through the content client.
2. `AgentRunCommand` carries references instead of `byte[]` values.
3. Runtime acquires processing leases and passes streams to provider adapters.
4. Transcript attachments are adopted into Agent package storage.
5. Skills ZIP transfer uses Package Content rather than chunked package files.

The existing 1 MiB request and 4 MiB response operation limits remain unchanged.
