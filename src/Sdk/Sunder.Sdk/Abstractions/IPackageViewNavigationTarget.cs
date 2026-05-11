using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public interface IPackageViewNavigationTarget
{
    ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default);
}

[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public sealed record PackageViewNavigationContext(
    string ViewId,
    IReadOnlyDictionary<string, string?> Parameters);
