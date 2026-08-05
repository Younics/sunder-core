using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Optionally prepares parameterized package-view navigation before the host presents the destination.</summary>
/// <remarks>The host attaches the destination to a non-interactive, transparent layout slot at its final panel dimensions, keeps the current view presented, and invokes preparation. Returning <see langword="false"/> rejects the navigation without presenting the destination. A superseding navigation or owner retirement cancels preparation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
[SunderSdkCapability(SunderSdkCapabilities.ViewNavigationPreparationV1)]
public interface IPackageViewNavigationPreparationTarget
{
    /// <summary>Prepares the final destination state while the view remains hidden.</summary>
    /// <remarks>Implementations should complete authoritative data loading, final collection publication, layout-sensitive rendering, and initial viewport placement before returning.</remarks>
    ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges that the prepared destination has been presented by the host.</summary>
    /// <remarks>This callback must not change initial layout or viewport placement. It may start paint-only presentation effects.</remarks>
    ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default);
}
