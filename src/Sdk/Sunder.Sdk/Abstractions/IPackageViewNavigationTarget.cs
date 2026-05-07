namespace Sunder.Sdk.Abstractions;

public interface IPackageViewNavigationTarget
{
    ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record PackageViewNavigationContext(
    string ViewId,
    IReadOnlyDictionary<string, string?> Parameters);
