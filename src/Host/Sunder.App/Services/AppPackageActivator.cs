using System.Reflection;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageActivator(
    AppSharedAssemblyRegistry sharedAssemblyRegistry,
    AppPackageServiceProviderFactory serviceProviderFactory,
    AppPackageViewRegistry viewRegistry,
    AppPackageBackgroundServiceCoordinator backgroundServices,
    AppPackageExtensionCatalog extensionCatalog)
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
        var manifest = AppPackageManifest.Load(Path.Combine(preparedSource.Folder, "sunder-package.json"));
        if (manifest?.EntryAssembly is null)
        {
            throw new InvalidOperationException("App-side package manifest is missing entryAssembly.");
        }

        activation.PackageInfo = new AppLoadedPackageInfo(package, preparedSource.Folder, manifest);
        sharedAssemblyRegistry.AddProbeDirectories([activation.PackageInfo.LibraryFolder]);

        var loadContext = new AppPackageLoadContext(package.PackageId, activation.PackageInfo.EntryAssemblyPath, sharedAssemblyRegistry, registerPackageAssembly);
        activation.LoadContext = loadContext;
        trackLoadContext(loadContext);

        var entryAssembly = loadContext.LoadPackageEntryAssembly();
        var module = CreatePackageModule(entryAssembly);
        var packageContext = new AppPackageContext(package.PackageId, package.Version, activation.PackageInfo.Folder);
        var serviceProvider = serviceProviderFactory.Create(package, packageContext, module);
        activation.ServiceProvider = serviceProvider;
        trackOwnedDisposable(serviceProvider);

        viewRegistry.SetSettingsViewPackage(new PackageSettingsViewDescriptor(
            package.PackageId,
            package.DisplayName,
            $"Configure {package.DisplayName}."));
        var registry = new AppPackageContributionRegistry(serviceProvider, viewRegistry, backgroundServices, extensionCatalog, package.PackageId);
        module.RegisterContributions(registry, serviceProvider);
        await backgroundServices.StartAsync(package.PackageId, cancellationToken);
    }

    private static ISunderPackageModule CreatePackageModule(Assembly entryAssembly)
    {
        var moduleType = AppPackageModuleResolver.Resolve(entryAssembly, out var moduleResolutionError);
        if (moduleType is null)
        {
            throw new InvalidOperationException(moduleResolutionError);
        }

        if (Activator.CreateInstance(moduleType) is ISunderPackageModule module)
        {
            return module;
        }

        throw new InvalidOperationException($"Package module '{moduleType.FullName}' does not implement ISunderPackageModule.");
    }
}
