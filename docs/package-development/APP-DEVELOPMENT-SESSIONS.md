# App development sessions and execution observations

App view models can request these host-owned services from generated constructor
composition in `Sunder.Sdk.Abstractions`:

```csharp
public interface IPackageDevelopmentService
{
    Task<IPackageDevelopmentSession> LoadAsync(
        string developmentFolder, bool watch, CancellationToken cancellationToken = default);
}

public interface IPackageDevelopmentSession : IAsyncDisposable
{
    string PackageId { get; }
    Task<PackageDevelopmentStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task SetWatchAsync(bool watch, CancellationToken cancellationToken = default);
}

public sealed record PackageDevelopmentStatus(
    string PackageId, bool IsLoaded, bool WatchEnabled, string Readiness,
    string? ErrorMessage, bool OverridesInstalledPackage)
{
    public bool RequestedWatch { get; init; }
    public string AppReadiness { get; init; } = "Pending";
    public PackageDevelopmentRevision? RuntimeRevision { get; init; }
    public PackageDevelopmentRevision? AppliedRevision { get; init; }
}

public interface IPackageLogNavigationService
{
    Task OpenPackageLogsAsync(string packageId, CancellationToken cancellationToken = default);
}
```

The inferred capabilities are `package-development.v1` and
`package-log-navigation.v1`. Both are App-only; Worker constructor composition and
the Host's Worker V1 compatibility profile reject them. Public SDK contracts have
no dependency on App or Host implementations. Package registrations cannot replace
these services.

The optional revision getters additionally infer
`package-development-revisions.v1`; using the original development service or
status members alone does not require it. Execution observations below infer
`worker-execution-observations.v1`. These two additions are currently for managed
.NET App targets only, not Worker V1 or Node/web targets. Both a supporting Host
and a supporting App implementation are required: the Host checks the authenticated
App's implemented capabilities before granting the exact target's load permit.
An older App request without that metadata cannot load a target requiring them.
Targets which do not use the additions retain their existing permit request shape.
Legacy requests which omit the exact App target cannot select around a new
observation requirement elsewhere in the same manifest. Managed retention requires
the real tracked caller; a null-caller fallback is not eligible for these additions.

## Loading and ownership

Call `LoadAsync` from a published App activation, normally a user action. Pass the
canonical **built development folder**, on the connected Host's filesystem. The
Host validates its manifest and content index, exact-RID target and import
evidence before deriving runtime requirements. Missing imports produce guidance
for `sunder dev package prepare-imports <folder>` using explicit local
`--candidate`/`--platform-catalog` inputs or an explicit `--registry`. There is no
inferred Registry fallback.

Missing .NET/Node runtimes use the existing runtime confirmation window,
source-bound `PackageInstallationApproval`, installation queue and official
runtime installer. Preparation pins the folder's content and import evidence,
revalidates after approval and after runtime installation, and revalidates again
before owner acquisition. The development project is neither packed nor uploaded
nor installed as an archive by this flow.

Each App package activation owns one Client `DevPackageOwnerSession` per normalized
folder. Repeated loads of that folder return the same live SDK session and update
its watch request. Sessions for different folders have independent owner leases.
Disposal releases only that owner's request; existing Host co-ownership and
installed-package fallback remain authoritative. The shared Client performs
heartbeats, ambiguous-mutation recovery, and reacquisition after a Host restart.

Caller cancellation during acquisition releases a possibly committed owner with
an independent cleanup budget. Activation revocation cancels in-flight work and
disposes its sessions; provider disposal awaits that cleanup. A session cannot be
used after disposal or after its activation has retired.

## Activation retention across development changes

The App retains unchanged managed Avalonia activations across presentation graph
changes. Loading, rebuilding, and unloading an unrelated development project keeps
the development tool's provider, cached views, background work, publication token,
and projection lease alive. Each package has its own publication and background-work
owner, so overriding another installed package and restoring its installed fallback
does not retire the development tool. Web activations use normal replacement.

