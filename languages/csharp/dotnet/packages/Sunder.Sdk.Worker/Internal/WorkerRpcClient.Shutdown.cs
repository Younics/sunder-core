namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient
{
    internal async Task CancelAllForShutdownAsync()
    {
        OutboundCall[] calls;
        WorkerRpcCallScope[] scopes;
        lock (_runtime.Gate)
        {
            scopes = _scopes.Values.ToArray();
            calls = _calls.Values
                .Where(static call => !call.Control && !call.Logging && !call.Terminal && !call.Cancelled)
                .ToArray();
        }
        foreach (var call in calls)
        {
            Cancel(call, new CancellationToken(canceled: true));
        }
        var scopeCleanup = scopes.Select(static scope => scope.RevokeFromRuntime()).ToArray();
        Task[] terminals;
        lock (_runtime.Gate)
        {
            terminals = _calls.Values
                .Select(static call => call.TerminalCompletion.Task)
                .ToArray();
        }
        await Task.WhenAll(terminals.Concat(scopeCleanup)).ConfigureAwait(false);

        WorkerPayloadHandle[] payloads;
        lock (_runtime.Gate)
        {
            payloads = _payloadHandles.Values.ToArray();
            _payloadHandles.Clear();
            foreach (var payload in payloads) RememberPayloadHandleUnderGate(payload.Id);
        }
        await Task.WhenAll(payloads.Select(ReleasePayloadForShutdownAsync)).ConfigureAwait(false);
    }

    internal Task DrainLoggingForShutdownAsync()
    {
        lock (_runtime.Gate)
        {
            return Task.WhenAll(_calls.Values
                .Where(static call => call.Logging && !call.Terminal)
                .Select(static call => call.TerminalCompletion.Task));
        }
    }

    internal void FailAll(Exception exception)
    {
        OutboundCall[] calls;
        WorkerRpcCallScope[] scopes;
        lock (_runtime.Gate)
        {
            scopes = _scopes.Values.ToArray();
            _scopes.Clear();
            calls = _calls.Values.ToArray();
            _calls.Clear();
            foreach (var call in calls) call.Terminal = true;
        }
        foreach (var scope in scopes) _ = scope.RevokeFromRuntime();
        foreach (var call in calls)
        {
            call.CancellationRegistration.Dispose();
            call.ReleaseTerminalResource();
            call.TerminalCompletion.TrySetResult();
            call.UnaryCompletion.TrySetException(exception);
            call.Events.Fail(exception);
        }
    }
}
