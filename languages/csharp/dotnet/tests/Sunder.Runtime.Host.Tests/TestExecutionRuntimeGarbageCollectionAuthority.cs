using Sunder.Runtime.Host.Services;
using Sunder.Registry.Contracts;

namespace Sunder.Runtime.Host.Tests;

internal sealed class TestExecutionRuntimeGarbageCollectionAuthority
    : IExecutionRuntimeGarbageCollectionAuthority
{
    private readonly object _sync = new();
    private InstalledPackageExecutionRuntimeBindingRecord[] _bindings = [];
    private ExecutionRuntimeLifecyclePolicySnapshot _lifecyclePolicies =
        new(0, []);
    private Exception? _failure;

    public Action? Executing { get; set; }

    public void SetBindings(params InstalledPackageExecutionRuntimeBindingRecord[] bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        lock (_sync)
        {
            _bindings = bindings.ToArray();
        }
    }

    public void FailWith(Exception? failure)
    {
        lock (_sync)
        {
            _failure = failure;
        }
    }

    public void SetLifecycle(
        string state,
        params ExecutionRuntimeReceiptV1[] receipts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentNullException.ThrowIfNull(receipts);
        var supportedAt = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        var lifecycle = state switch
        {
            ExecutionRuntimeLifecycleStates.Supported => new RegistryExecutionRuntimeLifecycle(
                state, supportedAt, supportedAt, null, null, null, null, null),
            ExecutionRuntimeLifecycleStates.Deprecated => new RegistryExecutionRuntimeLifecycle(
                state, supportedAt, supportedAt, supportedAt.AddHours(1), null, null, null, null),
            ExecutionRuntimeLifecycleStates.Retired => new RegistryExecutionRuntimeLifecycle(
                state,
                supportedAt,
                supportedAt,
                supportedAt.AddHours(1),
                supportedAt.AddHours(2),
                supportedAt.AddHours(2),
                null,
                null),
            ExecutionRuntimeLifecycleStates.Revoked => new RegistryExecutionRuntimeLifecycle(
                state,
                supportedAt,
                supportedAt,
                supportedAt.AddHours(1),
                supportedAt.AddHours(2),
                supportedAt.AddHours(2),
                supportedAt.AddHours(3),
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        var entries = receipts.Select(receipt => new ExecutionRuntimeLifecyclePolicyEntryV1(
            receipt.Identity,
            receipt.RegistryOrigin ?? "https://registry.example/",
            receipt.WorkerProtocol,
            receipt.ManifestSha256,
            receipt.ArtifactSha256,
            receipt.ArtifactSize,
            receipt.ContentIdentity,
            receipt.SigningKeyId,
            MinimumHostTrustGeneration: 1,
            receipt.NativeAttestationSha256,
            receipt.NativeAttestationKeyId,
            lifecycle,
            RuntimePolicyRevision: 1,
            PolicyObservedAtUtc: supportedAt.AddHours(4)));
        lock (_sync)
        {
            _lifecyclePolicies = new ExecutionRuntimeLifecyclePolicySnapshot(1, entries);
        }
    }

    public Task<TResult> ExecuteAsync<TResult>(
        Func<ExecutionRuntimeGarbageCollectionAuthoritySnapshot, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        InstalledPackageExecutionRuntimeBindingRecord[] bindings;
        ExecutionRuntimeLifecyclePolicySnapshot lifecyclePolicies;
        Exception? failure;
        lock (_sync)
        {
            bindings = _bindings.ToArray();
            lifecyclePolicies = _lifecyclePolicies;
            failure = _failure;
        }
        if (failure is not null)
        {
            return Task.FromException<TResult>(failure);
        }
        Executing?.Invoke();
        return operation(
            new ExecutionRuntimeGarbageCollectionAuthoritySnapshot(bindings, lifecyclePolicies),
            cancellationToken);
    }
}
