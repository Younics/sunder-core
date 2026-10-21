using System.Diagnostics;
using System.Globalization;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.App.Services;

internal sealed partial class AppProcessPolicySupervisor
{
    private readonly object _diagnosticGate = new();
    private string _heartbeatPhase = "not-started";
    private DateTimeOffset? _phaseStartedUtc;
    private string _loadPhase = "idle";
    private long? _packageGeneration;
    private DateTimeOffset? _lastHeartbeatSuccessUtc;
    private DateTimeOffset? _lastFailureUtc;
    private string? _lastFailureType;
    private string? _lastFailurePhase;
    private int? _lastFailureHttpStatus;
    private RuntimePolicyTransportFailure? _lastTransportFailure;
    private long _consecutiveFailures;
    private long? _lastProgressReport;
    private Guid? _observedRuntimeInstance;
    private string? _observedSession;
    private string _observedState = "none";
    private string _observedCode = "none";

    private void SetHeartbeatPhase(string phase)
    {
        lock (_diagnosticGate)
        {
            _heartbeatPhase = phase;
            _phaseStartedUtc = _time.GetUtcNow();
        }
    }

    private void SetLoadPhase(string phase, long generation)
    {
        lock (_diagnosticGate)
        {
            _loadPhase = phase;
            _packageGeneration = generation;
        }
    }

    private void ObserveRuntimeIdentity(Guid runtimeInstance)
    {
        lock (_diagnosticGate) _observedRuntimeInstance = runtimeInstance;
    }

    private void ObserveSessionStatus(RuntimeAppPolicySessionStatus status)
    {
        lock (_diagnosticGate)
        {
            _observedSession = SafeSessionId(status.SessionId);
            _observedState = status.State switch
            {
                RuntimeAppPolicySessionStates.Active or RuntimeAppPolicySessionStates.ShutdownRequested
                    or RuntimeAppPolicySessionStates.TerminationRequired or RuntimeAppPolicySessionStates.ExitProven
                    or RuntimeAppPolicySessionStates.Closed => status.State,
                _ => "invalid",
            };
            _observedCode = status.ErrorCode switch
            {
                null => "none",
                PackagePolicyErrorCodes.AppProcessTerminationRequired or PackagePolicyErrorCodes.AppProcessSupervisionRequired => status.ErrorCode,
                _ => "other",
            };
        }
    }

    private void RecordHeartbeatSuccess()
    {
        bool recovered;
        bool report;
        lock (_diagnosticGate)
        {
            recovered = _consecutiveFailures > 0;
            _consecutiveFailures = 0;
            _lastHeartbeatSuccessUtc = _time.GetUtcNow();
            report = recovered || ShouldReportProgress();
            if (report) _lastProgressReport = Stopwatch.GetTimestamp();
        }
        if (report) WriteDiagnostic(recovered ? "heartbeat-recovered" : "heartbeat-healthy");
    }

    private void RecordHeartbeatFailure(Exception error, string? phase = null, string eventName = "heartbeat-failed")
    {
        bool report;
        lock (_diagnosticGate)
        {
            _lastFailureUtc = _time.GetUtcNow();
            _lastFailureType = error.GetType().Name;
            _lastTransportFailure = RuntimePolicyTransportFailure.From(error);
            _lastFailurePhase = phase ?? $"{_heartbeatPhase}/{_connection.DiagnosticPhase ?? "connection"}";
            _lastFailureHttpStatus = error is RuntimeClientException runtime ? (int)runtime.StatusCode
                : error is HttpRequestException { StatusCode: { } status } ? (int)status : null;
            _consecutiveFailures++;
            report = _consecutiveFailures == 1 || ShouldReportProgress();
            if (report) _lastProgressReport = Stopwatch.GetTimestamp();
        }
        // No exception message, URL, response body, bearer token or remote error
        // text enters the diagnostic. Repeated failures emit at most every 30 s.
        if (report) WriteDiagnostic(eventName);
    }

    private bool ShouldReportProgress()
        => _lastProgressReport is null || Stopwatch.GetElapsedTime(_lastProgressReport.Value) >= TimeSpan.FromSeconds(30);

    private void WriteDiagnostic(string eventName) => TryWriteDiagnostic(DescribeSupervision(eventName));

    private void TryWriteDiagnostic(string message)
    {
        try { _writeDiagnostic(message); }
        catch (Exception) { /* Diagnostics cannot prevent supervision or shutdown. */ }
    }

    private string DescribeSupervision(string eventName)
    {
        RuntimeContinuousClockReading? proof;
        RuntimeContinuousClockReading? now = null;
        lock (_proofGate) proof = _lastProof;
        try { now = _clock(); } catch (Exception) { }
        var proofAgeMs = proof is not null && now is not null && now.SameEpoch(proof)
            && now.Ticks >= proof.Ticks && now.Frequency > 0
                ? ((double)((Int128)now.Ticks - proof.Ticks) * 1000 / now.Frequency).ToString("0.###", CultureInfo.InvariantCulture)
                : "unknown";
        lock (_diagnosticGate)
        {
            var message = FormattableString.Invariant(
                $"App policy supervision event={eventName}; process={Environment.ProcessId}; policy session={SafeSessionId(_session?.SessionId)}; App generation={_session?.AppGenerationId}; Runtime instance={_session?.RuntimeInstanceId}; observedRuntime={_observedRuntimeInstance}; observedSession={_observedSession ?? "none"}; state={_observedState}; code={_observedCode}; phase={_heartbeatPhase}/{_connection.DiagnosticPhase ?? "connection"}; phaseStartedUtc={_phaseStartedUtc:O}; loadPhase={_loadPhase}; packageGeneration={_packageGeneration}; lastSuccessUtc={_lastHeartbeatSuccessUtc:O}; lastFailureUtc={_lastFailureUtc:O}; lastFailurePhase={_lastFailurePhase ?? "none"}; lastFailureType={_lastFailureType ?? "none"}; lastFailureHttpStatus={_lastFailureHttpStatus}; transportPhase={_lastTransportFailure?.Phase}; transportKind={_lastTransportFailure?.Transport}; socketError={_lastTransportFailure?.SocketError}; nativeSocketError={_lastTransportFailure?.NativeError}; exchangeElapsedMs={_lastTransportFailure?.ElapsedMilliseconds:0.###}; consecutiveFailures={_consecutiveFailures}; proofAgeMs={proofAgeMs}; shutdownRequested={Volatile.Read(ref _shutdownRequested)}; threadPoolThreads={ThreadPool.ThreadCount}; pendingThreadPoolWork={ThreadPool.PendingWorkItemCount}.");
            return message.Length <= 2048 ? message : message[..2048];
        }
    }

    private static string SafeSessionId(string? session)
        => session is null ? "none" : Guid.TryParseExact(session, "N", out _) ? session : "invalid";
}
