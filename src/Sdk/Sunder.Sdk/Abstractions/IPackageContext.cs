using Microsoft.Extensions.Logging;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Logging;

namespace Sunder.Sdk.Abstractions;

/// <summary>Describes one package activation and its host-owned capabilities.</summary>
/// <remarks>The context is scoped to one Runtime or App activation and must not outlive that scope. Capability implementations are thread-safe unless their member documentation says otherwise.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.CoreV1)]
public interface IPackageContext
{
    /// <summary>Gets the immutable lowercase runtime package identifier.</summary>
    string PackageId { get; }
    /// <summary>Gets the canonical strict SemVer package version.</summary>
    string Version { get; }
    /// <summary>Gets the read-only installed package root; mutable data must use <see cref="Storage"/>.</summary>
    string InstallPath { get; }
    /// <summary>Gets package-scoped storage for the current host role.</summary>
    IPackageStorageContext Storage { get; }
    /// <summary>Gets host-owned package configuration.</summary>
    IPackageConfiguration Configuration { get; }
    /// <summary>Gets host-protected package secrets.</summary>
    IPackageSecrets Secrets { get; }
    /// <summary>Gets the package-scoped Microsoft logger factory.</summary>
    ILoggerFactory LoggerFactory { get; }
    /// <summary>Gets package logging capabilities.</summary>
    IPackageLogging Logging { get; }
}
