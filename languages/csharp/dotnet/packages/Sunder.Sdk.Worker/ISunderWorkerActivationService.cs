using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Worker;

/// <summary>Starts package-owned work after Worker generation commit and before publication readiness.</summary>
/// <remarks>
/// Generated single-project composition discovers concrete source implementations and
/// activates them in constructor dependency order. Candidate preparation does not invoke
/// this callback. Implement <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>
/// for owned cleanup, including candidates that never activate. Types also implementing
/// <see cref="ISunderWorkerLifecycleService"/> are activated once through that interface.
/// </remarks>
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV1)]
public interface ISunderWorkerActivationService
{
    /// <summary>Starts work that must be ready before this Worker advertises publication readiness.</summary>
    ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default);
}
