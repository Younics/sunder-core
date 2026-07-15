using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageActivator(
    AppSharedAssemblyRegistry sharedAssemblyRegistry,
    AppPackageServiceProviderFactory serviceProviderFactory,
    AppPackageViewRegistry viewRegistry,
    AppPackageExtensionCatalog extensionCatalog,
    Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo = null)
{
    public async Task ActivateAsync(
        ActivePackageDescriptor package,
        AppPreparedPackageSource preparedSource,
        AppPackageActivationState activation,
        Action<string, Assembly> registerPackageAssembly,
        Action<AppPackageLoadContext> trackLoadContext,
        Action<object> trackOwnedDisposable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((package.HostRoles & PackageHostRoles.App) == 0)
        {
            throw new InvalidOperationException($"Package '{package.PackageId}' does not declare the App host role.");
        }
        var manifest = AppPackageManifest.Load(Path.Combine(preparedSource.Folder, "sunder-package.json"));
        if (manifest?.EntryAssembly is null)
        {
            throw new InvalidOperationException("App-side package manifest is missing entryAssembly.");
        }

        activation.PackageInfo = new AppLoadedPackageInfo(package, preparedSource.Folder, manifest);

        var loadContext = new AppPackageLoadContext(package.PackageId, activation.PackageInfo.EntryAssemblyPath, sharedAssemblyRegistry, registerPackageAssembly);
        activation.LoadContext = loadContext;
        trackLoadContext(loadContext);

        var entryAssembly = loadContext.LoadPackageEntryAssembly();
        var module = CreatePackageModule(entryAssembly);
        var packageContext = await AppPackageContext.CreateAsync(
            package.PackageId,
            package.Version,
            activation.PackageInfo.Folder,
            getRuntimeConnectionInfo,
            serviceProviderFactory.Publication,
            cancellationToken).ConfigureAwait(false);
        ServiceProvider serviceProvider;
        try
        {
            serviceProvider = serviceProviderFactory.Create(package, packageContext, module);
        }
        catch
        {
            await packageContext.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        activation.ServiceProvider = serviceProvider;
        trackOwnedDisposable(serviceProvider);

        viewRegistry.SetSettingsViewPackage(new PackageSettingsViewDescriptor(
            package.PackageId,
            package.DisplayName,
            $"Configure {package.DisplayName}."));
        if (module is not null)
        {
            var registry = new AppPackageContributionRegistry(serviceProvider, viewRegistry, extensionCatalog, package.PackageId);
            using var extensionBatch = extensionCatalog.BeginBatch(PackageExtensionCatalogChangeReason.PackageActivated);
            module.RegisterAppContributions(registry, serviceProvider);
            extensionBatch.Commit();
        }
    }

    private static ISunderAppPackageModule? CreatePackageModule(Assembly entryAssembly)
    {
        var moduleType = AppPackageModuleResolver.Resolve(entryAssembly, out var moduleResolutionError);
        if (moduleType is null)
        {
            return moduleResolutionError is null
                ? null
                : throw new InvalidOperationException(moduleResolutionError);
        }

        if (Activator.CreateInstance(moduleType) is ISunderAppPackageModule module)
        {
            return module;
        }

        throw new InvalidOperationException($"Package module '{moduleType.FullName}' does not implement ISunderAppPackageModule.");
    }
}
