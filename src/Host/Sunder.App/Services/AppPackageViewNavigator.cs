using Avalonia.Controls;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class AppPackageViewNavigator
{
    internal enum NavigationPreparationStatus
    {
        Unsupported,
        Rejected,
        Ready,
    }

    public static async ValueTask WarmupViewAsync(
        Control view,
        CancellationToken cancellationToken)
    {
        if (view is IPackageViewWarmupTarget viewTarget)
        {
            await viewTarget.WarmupAsync(cancellationToken);
            return;
        }

        if (view.DataContext is IPackageViewWarmupTarget dataContextTarget)
        {
            await dataContextTarget.WarmupAsync(cancellationToken);
        }
    }

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

    public static bool SupportsNavigationPreparation(Control view)
        => view is IPackageViewNavigationPreparationTarget
           || view.DataContext is IPackageViewNavigationPreparationTarget;

    public static async ValueTask<NavigationPreparationStatus> PrepareViewNavigationAsync(
        Control view,
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken)
    {
        var target = ResolvePreparationTarget(view);
        if (target is null)
        {
            return NavigationPreparationStatus.Unsupported;
        }

        var context = new PackageViewNavigationContext(
            viewId,
            parameters ?? new Dictionary<string, string?>());
        return await target.PrepareNavigationAsync(context, cancellationToken)
            ? NavigationPreparationStatus.Ready
            : NavigationPreparationStatus.Rejected;
    }

    public static async ValueTask NotifyViewNavigationPresentedAsync(
        Control view,
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken)
    {
        if (ResolvePreparationTarget(view) is not { } target)
        {
            return;
        }

        await target.OnNavigationPresentedAsync(
            new PackageViewNavigationContext(
                viewId,
                parameters ?? new Dictionary<string, string?>()),
            cancellationToken);
    }

    private static IPackageViewNavigationPreparationTarget? ResolvePreparationTarget(Control view)
        => view as IPackageViewNavigationPreparationTarget
           ?? view.DataContext as IPackageViewNavigationPreparationTarget;
}
