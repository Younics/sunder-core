using System.Net;
using System.Runtime.ExceptionServices;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class DevPackageOwnerSession
{
    private ReloadOperation? _reload;
    private bool HasPendingReload => _reload is { Result: null, Failure: null };

    public async Task<DevPackageOwnerReloadResult> ReloadStagedAsync(string operationId, string packageId, string sourceContentIdentity,
        Func<PackageLifecycleStageResult, CancellationToken, Task> retireActivations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retireActivations);
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 || string.IsNullOrWhiteSpace(packageId)
            || sourceContentIdentity is not { Length: 64 } || !sourceContentIdentity.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Explicit reload requires an operation id, package id and canonical source content identity.");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = new CancellationTokenSource(_options.ReloadTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var entered = false;
        try
        {
            await _mutationGate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
            if (_pendingRelease is not null || _releaseCommitPending)
                throw new InvalidOperationException("Resolve the exact pending owner release before reloading.");
            if (_reload?.Request.MutationId != operationId)
            {
                if (HasPendingReload) throw new InvalidOperationException("Resolve the previous reload operation before starting another one.");
                DevPackageOwnerLeaseResponse? lease;
                lock (_syncRoot) lease = _lease;
                if (lease is null || _pendingMutation is not null || !lease.Packages.Any(package => package.PackageId == packageId)
                    || GetDesiredFolders()?.Any(folder => folder.Watch) != false)
                    throw new InvalidOperationException("Explicit reload requires an acknowledged non-watching development owner.");
                _reload = new(new(lease.RuntimeInstanceId, OwnerToken, lease.Revision, operationId, packageId, sourceContentIdentity));
            }
            var reload = _reload!;
            if (reload.Request.PackageId != packageId || reload.Request.SourceContentIdentity != sourceContentIdentity)
                throw new InvalidOperationException("A reload operation id cannot be reused for different output.");
            if (reload.Failure is not null) ExceptionDispatchInfo.Capture(reload.Failure).Throw();
            if (reload.Result is not null) return reload.Result;
            using var client = _clients();
            try
            {
                // Never reset/reacquire this binding during a mutation, including after
                // an ambiguous reply. A replacement Runtime cannot resolve the old stage.
                var handshake = await client.GetRuntimeHandshakeAsync(operation.Token).ConfigureAwait(false);
                if (handshake.RuntimeInstanceId != reload.Request.RuntimeInstanceId)
                    throw new InvalidOperationException("The reload belongs to the previous Runtime. Its outcome is unknown; do not replay it.");
                reload.Phase = "preparing";
                reload.Prepared ??= await ResolveAsync(() => client.StageDevPackageOwnerReloadAsync(OwnerId, reload.Request, operation.Token)).ConfigureAwait(false);
                var prepared = reload.Prepared;
                var stage = prepared.Stage;
                if (!stage.Success || stage.StageId is null || stage.BaseStamp?.RuntimeInstanceId != reload.Request.RuntimeInstanceId
                    || prepared.PackageId != packageId || prepared.SourceContentIdentity != sourceContentIdentity)
                    throw new InvalidDataException("Runtime returned a mismatched development reload stage.");
                reload.Phase = "retiring";
                if (reload.Retirement is null || reload.Retirement.IsCanceled || reload.Retirement.IsFaulted)
                    reload.Retirement = retireActivations(stage, operation.Token);
                await reload.Retirement.WaitAsync(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                reload.CommitStarted = true;
                reload.Phase = "committing";
                var result = await ResolveAsync(() => client.CommitDevPackageOwnerReloadAsync(OwnerId, CommitRequest(reload), operation.Token)).ConfigureAwait(false);
                var current = result.Snapshot.SessionPackages.SingleOrDefault(package => package.PackageId == packageId);
                if (result.RuntimeInstanceId != reload.Request.RuntimeInstanceId || result.OwnerId != OwnerId || result.StageId != stage.StageId
                    || result.PackageId != packageId || result.SourceContentIdentity != sourceContentIdentity
                    || result.Snapshot.RuntimeInstanceId != result.RuntimeInstanceId || current?.SourceContentIdentity != sourceContentIdentity
                    || result.Snapshot.SessionGeneration <= stage.BaseStamp!.SessionGeneration)
                    throw new InvalidDataException("Runtime returned a mismatched development reload acknowledgement.");
                reload.Result = result;
                return result;

                async Task<T> ResolveAsync<T>(Func<Task<T>> resolve)
                {
                    while (true)
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        try { return await resolve().ConfigureAwait(false); }
                        catch (Exception error) when (error is HttpRequestException
                            or RuntimeClientException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway
                                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout })
                        {
                            // Only this originally admitted reload resolves its exact
                            // idempotent stage/commit. Status readers never do this,
                            // and no new build, owner, output or retirement is selected.
                            reload.LastTransportFailure ??= error;
                            await Task.Delay(TimeSpan.FromMilliseconds(50), operation.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                if (error is RuntimeClientException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.Unauthorized })
                    reload.Failure = error; // Definite rejection; never silently stage another revision under this id.
                else if (!reload.CommitStarted && reload.Prepared is not null && reload.Retirement is not { IsCompleted: false })
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(_options.ReleaseTimeout);
                        await client.DiscardDevPackageOwnerReloadAsync(OwnerId, CommitRequest(reload), cleanup.Token).ConfigureAwait(false);
                        reload.Failure = error;
                    }
                    catch (Exception cleanupError) { ReportFault("reload-discard", cleanupError); }
                }
                throw;
            }
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The explicit development reload exceeded its bounded {_reload?.Phase ?? "admission"} budget. Observe this original operation before another mutation.",
                _reload?.LastTransportFailure ?? error);
        }
        finally { if (entered) _mutationGate.Release(); }
    }

    private static DevPackageOwnerReloadCommitRequest CommitRequest(ReloadOperation reload)
        => new(reload.Request.RuntimeInstanceId, reload.Request.OwnerToken, reload.Prepared!.Stage.StageId!);

    private sealed class ReloadOperation(DevPackageOwnerReloadStageRequest request)
    {
        internal DevPackageOwnerReloadStageRequest Request { get; } = request;
        internal DevPackageOwnerReloadStageResult? Prepared { get; set; }
        internal Task? Retirement { get; set; }
        internal bool CommitStarted { get; set; }
        internal DevPackageOwnerReloadResult? Result { get; set; }
        internal Exception? Failure { get; set; }
        internal Exception? LastTransportFailure { get; set; }
        internal string Phase { get; set; } = "preparing";
    }
}
