using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>User-invoked navigation to the shared developer log window. Available to published App activations only.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageLogNavigationV1)]
public interface IPackageLogNavigationService
{
    /// <summary>Enables log capture and selects an exact package filter, including when the package has no entries yet.</summary>
    Task OpenPackageLogsAsync(string packageId, CancellationToken cancellationToken = default);
}
