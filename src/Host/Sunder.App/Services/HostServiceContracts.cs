using System.Diagnostics;

namespace Sunder.App.Services;

internal interface IHostServiceManager
{
    Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken);

    Task<HostServiceObservation> ObserveAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken);

    Task<bool> TryStopAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
        => Task.FromResult(false);
}

internal interface IHostServiceReplacementFailure;

internal sealed class HostServiceReplacementException(Exception innerException)
    : InvalidOperationException(
        "The previous Sunder Host service was displaced before its replacement launch failed.",
        innerException),
        IHostServiceReplacementFailure;

internal sealed class HostServiceReplacementCanceledException(OperationCanceledException innerException)
    : OperationCanceledException(
        "The previous Sunder Host service was displaced before its replacement launch was cancelled.",
        innerException,
        innerException.CancellationToken),
        IHostServiceReplacementFailure;

internal static class HostServiceReplacementFailure
{
    public static Exception Wrap(Exception exception) => exception switch
    {
        IHostServiceReplacementFailure => exception,
        OperationCanceledException cancellation => new HostServiceReplacementCanceledException(cancellation),
        _ => new HostServiceReplacementException(exception),
    };
}

internal enum HostServiceState
{
    Absent,
    Starting,
    Running,
    Stopped,
    Failed,
    Unknown,
}

internal sealed record HostServiceLaunchReceipt
{
    public HostServiceLaunchReceipt(
        string backend,
        string serviceName,
        int? processId = null,
        string? instanceId = null)
    {
        Backend = HostServiceDiagnostics.SanitizeLabel(backend, "unknown");
        ServiceName = HostServiceDiagnostics.SanitizeLabel(serviceName, "unknown");
        ProcessId = processId is > 0 ? processId : null;
        InstanceId = HostServiceDiagnostics.SanitizeOptionalLabel(instanceId);
    }

    public string Backend { get; }

    public string ServiceName { get; }

    public int? ProcessId { get; }

    public string? InstanceId { get; }
}

internal sealed record HostServiceObservation
{
    public HostServiceObservation(
        HostServiceState state,
        int? processId = null,
        int? exitCode = null,
        string? result = null,
        string? safeDetail = null,
        IEnumerable<string>? diagnosticPaths = null)
    {
        State = state;
        ProcessId = processId is > 0 ? processId : null;
        ExitCode = exitCode;
        Result = HostServiceDiagnostics.SanitizeOptionalLabel(result);
        SafeDetail = HostServiceDiagnostics.SanitizeDetail(safeDetail);
        DiagnosticPaths = HostServiceDiagnostics.SanitizePaths(diagnosticPaths);
    }

    public HostServiceState State { get; }

    public int? ProcessId { get; }

    public int? ExitCode { get; }

    public string? Result { get; }

    public string? SafeDetail { get; }

    public IReadOnlyList<string> DiagnosticPaths { get; }

    public bool IsTerminal => State is HostServiceState.Absent
        or HostServiceState.Stopped
        or HostServiceState.Failed;

    public static HostServiceObservation Unknown(int? processId = null)
        => new(HostServiceState.Unknown, processId);
}

internal enum HostServiceStartupFailure
{
    TerminalState,
    ObservationError,
}

internal sealed class HostServiceStartupException : InvalidOperationException
{
    private HostServiceStartupException(
        string message,
        HostServiceStartupFailure failure,
        HostServiceLaunchReceipt receipt,
        HostServiceObservation? observation)
        : base(message)
    {
        Failure = failure;
        Receipt = receipt;
        Observation = observation;
    }

    public HostServiceStartupFailure Failure { get; }

    public HostServiceLaunchReceipt Receipt { get; }

    public HostServiceObservation? Observation { get; }

    public static HostServiceStartupException FromTerminalObservation(
        HostServiceLaunchReceipt receipt,
        HostServiceObservation observation)
        => new(
            BuildTerminalMessage(receipt, observation),
            HostServiceStartupFailure.TerminalState,
            receipt,
            observation);

    public static HostServiceStartupException FromObservationError(
        HostServiceLaunchReceipt receipt,
        Exception exception)
    {
        var detail = HostServiceDiagnostics.SanitizeDetail(exception.Message);
        var message = $"Sunder could not observe the launched Host service ({DescribeReceipt(receipt)}).";
        if (detail is not null)
        {
            message += $" {detail}";
        }

        return new HostServiceStartupException(
            message,
            HostServiceStartupFailure.ObservationError,
            receipt,
            observation: null);
    }

    private static string BuildTerminalMessage(
        HostServiceLaunchReceipt receipt,
        HostServiceObservation observation)
    {
        var message = $"The launched Sunder Host service reached terminal state '{observation.State}' before its Runtime connection became ready ({DescribeReceipt(receipt, observation.ProcessId)}).";
        if (observation.ExitCode is { } exitCode)
        {
            message += $" Exit code: {exitCode}.";
        }
        if (observation.Result is not null)
        {
            message += $" Result: {observation.Result}.";
        }
        if (observation.SafeDetail is not null)
        {
            message += $" {observation.SafeDetail}";
        }
        if (observation.DiagnosticPaths.Count > 0)
        {
            message += $" Diagnostics: {string.Join(", ", observation.DiagnosticPaths.Select(static path => $"'{path}'"))}.";
        }
        return message;
    }

    internal static string DescribeReceipt(
        HostServiceLaunchReceipt receipt,
        int? observedProcessId = null)
        => $"backend={receipt.Backend}, service={receipt.ServiceName}, pid={(observedProcessId ?? receipt.ProcessId)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}";
}
