using Avalonia.Controls;
using Sunder.App.Bridge;

namespace Sunder.App.Services;

internal static class AppPackageViewNavigator
{
    internal enum NavigationPreparationStatus { Unsupported, Rejected, Ready }
    private static readonly IReadOnlyDictionary<string, string?> Empty = new Dictionary<string, string?>();

    public static ValueTask WarmupViewAsync(Control view, CancellationToken cancellationToken)
        => AppViewCallbacksV1.Get(view)?.WarmupAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public static ValueTask NotifyViewNavigatedAsync(Control view, string viewId,
        IReadOnlyDictionary<string, string?>? parameters, CancellationToken cancellationToken)
        => AppViewCallbacksV1.Get(view)?.NavigateAsync(viewId, parameters ?? Empty, cancellationToken) ?? ValueTask.CompletedTask;

    public static bool SupportsNavigationPreparation(Control view)
        => AppViewCallbacksV1.Get(view)?.SupportsPreparation == true;

    public static async ValueTask<NavigationPreparationStatus> PrepareViewNavigationAsync(Control view, string viewId,
        IReadOnlyDictionary<string, string?>? parameters, CancellationToken cancellationToken)
    {
        if (AppViewCallbacksV1.Get(view) is not { SupportsPreparation: true } callbacks) return NavigationPreparationStatus.Unsupported;
        return await callbacks.PrepareAsync(viewId, parameters ?? Empty, cancellationToken)
            ? NavigationPreparationStatus.Ready : NavigationPreparationStatus.Rejected;
    }

    public static ValueTask NotifyViewNavigationPresentedAsync(Control view, string viewId,
        IReadOnlyDictionary<string, string?>? parameters, CancellationToken cancellationToken)
        => AppViewCallbacksV1.Get(view)?.PresentedAsync(viewId, parameters ?? Empty, cancellationToken) ?? ValueTask.CompletedTask;
}
