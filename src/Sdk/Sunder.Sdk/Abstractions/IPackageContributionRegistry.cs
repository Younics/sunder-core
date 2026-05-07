using Avalonia.Controls;
using Sunder.Sdk.Configuration;

namespace Sunder.Sdk.Abstractions;

public interface IPackageContributionRegistry
{
    void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control;

    void RegisterPackageView<TView>(string viewId) where TView : Control
        => RegisterPackageView<TView>(new PackageViewRegistration(viewId, viewId));

    void RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration) where TFactory : class, IPackageWorkspaceFactory;

    void RegisterPackageViewFactory<TFactory>(string viewId) where TFactory : class, IPackageWorkspaceFactory
        => RegisterPackageViewFactory<TFactory>(new PackageViewRegistration(viewId, viewId));

    void RegisterSettingsView<TView>() where TView : Control;

    void RegisterSettingsViewFactory<TFactory>() where TFactory : class, IPackageWorkspaceFactory;

    void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService;

    void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution);

    void RegisterConfigurationSchema(PackageConfigurationSchema schema);
}
