using System.Diagnostics.CodeAnalysis;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides activation-scoped references for safely invoking host-owned extension contributions.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionInvocationsV1)]
public interface IPackageExtensionInvocationCatalog
{
    /// <summary>Gets opaque references in deterministic host order without exposing contribution instances.</summary>
    IReadOnlyList<IPackageExtensionReference<TContract>> GetExtensionReferences<TContract>(
        PackageExtensionPoint<TContract> extensionPoint);

    /// <summary>Attempts to report an invariant failure against the exact active owner represented by <paramref name="reference"/>.</summary>
    /// <remarks>
    /// Hosts may reserve this operation for trusted extension orchestrators. Expected operation failures and normal cancellation must be
    /// handled locally. A successful report applies the host's package-fault policy.
    /// </remarks>
    /// <param name="reference">An opaque reference obtained from the host invocation catalog.</param>
    /// <param name="exception">The unexpected extension invariant failure.</param>
    /// <returns>
    /// <see langword="true"/> when the active owner accepted the fault report; <see langword="false"/> when the reference is foreign,
    /// stale, or already retiring.
    /// </returns>
    bool TryReportInvariantViolation<TContract>(
        IPackageExtensionReference<TContract> reference,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(exception);
        return false;
    }
}

/// <summary>Identifies one contribution from one exact owning-package activation.</summary>
/// <typeparam name="TContract">The extension contract type.</typeparam>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionInvocationsV1)]
public interface IPackageExtensionReference<TContract>
{
    /// <summary>Attempts to acquire an invocation lease for this exact activation.</summary>
    /// <param name="lease">The acquired lease, or <see langword="null"/> when owner retirement has started.</param>
    /// <returns><see langword="true"/> when the lease was acquired.</returns>
    bool TryAcquire([NotNullWhen(true)] out IPackageExtensionLease<TContract>? lease);
}

/// <summary>Temporarily exposes a contribution while preventing disposal of its owning activation.</summary>
/// <typeparam name="TContract">The extension contract type.</typeparam>
/// <remarks>Dispose the lease promptly. All properties throw after disposal.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ExtensionInvocationsV1)]
public interface IPackageExtensionLease<out TContract> : IDisposable
{
    /// <summary>Gets the canonical id of the package that owns this activation.</summary>
    string PackageId { get; }

    /// <summary>Gets the host-owned contribution for the lifetime of this lease.</summary>
    TContract Contribution { get; }

    /// <summary>Gets a token canceled when retirement of this exact owner activation starts.</summary>
    CancellationToken RetirementToken { get; }
}
