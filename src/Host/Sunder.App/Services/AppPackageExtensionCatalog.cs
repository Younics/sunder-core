using Sunder.Runtime.Contracts;
using Sunder.Package.Hosting;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageExtensionCatalog : OwnerAwarePackageExtensionCatalog
{
    private const string AuthorizedOrchestratorPackageId = "sunder.package.agent";
    private Action<PackageExtensionOwnerToken, string, PackageFailureOrigin, Exception?>? _reportFault;

    internal void ConfigureFaultReporting(
        Action<PackageExtensionOwnerToken, string, PackageFailureOrigin, Exception?> reportFault)
    {
        ArgumentNullException.ThrowIfNull(reportFault);
        if (Interlocked.CompareExchange(ref _reportFault, reportFault, null) is not null)
        {
            throw new InvalidOperationException("App package extension fault reporting is already configured.");
        }
    }

    internal IPackageExtensionInvocationCatalog CreateInvocationCatalog(string callerPackageId)
        => new ScopedInvocationCatalog(this, callerPackageId);

    internal bool TryReportOwnerInvariantViolation<TContract>(
        IPackageExtensionReference<TContract> reference,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var reportFault = Volatile.Read(ref _reportFault);
        if (reportFault is null || !TryResolveActiveOwner(reference, out var ownerToken))
        {
            return false;
        }

        reportFault(
            ownerToken,
            "A package extension invocation failed.",
            PackageFailureOrigin.AppUnhandledUi,
            exception);
        return true;
    }

    private sealed class ScopedInvocationCatalog(
        AppPackageExtensionCatalog catalog,
        string callerPackageId) : IPackageExtensionInvocationCatalog
    {
        public IReadOnlyList<IPackageExtensionReference<TContract>> GetExtensionReferences<TContract>(
            PackageExtensionPoint<TContract> extensionPoint)
            => catalog.GetExtensionReferences(extensionPoint);

        public bool TryReportInvariantViolation<TContract>(
            IPackageExtensionReference<TContract> reference,
            Exception exception)
            => string.Equals(callerPackageId, AuthorizedOrchestratorPackageId, StringComparison.Ordinal)
               && catalog.TryReportOwnerInvariantViolation(reference, exception);
    }
}
