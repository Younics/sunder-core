using Avalonia.Controls;
using Sunder.Sdk.Configuration;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ContributionsV1)]
public interface IPackageContributionRegistry
{
    [SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
    void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control;

    [SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
    void RegisterPackageView<TView>(string viewId) where TView : Control
        => RegisterPackageView<TView>(new PackageViewRegistration(viewId, viewId));

    [SunderSdkCapability(SunderSdkCapabilities.WorkspacesV1)]
    void RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration) where TFactory : class, IPackageWorkspaceFactory;

    [SunderSdkCapability(SunderSdkCapabilities.WorkspacesV1)]
    void RegisterPackageViewFactory<TFactory>(string viewId) where TFactory : class, IPackageWorkspaceFactory
        => RegisterPackageViewFactory<TFactory>(new PackageViewRegistration(viewId, viewId));

    [SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
    void RegisterSettingsView<TView>() where TView : Control;

    [SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
    void RegisterSettingsViewFactory<TFactory>() where TFactory : class, IPackageWorkspaceFactory;

    [SunderSdkCapability(SunderSdkCapabilities.BackgroundServicesV1)]
    void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService;

    [SunderSdkCapability(SunderSdkCapabilities.ExtensionsV1)]
    void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution);

    [SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
    void RegisterConfigurationSchema(PackageConfigurationSchema schema);
}
