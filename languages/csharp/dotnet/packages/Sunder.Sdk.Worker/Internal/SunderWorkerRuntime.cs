using System.Text.Json;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class SunderWorkerRuntime
{
    private readonly Func<WorkerRpcClient, WorkerRuntimeConfiguration> _configure;
    private readonly WorkerEnvironment _environment;
    private readonly WorkerLimits _limits;
    private readonly WorkerDiagnosticSink _diagnostics;
    private readonly WorkerFrameReader _reader;
    private readonly WorkerProtocolOutput _output;
    private readonly WorkerRpcClient _client;
    private IReadOnlyDictionary<string, SunderWorkerProviderRegistration> _providers =
        new Dictionary<string, SunderWorkerProviderRegistration>(StringComparer.Ordinal);
    private WorkerProviderIdentity[] _providerIdentities = [];
    private WorkerRuntimeConfiguration? _configuration;
    private readonly Dictionary<string, InboundInvocation> _invocations = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _currentInvocationId = new();
    private readonly HashSet<string> _rememberedHostIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedHostIdOrder = new();
    private readonly CancellationTokenSource _protocolStop = new();
    private readonly CancellationTokenSource _workStop = new();
    private Task _activationOperation = Task.CompletedTask;
    private Task _candidateStartOperation = Task.CompletedTask;
    private Task _generationCommitOperation = Task.CompletedTask;
    private Task _migrationPreparationOperation = Task.CompletedTask;
    private Task _generationHealthOperation = Task.CompletedTask;
    private RuntimePhase _phase;
    private long? _sessionGeneration;
    private bool _generationCommitAcknowledgementPending;
    private bool _generationFaultReported;
    private bool _loggingClosed;
    private Exception? _failure;

    public SunderWorkerRuntime(
        Func<WorkerRpcClient, WorkerRuntimeConfiguration> configure,
        Stream input,
        Stream output,
        WorkerDiagnosticSink diagnostics,
        WorkerEnvironment environment,
        WorkerLimits limits)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configure = configure;
        _environment = environment;
        _limits = limits;
        _diagnostics = diagnostics;
        _reader = new WorkerFrameReader(input, limits);
        _output = new WorkerProtocolOutput(output, limits, _protocolStop.Token);
        _client = new WorkerRpcClient(this, _output, environment, limits);
    }

    internal object Gate { get; } = new();

    internal string? CurrentInvocationId => _currentInvocationId.Value;
    internal CancellationToken StoppingToken => _protocolStop.Token;
    internal bool IsActiveUnderGate => _phase is RuntimePhase.Activating or RuntimePhase.Active;
    internal bool CanReadStateForMigrationPreparationUnderGate =>
        _phase == RuntimePhase.PreparingStorageMigrations;
    internal bool CanSendControlUnderGate => _stop?.Aborted != true && _phase is
        RuntimePhase.PreparingStorageMigrations
        or RuntimePhase.Activating
        or RuntimePhase.Active
        or RuntimePhase.Stopping;
    internal bool CanSendLoggingUnderGate => !_loggingClosed && _stop?.Aborted != true && _phase is
        RuntimePhase.Ready
        or RuntimePhase.CandidateStarting
        or RuntimePhase.CandidateStarted
        or RuntimePhase.Committing
        or RuntimePhase.Committed
        or RuntimePhase.PreparingStorageMigrations
        or RuntimePhase.StorageMigrationsPrepared
        or RuntimePhase.Activating
        or RuntimePhase.Active
        or RuntimePhase.Stopping;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => ((SunderWorkerRuntime)state!).CancelFromCaller(),
            this);
        try
        {
            while (true)
            {
                using var document = await _reader.ReadAsync(_protocolStop.Token).AsTask()
                    .WaitAsync(_protocolStop.Token).ConfigureAwait(false);
                if (await HandleEnvelopeAsync(document.RootElement).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException) when (_protocolStop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var failure = exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("Sunder worker protocol processing failed.", exception);
            Fail(failure);
        }
        finally
        {
            await CompleteRuntimeAsync().ConfigureAwait(false);
        }
        if (_failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
        if (_phase == RuntimePhase.Stopped) return;
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(_protocolStop.Token);
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        AbortRuntime(exception);
    }

    private async ValueTask<bool> HandleEnvelopeAsync(JsonElement root)
    {
        var type = WorkerJson.ReadEnvelopeType(root);
        lock (Gate)
        {
            if (_phase == RuntimePhase.WaitingForHello
                && !string.Equals(type, "host.hello", StringComparison.Ordinal))
            {
                throw new WorkerProtocolException($"Host sent '{type}' before the required host.hello envelope.");
            }
        }

        switch (type)
        {
            case "host.hello":
                await HandleHelloAsync(root).ConfigureAwait(false);
                return false;
            case "host.activate":
                await HandleActivateAsync(root).ConfigureAwait(false);
                return false;
            case "host.start-candidate":
                BeginStartCandidate(root);
                return false;
            case "host.commit-generation":
                BeginCommitGeneration(root);
                return false;
            case "host.prepare-storage-migrations":
                BeginStorageMigrationPreparation(root);
                return false;
            case "host.invoke":
                BeginInvocation(root);
                return false;
            case "host.callback-start":
            case "host.callback-complete":
            case "host.callback-cancel":
            case "host.auth-status":
            case "host.auth-connect":
            case "host.auth-complete":
            case "host.auth-disconnect":
                BeginContribution(type, root);
                return false;
            case "host.contribution-cancel":
                HandleContributionCancellation(root);
                return false;
            case "host.cancel":
                HandleInvocationCancellation(root);
                return false;
            case "host.result":
            case "host.event":
            case "host.complete":
            case "host.error":
                _client.HandleResponse(type, root);
                return false;
            case "host.shutdown":
                BeginShutdown(root);
                return false;
            default:
                throw new WorkerProtocolException($"Unknown Host envelope type '{type}'.");
        }
    }
}
