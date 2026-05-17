using Avalonia.Controls;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class AppPackageViewNavigator
{
    public static async ValueTask NotifyViewNavigatedAsync(
        Control view,
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken)
    {
        var context = new PackageViewNavigationContext(viewId, parameters ?? new Dictionary<string, string?>());
        if (view is IPackageViewNavigationTarget viewTarget)
        {
            await viewTarget.OnNavigatedToAsync(context, cancellationToken);
            return;
        }

        if (view.DataContext is IPackageViewNavigationTarget dataContextTarget)
        {
            await dataContextTarget.OnNavigatedToAsync(context, cancellationToken);
        }
    }
}
