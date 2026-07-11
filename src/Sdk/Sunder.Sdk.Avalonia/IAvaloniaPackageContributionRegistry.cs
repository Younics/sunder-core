using Avalonia.Controls;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Avalonia;

/// <summary>Registers Avalonia App contributions during package activation.</summary>
/// <remarks>Registration occurs on the UI thread. The App owns created controls and factories for the activation lifecycle.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ContributionsV1)]
public interface IAvaloniaPackageContributionRegistry : ISunderAppContributionRegistry
{
    /// <summary>Registers an App-owned view type with its stable package-scoped metadata.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
    void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control;

    /// <summary>Registers an App-owned view type using its stable package-scoped id as its name.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
    void RegisterPackageView<TView>(string viewId) where TView : Control
        => RegisterPackageView<TView>(new PackageViewRegistration(viewId, viewId));

    /// <summary>Registers a factory that creates App-owned workspace views.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.WorkspacesV1)]
    void RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration) where TFactory : class, IPackageWorkspaceFactory;

    /// <summary>Registers a workspace factory using its stable package-scoped id as its name.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.WorkspacesV1)]
    void RegisterPackageViewFactory<TFactory>(string viewId) where TFactory : class, IPackageWorkspaceFactory
        => RegisterPackageViewFactory<TFactory>(new PackageViewRegistration(viewId, viewId));

    /// <summary>Registers the package settings control type.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
    void RegisterSettingsView<TView>() where TView : Control;

    /// <summary>Registers a factory that creates the package settings control.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
    void RegisterSettingsViewFactory<TFactory>() where TFactory : class, IPackageWorkspaceFactory;

}

/// <summary>Provides Avalonia registrations on the host-neutral App registry.</summary>
public static class AvaloniaPackageContributionRegistryExtensions
{
    /// <summary>Registers an App-owned view type and metadata.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
    public static void RegisterPackageView<TView>(this ISunderAppContributionRegistry registry, PackageViewRegistration registration)
        where TView : Control
        => GetAvaloniaRegistry(registry).RegisterPackageView<TView>(registration);

    /// <summary>Registers an App-owned view type by id.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
    public static void RegisterPackageView<TView>(this ISunderAppContributionRegistry registry, string viewId)
        where TView : Control
        => GetAvaloniaRegistry(registry).RegisterPackageView<TView>(viewId);

    /// <summary>Registers an App-owned workspace factory and metadata.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.WorkspacesV1)]
    public static void RegisterPackageViewFactory<TFactory>(this ISunderAppContributionRegistry registry, PackageViewRegistration registration)
        where TFactory : class, IPackageWorkspaceFactory
        => GetAvaloniaRegistry(registry).RegisterPackageViewFactory<TFactory>(registration);

    /// <summary>Registers an App-owned workspace factory by id.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.WorkspacesV1)]
    public static void RegisterPackageViewFactory<TFactory>(this ISunderAppContributionRegistry registry, string viewId)
        where TFactory : class, IPackageWorkspaceFactory
        => GetAvaloniaRegistry(registry).RegisterPackageViewFactory<TFactory>(viewId);

    /// <summary>Registers the package settings control type.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
    public static void RegisterSettingsView<TView>(this ISunderAppContributionRegistry registry)
        where TView : Control
        => GetAvaloniaRegistry(registry).RegisterSettingsView<TView>();

    /// <summary>Registers a factory that creates the package settings control.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
    public static void RegisterSettingsViewFactory<TFactory>(this ISunderAppContributionRegistry registry)
        where TFactory : class, IPackageWorkspaceFactory
        => GetAvaloniaRegistry(registry).RegisterSettingsViewFactory<TFactory>();

    private static IAvaloniaPackageContributionRegistry GetAvaloniaRegistry(ISunderAppContributionRegistry registry)
        => registry as IAvaloniaPackageContributionRegistry
           ?? throw new InvalidOperationException("The App host does not support Avalonia package contributions.");
}
