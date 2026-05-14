using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.SettingsNavigationV1)]
public interface IPackageSettingsNavigationService
{
    ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);
}

[SunderSdkCapability(SunderSdkCapabilities.SettingsNavigationV1)]
public sealed class NullPackageSettingsNavigationService : IPackageSettingsNavigationService
{
    public static NullPackageSettingsNavigationService Instance { get; } = new();

    private NullPackageSettingsNavigationService()
    {
    }

    public ValueTask<bool> OpenSettingsAsync(
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }

    public ValueTask<bool> OpenPackageSettingsAsync(
        string packageId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}
