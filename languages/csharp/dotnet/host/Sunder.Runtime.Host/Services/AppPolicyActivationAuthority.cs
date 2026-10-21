using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

/// <summary>One actual App activation, shared by all of its exact permit/transport bindings.</summary>
internal sealed class AppPolicyActivationAuthority
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _retirement = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _retiring;
    private Task? _retirementTask;
    private Exception? _cleanupFailure;

    internal CancellationToken RetirementToken => _retirement.Token;
    internal bool IsActive { get { lock (_gate) return !_retiring; } }

    internal IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_retiring) throw SunderRpcException.Infrastructure(SunderRpcErrorKind.Unavailable,
                "rpc.app-activation.retired", "The App activation is retiring or retired.");
            _operations++;
            return new Operation(this);
        }
    }

    internal Task RetireAsync()
    {
        lock (_gate)
        {
            if (_retirementTask is not null) return _retirementTask;
            _retiring = true;
            CompleteDrain();
            return _retirementTask = Task.WhenAll(RuntimeCancellation.Signal(_retirement), _drained.Task);
        }
    }

    internal void RecordCleanupFailure(Exception failure)
    {
        lock (_gate) _cleanupFailure ??= failure;
    }

    private void CompleteDrain()
    {
        if (!_retiring || _operations != 0) return;
        if (_cleanupFailure is null) _drained.TrySetResult();
        else _drained.TrySetException(new RuntimeUnavailableException("App activation resource cleanup could not be proven."));
    }

    private void Release()
    {
        lock (_gate)
        {
            _operations--;
            CompleteDrain();
        }
    }

    private sealed class Operation(AppPolicyActivationAuthority owner) : IDisposable
    {
        private AppPolicyActivationAuthority? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
