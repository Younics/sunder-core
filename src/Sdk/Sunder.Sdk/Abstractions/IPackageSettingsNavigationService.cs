using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Requests App-owned settings navigation from package code.</summary>
/// <remarks>Parameters are copied before UI dispatch. A false result means the destination is unavailable.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.SettingsNavigationV1)]
public interface IPackageSettingsNavigationService
{
    /// <summary>Opens global settings with optional navigation parameters.</summary>
    ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens settings for a package id with optional navigation parameters.</summary>
    ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Represents host contexts without settings navigation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.SettingsNavigationV1)]
public sealed class NullPackageSettingsNavigationService : IPackageSettingsNavigationService
{
    /// <summary>Gets the shared stateless instance.</summary>
    public static NullPackageSettingsNavigationService Instance { get; } = new();

    private NullPackageSettingsNavigationService()
    {
    }

    /// <summary>Observes cancellation and returns <see langword="false"/> without side effects.</summary>
    public ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }

    /// <summary>Observes cancellation and returns <see langword="false"/> without side effects.</summary>
    public ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}
