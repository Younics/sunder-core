# Package runtimes in Sunder

**Settings → Package Runtimes** manages the .NET Runtime 10 and Node.js 24 runtimes
used by packages. Downloads come from Host-configured official vendor sources.
These runtimes are separate from Agent Builder's development SDKs and Sunder app updates.
Runtime Registry-source selection and `.sunderruntime` import are not part of this model.

## One installation review, ordinary queued jobs

Installing from the Marketplace, a local `.sunderpkg`, or a Stack uses one combined
review of package changes, permissions, and required runtime downloads. Nothing is
queued until that review is approved. Cancelling it creates no jobs.

After approval, the existing SDK background queue runs:

1. **Runtime download/install** first, only when a required runtime is missing.
2. **Package download/install** second, visibly **Queued — waiting for runtime** until
   the first job finishes. A queued package delegate consumes no execution slot.

Healthy runtime reuse queues only the package job, with a fresh Host readiness check
before mutation. No runtime-install job is created for healthy reuse.

When both jobs are needed they use the same `SequentialWithinGroup` installation group.
The complete pair is enrolled before either delegate can run. Runtime failure or cancellation cancels
the installation's remaining queued package work; it does not cancel unrelated operations.
Failures remain failed jobs with their actual error. There is no second routine package
confirmation after the runtime download.

Before mutation, Sunder re-previews the original source and checks fresh runtime readiness.
New preview handles, timestamps and cache/download availability do not ask for approval
again. A changed package version, artifact, runtime identity, source or permission tuple
stops the queued installation and asks the user to review it again.

Batch/Stack package changes remain one atomic Host transaction. Queue metadata identifies
the group and its package members; rows do not cause separate per-package commits. The
Stack wizard awaits completion outside the queue, including with a single execution slot.

## Local package uploads

The reviewed immutable local archive is uploaded and retained before confirmation.
Its typed UploadId/lease/hash/length proof is attached to both preview and final staging.
Retention belongs to the whole installation: preparation, review, queued wait, runtime
download and package completion. Renewal occurs every ten minutes under the Host's
30-minute renewable window and absolute two-hour bound.

Cancellation releases retention even if the package delegate never starts. Successful
consumption is followed by idempotent best-effort release; a lost cleanup acknowledgement
does not undo a committed package. Expired, conflicted or previous-Host proofs cannot be
revived: select the original package again for a new upload and review.

## Runtime management

The page has two stable cards: **.NET** and **Node.js**. Each shows whether the runtime
is installed, needs repair, has an update, or is waiting to apply a downloaded update.
Actions select the latest Host-supported LTS release compatible with package requirements
(currently .NET 10 and Node 24). Installing another major version is not an automatic
upgrade of a package's declared requirement. Exact versions are secondary information;
there is no paginated release catalogue on this page.

Cards update in place from Host runtime/session events and background-job progress.
Reconnection refreshes local status, including after a missed event. The page does not
poll every few seconds or disable its controls for a background status read. Vendor
discovery runs when the page opens or **Check for updates** is selected; Host automatic
update checks continue independently. Offline discovery preserves installed status and
shows that the latest release could not be checked, rather than claiming **Up to date**.

Details expose installed bytes, health, support status, retention, device RID, vendor
checksum and source evidence. Unknown download size is shown as unknown, never as zero.
Install uses an exact Host preview and retains the runtime until removal.
Manual runtime installation uses the existing background queue and continues when
Settings is closed; cancellation is available in Background Processes.

Check, Repair and Remove remain Host-authoritative. Remove is disabled while usage
prevents it and rechecks current impact before applying. Automatic runtime updates
default ON in Host settings; writes use revision CAS and preserve the last known setting
on rejection. They are separate from Sunder self-updates and package/platform security
refresh. Manual updates remain available when automatic updates are OFF.

Prepared/deferred updates do not claim activation. Safe application waits for affected
packages to stop. **Restart & Apply** confirms the fresh full-generation impact and any
App restart requirement. `StoreCommitted`, `RuntimeSessionApplied` and the committed
stamp distinguish saved bindings, pending reconciliation and actual application.

## Integration scope

The shared API is described in [Official runtime downloads](OFFICIAL-RUNTIME-HANDOFF.md).
App queue and confirmation behavior is exercised with typed fixtures, including one-slot
ordering, fast failure, queued cancellation, retained-upload cleanup and atomic batches.
Shared-client HTTP coverage verifies authentication and retained-upload proof through the
runtime-first queue and final package staging. This does not yet qualify an end-to-end
App queued installation against actual official-runtime Host handlers. That integration
remains open; the earlier signed-runtime journey does not qualify this replacement model.
