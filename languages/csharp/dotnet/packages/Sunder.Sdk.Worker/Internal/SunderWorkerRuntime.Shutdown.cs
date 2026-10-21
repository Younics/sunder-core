using System.Text.Json;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class SunderWorkerRuntime
{
    private RuntimeStop? _stop;
    private Task? _workCancellation;

    private void BeginShutdown(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "shutdownId", "reason");
        var shutdownId = WorkerJson.RequiredSafeId(root, "shutdownId");
        var reason = WorkerJson.RequiredString(root, "reason", 128);
        RuntimeStop stop;
        bool start;
        lock (Gate)
        {
            if (_phase is RuntimePhase.WaitingForHello or RuntimePhase.Stopped or RuntimePhase.Faulted
                || _stop?.ShutdownId is not null)
            {
                throw new WorkerProtocolException("host.shutdown is duplicate or out of order.");
            }
            start = _stop is null;
            stop = _stop ??= CreateStopUnderGate(reason);
            stop.ShutdownId = shutdownId;
        }
        if (start) _ = RunStopAsync(stop);
    }

    private void StopAfterLifecycleFailure(Exception failure)
    {
        RuntimeStop stop;
        bool start;
        lock (Gate)
        {
            if (_phase is RuntimePhase.Stopped or RuntimePhase.Faulted) return;
            start = _stop is null;
            stop = _stop ??= CreateStopUnderGate("lifecycle-faulted");
            if (stop.TerminalSelected) return;
            stop.Failure = CombineFailures(stop.Failure, failure);
            QueueFaultReportUnderGate(stop);
        }
        if (start) _ = RunStopAsync(stop);
    }

    // Publish the stop and every admitted operation before executing package callbacks.
    // In particular, a failing lifecycle operation must not await its own drain.
    private RuntimeStop CreateStopUnderGate(string reason)
    {
        var activeGeneration = _phase == RuntimePhase.Active ? _sessionGeneration : null;
        _phase = RuntimePhase.Stopping;
        return new RuntimeStop(reason, _limits.ShutdownDrainTimeout,
            _invocations.Values.ToArray(), GetContributionsUnderGate(),
            [_candidateStartOperation, _generationCommitOperation, _migrationPreparationOperation, _activationOperation], activeGeneration);
    }

    private async Task RunStopAsync(RuntimeStop stop)
    {
        // Both cancellation registrations and shutdown callbacks may have a blocking
        // synchronous prefix. Neither may block the protocol reader or the watchdog.
        var cleanup = Task.Run(() => DrainAndDisposeAsync(stop));
        try
        {
            await cleanup.WaitAsync(stop.Deadline.Token).ConfigureAwait(false);
            if (_configuration?.Composition?.Completion.IsCompleted == true)
                await _generationHealthOperation.ConfigureAwait(false);
            Exception? failure;
            string? shutdownId;
            Task report;
            lock (Gate)
            {
                failure = stop.Failure;
                shutdownId = stop.ShutdownId;
                report = stop.FaultReport;
                stop.TerminalSelected = true;
            }
            if (failure is not null)
            {
                await report.WaitAsync(stop.Deadline.Token).ConfigureAwait(false);
                AbortRuntime(failure);
            }
            else if (!stop.Aborted)
            {
                if (shutdownId is not null)
                {
                    var body = WorkerJson.Serialize(_limits, writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "worker.shutdown-ack");
                        writer.WriteString("shutdownId", shutdownId);
                        writer.WriteEndObject();
                    });
                    await _output.WriteAsync(body).AsTask().WaitAsync(stop.Deadline.Token).ConfigureAwait(false);
                }
                lock (Gate)
                {
                    if (!stop.Aborted && stop.Failure is null) _phase = RuntimePhase.Stopped;
                }
                await SignalCancellationAsync(_protocolStop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.Deadline.IsCancellationRequested)
        {
            AbortRuntime(new WorkerProtocolException(
                "Worker shutdown cleanup exceeded its deadline; package resources remain owned until process termination."), cleanupFailure: true);
        }
        catch (OperationCanceledException) when (stop.CallerCancelled)
        {
        }
        catch (Exception exception)
        {
            Task report;
            lock (Gate)
            {
                stop.Failure = CombineFailures(stop.Failure, exception is WorkerProtocolException ? exception
                    : new WorkerProtocolException("The worker shutdown sequence failed.", exception));
                QueueFaultReportUnderGate(stop);
                report = stop.FaultReport;
            }
            // Cleanup failure must not preempt an already queued exact-generation
            // report while its transport and remaining cleanup budget are usable.
            if (!stop.Aborted)
            {
                try { await report.WaitAsync(stop.Deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.Deadline.IsCancellationRequested) { }
            }
            AbortRuntime(cleanupFailure: true);
        }
        finally
        {
            // A timeout never authorizes disposing dependencies under unfinished work.
            // Observe eventual cleanup, but leave that work to the process boundary.
            _ = ObserveStopCleanupAsync(cleanup, stop);
            stop.Completion.TrySetResult();
        }
    }

    private async Task DrainAndDisposeAsync(RuntimeStop stop)
    {
        var cancellation = CancelWorkAsync();
        var inboundCancellation = stop.Invocations.Select(CancelInvocationAsync)
            .Concat(stop.Contributions.Select(CancelContributionAsync)).ToArray();
        var clientDrain = _client.CancelAllForShutdownAsync();
        await Task.WhenAll(stop.Operations.Concat(stop.Invocations.Select(value => value.Operation))
            .Concat(stop.Contributions.Select(value => value.Operation))
            .Concat(inboundCancellation).Append(cancellation)).ConfigureAwait(false);
        try
        {
            await clientDrain.ConfigureAwait(false);
        }
        catch (Exception) when (stop.Aborted)
        {
            // A broken transport cannot acknowledge control cleanup. Admitted package
            // work has still been drained before we release its local dependencies.
        }
        stop.Deadline.Token.ThrowIfCancellationRequested();
        await AwaitClientAbortAsync(stop).ConfigureAwait(false);
        lock (Gate)
        {
            stop.Deadline.Token.ThrowIfCancellationRequested();
            stop.CleanupStarted = true;
        }
        if (_configuration?.OnShutdown is { } shutdown)
        {
            await shutdown(new SunderWorkerShutdownContext(stop.Reason), stop.CleanupCancellation.Token)
                .ConfigureAwait(false);
        }
        lock (Gate) _loggingClosed = true;
        await AwaitClientAbortAsync(stop).ConfigureAwait(false);
        await _client.DrainLoggingForShutdownAsync().ConfigureAwait(false);
    }

    private Task AwaitClientAbortAsync(RuntimeStop stop)
    {
        lock (Gate) return stop.ClientAbort?.Task ?? Task.CompletedTask;
    }

    private void QueueFaultReportUnderGate(RuntimeStop stop)
    {
        if (stop.Aborted || stop.ActiveGeneration is null || _generationFaultReported) return;
        _generationFaultReported = true;
        stop.FaultReport = Task.Run(async () =>
        {
            try
            {
                await _activationOperation.ConfigureAwait(false);
                var body = WorkerJson.Serialize(_limits, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "worker.generation-fault");
                    writer.WriteString("activationId", _environment.ActivationId);
                    writer.WriteNumber("sessionGeneration", stop.ActiveGeneration.Value);
                    writer.WriteString("code", "worker.lifecycle-faulted");
                    writer.WriteString("message", LifecycleFailureDiagnostic(stop.Failure));
                    writer.WriteEndObject();
                });
                await _output.WriteAsync(body).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_protocolStop.IsCancellationRequested) { }
            catch (Exception exception) { Fail(exception); }
        });
    }

    private static string LifecycleFailureDiagnostic(Exception? failure)
    {
        // The Host must be able to distinguish a real lifecycle fault from an
        // unrelated transport/drain symptom. Exception messages/stacks can contain
        // package data or credentials, so report only a small bounded type chain.
        var pending = new Queue<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var types = new List<string>();
        if (failure is not null) pending.Enqueue(failure);
        while (pending.Count > 0 && seen.Count < 8 && types.Count < 4)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current)) continue;
            var name = new string(current.GetType().Name.Take(48)
                .Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
            if (!types.Contains(name, StringComparer.Ordinal)) types.Add(name);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(8 - seen.Count)) pending.Enqueue(inner);
            }
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
        }
        return types.Count == 0 ? "A package-owned worker lifecycle service failed."
            : "A package-owned worker lifecycle service failed (" + string.Join(", ", types) + ").";
    }

    private bool IsLifecycleStoppingUnderGate =>
        _phase == RuntimePhase.Stopping && _stop?.ActiveGeneration is not null && _stop.Failure is not null;

    private static async Task ObserveStopCleanupAsync(Task cleanup, RuntimeStop stop)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception) { /* The stop owner already reported failure or containment. */ }
        finally
        {
            stop.DeadlineCancellation.Dispose();
            stop.CleanupCancellation.Dispose();
            stop.Deadline.Dispose();
        }
    }

    private void AbortRuntime(Exception? failure = null, bool callerCancelled = false, bool cleanupFailure = false)
    {
        RuntimeStop stop;
        bool start;
        bool cancelCleanup;
        bool abortNow;
        lock (Gate)
        {
            if (_phase == RuntimePhase.Stopped) return;
            start = _stop is null;
            stop = _stop ??= CreateStopUnderGate(callerCancelled ? "cancelled" : "faulted");
            abortNow = !stop.Aborted;
            // Once transport abort wins, already-read frames cannot replace its cause.
            // The cleanup owner can still attach a real cleanup failure or deadline.
            if (failure is not null && (abortNow || cleanupFailure)) stop.Failure = CombineFailures(stop.Failure, failure);
            _failure = stop.Failure;
            stop.CallerCancelled |= callerCancelled;
            cancelCleanup = callerCancelled && stop.CleanupStarted;
            stop.Aborted = true;
            if (abortNow) stop.ClientAbort = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_failure is not null) _phase = RuntimePhase.Faulted;
        }
        if (cancelCleanup) _ = SignalCancellationAsync(stop.CleanupCancellation);
        if (!abortNow) return;
        if (_failure is not null) _diagnostics.Write($"{WorkerProtocol.Name} worker failure", _failure);
        _ = CancelWorkAsync();
        foreach (var invocation in stop.Invocations) _ = CancelInvocationAsync(invocation);
        foreach (var contribution in stop.Contributions) _ = CancelContributionAsync(contribution);
        _ = SignalCancellationAsync(_protocolStop);
        // FailAll may release user cancellation registrations; do not run it on the reader.
        var clientAbort = stop.ClientAbort!;
        _ = Task.Run(() =>
        {
            try { _client.FailAll(_failure ?? new OperationCanceledException("The worker run was cancelled.")); }
            catch (Exception exception) { Fail(exception); }
            finally { clientAbort.TrySetResult(); }
        });
        if (start) _ = RunStopAsync(stop);
    }

    private void CancelFromCaller() => AbortRuntime(callerCancelled: true);

    private Task CompleteRuntimeAsync()
    {
        lock (Gate)
        {
            if (_stop is not null) return _stop.Completion.Task;
        }
        AbortRuntime();
        return _stop!.Completion.Task;
    }

    private static Exception CombineFailures(Exception? previous, Exception next)
        => previous is null || ReferenceEquals(previous, next) ? next
            : new WorkerProtocolException("Worker failure and cleanup both failed.", new AggregateException(previous, next));

    private bool IsStopping()
    {
        lock (Gate) return _phase is RuntimePhase.Stopping or RuntimePhase.Stopped or RuntimePhase.Faulted;
    }

    private void ThrowIfFaultedUnderGate()
    {
        if (_failure is not null) throw _failure;
    }

    private Task CancelInvocationAsync(InboundInvocation invocation)
    {
        lock (Gate) return invocation.CancellationSealed
            ? invocation.CancellationOperation ?? Task.CompletedTask
            : invocation.CancellationOperation ??= SignalCancellationAsync(invocation.Cancellation);
    }

    private Task CancelWorkAsync()
    {
        lock (Gate) return _workCancellation ??= SignalCancellationAsync(_workStop);
    }

    private void CancelInvocation(InboundInvocation invocation) => _ = CancelInvocationAsync(invocation);

    private async Task SignalCancellationAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException exception) { _diagnostics.Write("Package cancellation callback failed", exception); }
        catch (ObjectDisposedException) { }
    }

    private sealed class RuntimeStop
    {
        public RuntimeStop(string reason, TimeSpan timeout, InboundInvocation[] invocations,
            InboundContribution[] contributions, Task[] operations, long? activeGeneration)
        {
            Reason = reason;
            Invocations = invocations;
            Contributions = contributions;
            Operations = operations;
            ActiveGeneration = activeGeneration;
            Deadline = new(timeout);
            // CancelAsync signals immediately without running package registrations on
            // the deadline timer, where a blocking callback could suppress the watchdog.
            DeadlineCancellation = Deadline.Token.UnsafeRegister(
                static state => _ = CancelCleanupAsync((CancellationTokenSource)state!), CleanupCancellation);
        }

        private static async Task CancelCleanupAsync(CancellationTokenSource source)
        {
            try { await source.CancelAsync().ConfigureAwait(false); }
            catch (AggregateException) { }
            catch (ObjectDisposedException) { }
        }

        public string Reason { get; }
        public InboundInvocation[] Invocations { get; }
        public InboundContribution[] Contributions { get; }
        public Task[] Operations { get; }
        public long? ActiveGeneration { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ClientAbort { get; set; }
        public Task FaultReport { get; set; } = Task.CompletedTask;
        public CancellationTokenSource Deadline { get; }
        public CancellationTokenRegistration DeadlineCancellation { get; }
        public CancellationTokenSource CleanupCancellation { get; } = new();
        public string? ShutdownId { get; set; }
        public Exception? Failure { get; set; }
        public volatile bool Aborted;
        public volatile bool CallerCancelled;
        public bool CleanupStarted { get; set; }
        public bool TerminalSelected { get; set; }
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
