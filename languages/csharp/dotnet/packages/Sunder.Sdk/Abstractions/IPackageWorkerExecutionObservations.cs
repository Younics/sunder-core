using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Services;

namespace Sunder.Sdk.Abstractions;

/// <summary>Observes Host-owned cleanup evidence for exact worker executions. Available to published App activations only.</summary>
/// <remarks>
/// Capturing or observing a reference does not start, stop, restart, retain, or grant access to a worker.
/// The Host authorizes every operation. A reference is not a permission or a package-job completion receipt.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.WorkerExecutionObservationsV1)]
public interface IPackageWorkerExecutionObservations
{
    /// <summary>Captures the worker execution implementing this exact, current service provider.</summary>
    /// <remarks>Capture before dispatching work. A retired or replaced provider cannot select its successor.</remarks>
    Task<PackageWorkerExecutionReference> CaptureAsync<TService>(
        ServiceProvider<TService> provider,
        CancellationToken cancellationToken = default) where TService : class;

    /// <summary>Reads cleanup evidence for the originally captured execution, without requiring its provider to remain active.</summary>
    /// <remarks>
    /// The currently authorized same-package App may observe retained historical evidence, including after a Host restart.
    /// The reference's RuntimeInstanceId identifies the original Host; it is not a transport-routing instruction.
    /// Unknown or pending evidence never confirms cleanup. Cleanup does not assert that a package job succeeded.
    /// </remarks>
    Task<PackageWorkerExecutionObservation> ObserveAsync(
        PackageWorkerExecutionReference reference,
        CancellationToken cancellationToken = default);
}

/// <summary>A Host-issued locator for one exact worker execution, not a bearer permission.</summary>
[SunderSdkCapability(SunderSdkCapabilities.WorkerExecutionObservationsV1)]
public sealed record PackageWorkerExecutionReference(Guid RuntimeInstanceId, string Value)
{
    /// <summary>The Host instance which originally owned the execution, not necessarily the currently connected Host.</summary>
    public Guid RuntimeInstanceId { get; } = RuntimeInstanceId != Guid.Empty
        ? RuntimeInstanceId : throw new ArgumentException("A worker execution reference requires its original Host identity.", nameof(RuntimeInstanceId));

    /// <summary>The bounded, opaque Host-issued reference value. Constructing a value does not authorize it.</summary>
    public string Value { get; } = !string.IsNullOrWhiteSpace(Value) && Value.Length <= 256
        ? Value : throw new ArgumentException("A worker execution reference must be non-empty and at most 256 characters.", nameof(Value));
}

/// <summary>The Host's cleanup evidence for the original execution, not the lifecycle of a replacement provider.</summary>
[SunderSdkCapability(SunderSdkCapabilities.WorkerExecutionObservationsV1)]
public enum PackageWorkerExecutionState
{
    /// <summary>The Host cannot establish cleanup for this reference. Absence is not proof of exit.</summary>
    Unknown = 0,
    /// <summary>The original execution has not yet confirmed complete cleanup.</summary>
    CleanupPending = 1,
    /// <summary>The Host has confirmed cleanup of the original execution and its owned process resources.</summary>
    CleanupConfirmed = 2,
}

/// <summary>An observation of Host-owned cleanup evidence. This does not acknowledge package-owned business results.</summary>
[SunderSdkCapability(SunderSdkCapabilities.WorkerExecutionObservationsV1)]
public sealed record PackageWorkerExecutionObservation(
    PackageWorkerExecutionReference Reference,
    PackageWorkerExecutionState State)
{
    private DateTimeOffset? _confirmedAtUtc;

    /// <summary>The exact original execution reference represented by this observation.</summary>
    public PackageWorkerExecutionReference Reference { get; } = Reference
        ?? throw new ArgumentNullException(nameof(Reference));

    /// <summary>The observed cleanup evidence. Unknown and CleanupPending are not successful cleanup.</summary>
    public PackageWorkerExecutionState State { get; } = Enum.IsDefined(State)
        ? State : throw new ArgumentOutOfRangeException(nameof(State));

    /// <summary>The Host's confirmation time, when retained. This is not the App's observation time or a job-completion time.</summary>
    public DateTimeOffset? ConfirmedAtUtc
    {
        get => _confirmedAtUtc;
        init
        {
            if (value is not null && State != PackageWorkerExecutionState.CleanupConfirmed)
                throw new ArgumentException("Only confirmed cleanup may carry a confirmation time.", nameof(value));
            _confirmedAtUtc = value?.ToUniversalTime();
        }
    }
}
