using System.Reflection;
using Sunder.Protocol;

namespace Sunder.App.Services;

internal sealed class AppPackageLoadCoordinator(
    AppPackageSourceLoader sourceLoader,
    AppPackageActivator packageActivator,
    AppPackageUnloadCoordinator unloadCoordinator,
    Func<string, string, PackageFailureOrigin, Exception?, CancellationToken, Task> disablePackageAsync,
    Action<string, Assembly> registerPackageAssembly,
    Action<AppPackageLoadContext> trackLoadContext,
    Action<object> trackOwnedDisposable,
    Action<string, AppLoadedPackageHandle> setLoadedPackage)
{
    public async Task LoadPackageAsync(
        ActivePackageDescriptor package,
        PackageSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        var sourceLoadResult = await sourceLoader.LoadAsync(package, source, cancellationToken);
        if (!sourceLoadResult.IsSuccess)
        {
            await disablePackageAsync(
                package.PackageId,
                sourceLoadResult.FailureMessage ?? "Failed to prepare app-side package source.",
                PackageFailureOrigin.AppActivation,
                null,
                cancellationToken);
            return;
        }

        if (sourceLoadResult.PreparedSource is null)
        {
            throw new InvalidOperationException($"Package '{package.PackageId}' source loader reported success without a prepared source.");
        }

        await ActivatePreparedPackageAsync(package, source, sourceLoadResult.PreparedSource, cancellationToken);
    }

    private async Task ActivatePreparedPackageAsync(
        ActivePackageDescriptor package,
        PackageSourceDescriptor source,
        AppPreparedPackageSource preparedSource,
        CancellationToken cancellationToken)
    {
        var activation = new AppPackageActivationState();
        var packageActivated = false;
        try
        {
            await packageActivator.ActivateAsync(
                package,
                preparedSource,
                activation,
                registerPackageAssembly,
                trackLoadContext,
                trackOwnedDisposable,
                cancellationToken);

            if (activation.PackageInfo is null || activation.ServiceProvider is null || activation.LoadContext is null)
            {
                throw new InvalidOperationException($"Package '{package.PackageId}' activation did not produce a complete app-side package handle.");
            }

            setLoadedPackage(
                package.PackageId,
                new AppLoadedPackageHandle(
                    package,
                    source,
                    activation.PackageInfo.Folder,
                    activation.ServiceProvider,
                    activation.LoadContext));
            packageActivated = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await disablePackageAsync(
                package.PackageId,
                $"Failed to activate package views: {ex.Message}",
                PackageFailureOrigin.AppActivation,
                ex,
                CancellationToken.None);
        }
        finally
        {
            if (!packageActivated)
            {
                await unloadCoordinator.RollBackActivationAsync(
                    package.PackageId,
                    activation.PackageInfo,
                    activation.ServiceProvider,
                    activation.LoadContext);
            }
        }
    }
}
