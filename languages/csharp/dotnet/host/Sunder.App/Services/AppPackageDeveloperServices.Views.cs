using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed partial class AppPackageDeveloperServices
{
    Task<IReadOnlyList<PackageDevelopmentView>> IAppPackageDevelopmentBackend.GetDevelopmentViewsAsync(string packageId, PackageDevelopmentRevision revision, CancellationToken token)
        => dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            return (_packageHost ?? throw new InvalidOperationException("The App shell is unavailable.")).GetDevelopmentViews(packageId, revision);
        }, token);

    Task IAppPackageDevelopmentBackend.ShowDevelopmentViewAsync(string packageId, string viewId, PackageDevelopmentRevision revision, CancellationToken token)
        => dispatcher.InvokeAsync(() => (_launcher ?? throw new InvalidOperationException("The App shell is unavailable.")).OpenDevelopmentViewAsync(packageId, viewId, revision, token));

    Task<AppDevelopmentViewBinding> IAppPackageDevelopmentBackend.OpenDevelopmentViewAsync(string packageId, string viewId,
        PackageDevelopmentRevision revision, CancellationToken token)
        => dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            return (_packageHost ?? throw new InvalidOperationException("The owning App shell is unavailable."))
                .OpenDevelopmentView(packageId, viewId, revision);
        }, token);
}
