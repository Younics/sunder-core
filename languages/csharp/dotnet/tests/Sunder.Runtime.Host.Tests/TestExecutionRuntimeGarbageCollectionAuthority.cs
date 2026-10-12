using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Tests;

internal sealed class TestExecutionRuntimeGarbageCollectionAuthority : IExecutionRuntimeGarbageCollectionAuthority
{
    private readonly object _sync = new();
    private List<InstalledPackageExecutionRuntimeBindingRecord> _bindings = [];
    private List<ExecutionRuntimeRetention> _retentions = [];

    public bool ThrowOnExecute { get; set; }

    public void SetBindings(params InstalledPackageExecutionRuntimeBindingRecord[] bindings)
    {
        lock (_sync)
        {
            _bindings = bindings.ToList();
        }
    }

    public void SetRetentions(params ExecutionRuntimeRetention[] retentions)
    {
        lock (_sync)
        {
            _retentions = retentions.ToList();
        }
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<ExecutionRuntimeGarbageCollectionAuthoritySnapshot, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnExecute)
        {
            throw new IOException("Injected execution-runtime garbage collection authority failure.");
        }

        ExecutionRuntimeGarbageCollectionAuthoritySnapshot snapshot;
        lock (_sync)
        {
            snapshot = new ExecutionRuntimeGarbageCollectionAuthoritySnapshot(_bindings, _retentions);
        }
        return await operation(snapshot, cancellationToken).ConfigureAwait(false);
    }
}
