using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia.Hosting;

namespace Sunder.App.Tests;

// In-process view tests use the same SDK callback adapter as generated package composition.
internal static class AppPackageViewRegistryTestExtensions
{
    internal static void RegisterPackageView<T>(this AppPackageViewRegistry registry, string packageId,
        PackageViewRegistration registration, IServiceProvider services) where T : Control
        => registry.RegisterPackageView(packageId, registration, () => Create<T>(services));
    internal static void RegisterPackageView<T>(this AppPackageViewRegistry registry, string packageId,
        string viewId, IServiceProvider services) where T : Control
        => registry.RegisterPackageView<T>(packageId, new PackageViewRegistration(viewId, viewId), services);
    internal static void RegisterSettingsView<T>(this AppPackageViewRegistry registry, string packageId, IServiceProvider services) where T : Control
        => registry.RegisterSettingsView(packageId, () => Create<T>(services));
    private static Control Create<T>(IServiceProvider services) where T : Control
        => AppPackageRuntime.AttachViewCallbacks(services.GetService<T>() ?? ActivatorUtilities.CreateInstance<T>(services));
}
