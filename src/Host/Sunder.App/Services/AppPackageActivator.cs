using System.Reflection;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageActivator(
    AppSharedAssemblyRegistry sharedAssemblyRegistry,
    AppPackageServiceProviderFactory serviceProviderFactory,
    AppPackageViewRegistry viewRegistry,
    AppPackageExtensionCatalog extensionCatalog,
    bool isPreflight = false,
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
            isPreflight,
            getRuntimeConnectionInfo,
            cancellationToken).ConfigureAwait(false);
        var serviceProvider = serviceProviderFactory.Create(package, packageContext, module);
        activation.ServiceProvider = serviceProvider;
        trackOwnedDisposable(serviceProvider);

        viewRegistry.SetSettingsViewPackage(new PackageSettingsViewDescriptor(
            package.PackageId,
            package.DisplayName,
            $"Configure {package.DisplayName}."));
        if (module is not null)
        {
            var registry = new AppPackageContributionRegistry(serviceProvider, viewRegistry, extensionCatalog, package.PackageId);
            module.RegisterAppContributions(registry, serviceProvider);
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