The Host supplies an instance-bound activation fingerprint covering the actual
source kind/path/content, installed origin/policy identity, manifest and acquired
targets, dependency/import closure, optional presence, and effective provider
readiness. Unrelated projects do not alter that fingerprint. Changed content,
installed-to-development overrides, changed dependency/import readiness, a Host
replacement, quarantine, and explicit retry prevent reuse. Missing fingerprints
also prevent reuse. Current process load policy is checked again before retention.
Same-generation readiness changes with a changed fingerprint run the lifecycle
path rather than merely updating labels.

Candidate presentation borrows the existing activation; rejected candidates release
their references without revoking it. The last presentation reference retires the
actual activation and disposes its development sessions. Ownership is never moved
to a newly constructed package provider or resurrected after revocation.

Retained managed RPC clients open a fresh, validated Host App session for the new
Runtime generation. New calls use it after commit. Existing call scopes and streams
keep their original inner client and fail stale/retired rather than silently
retargeting another session.

## Runtime Worker continuity

Development-owner acquisition/release and targeted development reload retain a
healthy Worker when its validated source, effective dependency/import closure,
readiness, exact execution-runtime bytes/binding, and approved sensitive
capabilities agree. Retention is applied after validating the candidate and before
starting its replacement Workers. Explicit same-byte reload of that package still
replaces its Worker, as do changed source/runtime/grant identities. Dependency
replacements invalidate dependent retention too.

A retained Worker keeps its activation ID, activation epoch, process, in-memory
jobs, workspace, and original immutable Worker V1 activation-generation stamp.
The current Runtime graph has a separate publication generation. Fresh RPC
catalog entries belong to that current publication, with new exact endpoint
references; old call scopes/endpoints still retire. Host package-data access checks
the exact live Worker activation and its birth stamp in the current session.
The actual owner's retirement stops and disposes that Worker. No job cache is
copied or persisted across a replacement to simulate continuity.

Snapshot-directory ownership follows the retained package source. Discarded
candidates cannot dispose borrowed Workers, successful publication transfers
cleanup ownership atomically, and rollback preserves the unchanged Workers while
recovering only those that were quiesced.

## Generated bindings after an App transport reopen

A successful independent catalog query on the reopened managed transport waits
for the receiver's single observer to resubscribe and resnapshot. A cached catalog
failure from the previous transport does not permanently retire the injected
generated gateway. No service invocation is replayed. Previously issued exact
provider references and retained call scopes remain bound to their original
lifetimes and cannot be revived by a new successful query.

## Status

`GetStatusAsync` confirms ownership through the existing Host owner protocol and
reads the current Host package snapshot. `Readiness` is prefixed with `Runtime:`
(for example `Runtime:Ready`, `Runtime:Failed`, or `Runtime:Reconnecting`). It does
not assert App view activation or presentation readiness. `AppReadiness` separately
reports `Pending`, `Activated`, `Quarantined`, `Unavailable`, or `NotApplicable`.
`Activated` requires the matching Host stamp and current activation fingerprint to have been published by App; it does
not assert that every lazily created view has been opened. `ErrorMessage` carries
the observed Host package failure. `IsLoaded` means the owner and current Host
snapshot agree, including for a loaded package that is degraded or failed.
`RequestedWatch` describes this owner's request. `WatchEnabled` is the observed
Host aggregate, including other owners that keep the same folder watched. A Host
that does not report the aggregate cannot produce a truthful status and must be
updated. `OverridesInstalledPackage` is true when that observed dev
overlay has an installed package underneath it.

### Applied output revisions

`PackageDevelopmentRevision` contains the observing `RuntimeInstanceId`, sampled
`SessionGeneration`, and canonical full-package `SourceContentIdentity` SHA-256.
The identity comes from Host-validated content, not an authored descriptor, source
timestamp, selected folder or App-role-only hash.

`RuntimeRevision` reports the sampled loaded source. `AppliedRevision` additionally
requires that source to be enabled and Runtime-ready (including degraded operation)
and, when the package has an App target, that its matching source, Runtime stamp
and activation fingerprint have been published by App. A Worker-only change may
retain the managed App object while publishing its new source/caller observation.
This does not mean lazy views have been opened, a browser DOM is ready, or a
business RPC has succeeded; those are separate application-specific checks.

