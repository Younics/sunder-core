using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient : ISunderRpcClient
{
    private readonly SunderWorkerRuntime _runtime;
    private readonly WorkerProtocolOutput _output;
    private readonly WorkerEnvironment _environment;
    private readonly WorkerLimits _limits;
    private readonly Dictionary<string, OutboundCall> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerRpcCallScope> _scopes = new(StringComparer.Ordinal);
    private long _nextId;
    private int _pendingScopeOpens;
    private int _closingScopes;
    private int _openContentHandles;

    public WorkerRpcClient(
        SunderWorkerRuntime runtime,
        WorkerProtocolOutput output,
        WorkerEnvironment environment,
        WorkerLimits limits)
    {
        _runtime = runtime;
        _output = output;
        _environment = environment;
        _limits = limits;
    }

    internal WorkerEnvironment Environment => _environment;

    public SunderRpcResourceProfile? ResourceProfile { get; internal set; }

    internal ValueTask ReportGenerationFaultAsync(
        string code,
        string message,
        CancellationToken cancellationToken)
        => _runtime.ReportGenerationFaultAsync(code, message, cancellationToken);
}

internal sealed class WorkerResourceReservation(Action release)
{
    private Action? _release = release;

    public void Release() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
