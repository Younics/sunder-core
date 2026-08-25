using System.Text.Json;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class SunderWorkerRuntime
{
    private void BeginShutdown(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "shutdownId", "reason");
        var shutdownId = WorkerJson.RequiredSafeId(root, "shutdownId");
        var reason = WorkerJson.RequiredString(root, "reason", 128);
        InboundInvocation[] invocations;
        InboundContribution[] contributions;
        lock (Gate)
        {
            var canStop = _phase is RuntimePhase.Ready
                or RuntimePhase.CandidateStarting
                or RuntimePhase.CandidateStarted
                or RuntimePhase.Committing
                or RuntimePhase.Committed
                or RuntimePhase.PreparingStorageMigrations
                or RuntimePhase.StorageMigrationsPrepared
                or RuntimePhase.Activating
                or RuntimePhase.Active;
            if (!canStop)
            {
                throw new WorkerProtocolException("host.shutdown is duplicate or out of order.");
            }
            _phase = RuntimePhase.Stopping;
            invocations = _invocations.Values.ToArray();
            contributions = GetContributionsUnderGate();
        }

        CancelSource(_workStop);
        foreach (var invocation in invocations) CancelInvocation(invocation);
        CancelContributions(contributions);
        var clientDrain = _client.CancelAllForShutdownAsync();
        _shutdownOperation = CompleteShutdownAsync(
            shutdownId,
            reason,
            invocations,
            contributions,
            clientDrain);
    }

    private async Task CompleteShutdownAsync(
        string shutdownId,
        string reason,
        IReadOnlyList<InboundInvocation> invocations,
        IReadOnlyList<InboundContribution> contributions,
        Task clientDrain)
    {
        try
        {
            await Task.WhenAll(invocations.Select(static invocation => invocation.Operation)).ConfigureAwait(false);
            await Task.WhenAll(contributions.Select(static contribution => contribution.Operation)).ConfigureAwait(false);
            await _candidateStartOperation.ConfigureAwait(false);
            await _generationCommitOperation.ConfigureAwait(false);
            await _migrationPreparationOperation.ConfigureAwait(false);
            await _activationOperation.ConfigureAwait(false);
            ThrowIfFaulted();
            await clientDrain.ConfigureAwait(false);

            if (Configuration.OnShutdown is not null)
            {
                await Configuration.OnShutdown(
                    new SunderWorkerShutdownContext(reason),
                    _protocolStop.Token).ConfigureAwait(false);
            }
            lock (Gate)
            {
                _loggingClosed = true;
            }
            await _client.DrainLoggingForShutdownAsync().ConfigureAwait(false);

            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.shutdown-ack");
                writer.WriteString("shutdownId", shutdownId);
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
            lock (Gate)
            {
                ThrowIfFaultedUnderGate();
                _phase = RuntimePhase.Stopped;
            }
            CancelSource(_protocolStop);
        }
        catch (OperationCanceledException) when (_protocolStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("The worker shutdown sequence failed.", exception));
        }
    }

    private void CancelFromCaller()
    {
        InboundInvocation[] invocations;
        InboundContribution[] contributions;
        lock (Gate)
        {
            if (_phase is RuntimePhase.Stopped or RuntimePhase.Faulted) return;
            _phase = RuntimePhase.Stopping;
            invocations = _invocations.Values.ToArray();
            contributions = GetContributionsUnderGate();
        }
        CancelSource(_workStop);
        foreach (var invocation in invocations) CancelInvocation(invocation);
        CancelContributions(contributions);
        _client.FailAll(new OperationCanceledException("The worker run was cancelled."));
        CancelSource(_protocolStop);
    }

    private void CompleteRuntime()
    {
        CancelSource(_workStop);
        InboundInvocation[] invocations;
        InboundContribution[] contributions;
        lock (Gate)
        {
            invocations = _invocations.Values.ToArray();
            contributions = GetContributionsUnderGate();
        }
        foreach (var invocation in invocations) CancelInvocation(invocation);
        CancelContributions(contributions);
        _client.FailAll(_failure ?? new OperationCanceledException("The worker runtime has stopped."));
        CancelSource(_protocolStop);
    }

    private bool IsStopping()
    {
        lock (Gate)
        {
            return _phase is RuntimePhase.Stopping or RuntimePhase.Stopped or RuntimePhase.Faulted;
        }
    }

    private void ThrowIfFaulted()
    {
        lock (Gate)
        {
            ThrowIfFaultedUnderGate();
        }
    }

    private void ThrowIfFaultedUnderGate()
    {
        if (_failure is not null) throw _failure;
    }

    private void CancelInvocation(InboundInvocation invocation)
    {
        try
        {
            invocation.Cancellation.Cancel();
        }
        catch (AggregateException exception)
        {
            _diagnostics.Write("Provider cancellation callback failed", exception);
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race with an advisory cancellation.
        }
    }

    private static void CancelSource(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
        }
    }

    private enum RuntimePhase
    {
        WaitingForHello,
        Ready,
        CandidateStarting,
        CandidateStarted,
        Committing,
        Committed,
        PreparingStorageMigrations,
        StorageMigrationsPrepared,
        Activating,
        Active,
        Stopping,
        Stopped,
        Faulted,
    }
}