Null revision metadata is unknown, including from an older Host. Missing applied
evidence is not success. A source edit, running/successful build, effective output
watch and observed applied revision are different events. Compare Runtime identity
and full content identity for output changes: an unrelated generation increment
does not by itself mean a new output, and a replacement Runtime is a new origin.

## Exact Worker execution cleanup

Inject `IPackageWorkerExecutionObservations` into an App root when it must preserve
exact cleanup ownership across a lost Worker/job response. From a fresh generated
service catalog, select the exact current provider in the same package and call
`CaptureAsync(provider, token)` **before** the sole work-start attempt. Retain that
provider for dispatch; capturing does not authorize following its replacement or
replaying a possibly dispatched start.

Capture authenticates the complete current provider descriptor and joins its
actual process Worker to the Host-owned execution reservation. It neither invokes
the service nor starts, stops, restarts or retains the Worker. A provider identifier
or package/backend/job identifier by itself is not an execution reference.

Later, `ObserveAsync(reference, token)` reads evidence for the original execution,
even when its provider has retired. The currently published, authorized same-package
App may query retained history; a revoked activation cannot borrow a successor's
authority. The reference's `RuntimeInstanceId` identifies the original owning Host,
not where to route the request. A connected successor Host can read the original
durable evidence, but a reference is not a bearer permission.

| State | Meaning |
| --- | --- |
| `Unknown` | The Host cannot establish the reference's cleanup evidence. Missing data is not proof of exit. |
| `CleanupPending` | The exact execution's complete cleanup is not confirmed. |
| `CleanupConfirmed` | Matching durable positive containment-cleanup evidence exists and the old live lease is absent. |

Neither root-process exit, catalog disappearance, job-not-found, elapsed time nor
replacement readiness confirms cleanup. A receipt is retained before the owning
lease is removed; failed or indeterminate receipt publication retains ownership.
`ConfirmedAtUtc`, when present, is the Host confirmation time, not job completion
or the caller's observation time.

**Cleanup confirmation never supplies a package job outcome.** If the last response
was lost, success/cancellation/failure can remain unknown after cleanup is proven.
Persist unresolved job-admission/exclusion ownership before dispatch if it must
survive App recreation; this observation service is not a package job journal.

Retention is bounded with backpressure and no eviction of outstanding references:
currently 4,096 executions overall and 256 per package, with one idempotent slot per
exact execution rather than per capture or job. Capacity exhaustion rejects new
capture, not old evidence. Permission/security denials, invalid requests and corrupt
evidence remain terminal errors, not successful cleanup or reconnect instructions.

### No-dispatch evidence for the exact Start attempt

On the exception returned by that exact generated unary service operation,
`SunderRpcException.HasTrustedNoDispatchEvidence(error)` can establish that its RPC
service handler was not dispatched. This member requires the same managed App-only
`worker-execution-observations.v1` capability. Existing exception members and
ordinary generated clients do not acquire this additional requirement.

The predicate recognizes SDK-owned pre-Invoke checks and validated explicit Host
metadata from the actual App Invoke response. A later metadata query rejected as
NotDispatched does **not** describe an earlier successful Start: downstream
metadata/decode/disposal failures retain dispatch uncertainty. Public error flags,
messages, Domain errors, arbitrary inner exceptions and cancellation alone are
not this proof. SDK cancellation translation preserves an existing proof and the
original caller token; external wrappers cannot create it.

False supplies no no-dispatch evidence. Some failures before the SDK-owned call
scope exists remain unproven, even if no request was sent. True concerns only that
operation's RPC handler, not external callbacks, and never authorizes Start replay
or establishes a job outcome. Preserve the original admission until there is
trusted no-Start evidence, an exact terminal job result or exact Host cleanup.

Legacy neutral error DTO defaults and serialization remain unchanged; omitted or
duplicate wire flags simply cannot authenticate this App proof. Genuine Worker V1
transport decisions retain their existing metadata without becoming App
observation authority.

## Logs

`OpenPackageLogsAsync` is explicit user navigation. It enables the existing
developer log capture and Runtime log stream in an ordinary App session, opens the
shared developer log window, clears its prior search, and selects the exact package
filter. An empty package filter persists through replay, refresh, trimming and
Runtime log reset. This is navigation only; the SDK exposes no log-reader API.
