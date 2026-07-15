using Avalonia.Controls;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Avalonia;

/// <summary>Registers Avalonia App contributions during package activation.</summary>
/// <remarks>Registration occurs on the UI thread. The App creates controls from the package App service provider and owns them for the activation lifecycle.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ContributionsV1)]
public interface IAvaloniaPackageContributionRegistry : ISunderAppContributionRegistry
{
    /// <summary>Registers an App-owned view type with its stable globally unique metadata.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
    void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control;

    /// <summary>Registers an App-owned view type using its stable globally unique id as its name.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
    void RegisterPackageView<TView>(string viewId) where TView : Control
        => RegisterPackageView<TView>(new PackageViewRegistration(viewId, viewId));

    /// <summary>Registers the package settings control type.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
    void RegisterSettingsView<TView>() where TView : Control;
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

    /// <summary>Registers the package settings control type.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
    public static void RegisterSettingsView<TView>(this ISunderAppContributionRegistry registry)
        where TView : Control
        => GetAvaloniaRegistry(registry).RegisterSettingsView<TView>();

    private static IAvaloniaPackageContributionRegistry GetAvaloniaRegistry(ISunderAppContributionRegistry registry)
        => registry as IAvaloniaPackageContributionRegistry
           ?? throw new InvalidOperationException("The App host does not support Avalonia package contributions.");
}
