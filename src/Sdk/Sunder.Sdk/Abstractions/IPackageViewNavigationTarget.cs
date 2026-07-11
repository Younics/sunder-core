using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Receives navigation parameters when an App-owned package view is opened or retargeted.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public interface IPackageViewNavigationTarget
{
    /// <summary>Applies an immutable navigation snapshot; cancellation indicates the view is closing or being superseded.</summary>
    ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides immutable navigation data for one package view.</summary>
/// <param name="ViewId">Stable id of the target view.</param>
/// <param name="Parameters">Copied case-sensitive parameter values; individual values may be <see langword="null"/>.</param>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public sealed record PackageViewNavigationContext(
    string ViewId,
    IReadOnlyDictionary<string, string?> Parameters);
