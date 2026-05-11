using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageViewRegistry
{
    private readonly Dictionary<string, AppRegisteredPackageView> _registeredViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppRegisteredSettingsView> _registeredSettingsViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _viewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _settingsViewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageSettingsViewDescriptor> _settingsViewPackagesById;
    private readonly Dictionary<string, List<string>> _viewIdsByPackageId = new(StringComparer.OrdinalIgnoreCase);

    public AppPackageViewRegistry()
        : this(new Dictionary<string, PackageSettingsViewDescriptor>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public AppPackageViewRegistry(IDictionary<string, PackageSettingsViewDescriptor> settingsViewPackagesById)
    {
        _settingsViewPackagesById = new Dictionary<string, PackageSettingsViewDescriptor>(settingsViewPackagesById, StringComparer.OrdinalIgnoreCase);
    }

    public void RegisterPackageView<TView>(string packageId, string viewId, IServiceProvider serviceProvider)
        where TView : Control
    {
        _registeredViews[viewId] = new AppRegisteredPackageView(serviceProvider, typeof(TView), AppRegistrationKind.View, packageId);
        TrackPackageViewId(packageId, viewId);
    }

    public void RegisterPackageViewFactory<TFactory>(string packageId, string viewId, IServiceProvider serviceProvider)
        where TFactory : class, IPackageWorkspaceFactory
    {
        _registeredViews[viewId] = new AppRegisteredPackageView(serviceProvider, typeof(TFactory), AppRegistrationKind.Factory, packageId);
        TrackPackageViewId(packageId, viewId);
    }

    public void RegisterSettingsView<TView>(string packageId, IServiceProvider serviceProvider)
        where TView : Control
    {
        _registeredSettingsViews[packageId] = new AppRegisteredSettingsView(serviceProvider, typeof(TView), AppRegistrationKind.View, packageId);
    }

    public void RegisterSettingsViewFactory<TFactory>(string packageId, IServiceProvider serviceProvider)
        where TFactory : class, IPackageWorkspaceFactory
    {
        _registeredSettingsViews[packageId] = new AppRegisteredSettingsView(serviceProvider, typeof(TFactory), AppRegistrationKind.Factory, packageId);
    }

    public Control? GetOrCreateView(
        string viewId,
        Func<string, bool> isPackageDisabled,
        Action<string, string, Exception> reportFailure)
    {
        if (!_registeredViews.TryGetValue(viewId, out var registration))
        {
            return null;
        }

        if (isPackageDisabled(registration.PackageId))
        {
            return null;
        }

        if (_viewCache.TryGetValue(viewId, out var cachedView))
        {
            return cachedView;
        }

        try
        {
            var control = CreateControl(registration.ServiceProvider, registration.ImplementationType, registration.RegistrationKind);
            _viewCache[viewId] = control;
            return control;
        }
        catch (Exception ex)
        {
            reportFailure(registration.PackageId, $"Failed to create package view '{viewId}': {ex.Message}", ex);
            return null;
        }
    }

    public bool HasSettingsView(string packageId)
        => _registeredSettingsViews.ContainsKey(packageId);

    public void SetSettingsViewPackage(PackageSettingsViewDescriptor descriptor)
    {
        _settingsViewPackagesById[descriptor.PackageId] = descriptor;
    }

    public IReadOnlyList<string> ListPackageViewIds(string packageId)
        => _viewIdsByPackageId.TryGetValue(packageId, out var viewIds) ? viewIds.ToArray() : [];

    public IReadOnlyList<PackageSettingsViewDescriptor> ListSettingsViewPackages(Func<string, bool> isPackageDisabled)
        => _registeredSettingsViews.Keys
            .Where(packageId => !isPackageDisabled(packageId))
            .Select(packageId => _settingsViewPackagesById.TryGetValue(packageId, out var descriptor)
                ? descriptor
                : new PackageSettingsViewDescriptor(packageId, packageId, $"Configure {packageId}."))
            .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public Control? GetOrCreateSettingsView(
        string packageId,
        Func<string, bool> isPackageDisabled,
        Action<string, string, Exception> reportFailure)
    {
        if (!_registeredSettingsViews.TryGetValue(packageId, out var registration))
        {
            return null;
        }

        if (isPackageDisabled(registration.PackageId))
        {
            return null;
        }

        if (_settingsViewCache.TryGetValue(packageId, out var cachedView))
        {
            return cachedView;
        }

        try
        {
            var control = CreateControl(registration.ServiceProvider, registration.ImplementationType, registration.RegistrationKind);
            _settingsViewCache[packageId] = control;
            return control;
        }
        catch (Exception ex)
        {
            reportFailure(registration.PackageId, $"Failed to create package settings view for '{packageId}': {ex.Message}", ex);
            return null;
        }
    }

    public void RemoveCachedViews(string packageId)
    {
        if (_viewIdsByPackageId.TryGetValue(packageId, out var viewIds))
        {
            foreach (var viewId in viewIds)
            {
                if (_viewCache.Remove(viewId, out var cachedView))
                {
                    DisposeCachedControl(cachedView);
                }
            }
        }

        if (_settingsViewCache.Remove(packageId, out var cachedSettingsView))
        {
            DisposeCachedControl(cachedSettingsView);
        }
    }

    public bool RemoveCachedView(string viewId)
    {
        if (!_viewCache.Remove(viewId, out var cachedView))
        {
            return false;
        }

        DisposeCachedControl(cachedView);
        return true;
    }

    public IReadOnlyList<string> UnregisterPackage(string packageId)
    {
        var removedViewIds = ListPackageViewIds(packageId);
        foreach (var viewId in removedViewIds)
        {
            _registeredViews.Remove(viewId);
            if (_viewCache.Remove(viewId, out var cachedView))
            {
                DisposeCachedControl(cachedView);
            }
        }

        _registeredSettingsViews.Remove(packageId);
        if (_settingsViewCache.Remove(packageId, out var cachedSettingsView))
        {
            DisposeCachedControl(cachedSettingsView);
        }
        _settingsViewPackagesById.Remove(packageId);
        _viewIdsByPackageId.Remove(packageId);
        return removedViewIds;
    }

    private void TrackPackageViewId(string packageId, string viewId)
    {
        if (!_viewIdsByPackageId.TryGetValue(packageId, out var viewIds))
        {
            viewIds = [];
            _viewIdsByPackageId[packageId] = viewIds;
        }

        if (!viewIds.Contains(viewId, StringComparer.OrdinalIgnoreCase))
        {
            viewIds.Add(viewId);
        }
    }

    private static Control CreateControl(IServiceProvider serviceProvider, Type implementationType, AppRegistrationKind registrationKind)
    {
        if (registrationKind == AppRegistrationKind.View)
        {
            return (Control)ActivatorUtilities.CreateInstance(serviceProvider, implementationType);
        }

        var factory = (IPackageWorkspaceFactory)ActivatorUtilities.CreateInstance(serviceProvider, implementationType);
        return factory.CreateRootView(serviceProvider);
    }

    private static void DisposeCachedControl(Control control)
    {
        if (control.DataContext is IDisposable dataContextDisposable && !ReferenceEquals(dataContextDisposable, control))
        {
            dataContextDisposable.Dispose();
        }

        if (control is IDisposable controlDisposable)
        {
            controlDisposable.Dispose();
        }
    }

    private sealed record AppRegisteredPackageView(IServiceProvider ServiceProvider, Type ImplementationType, AppRegistrationKind RegistrationKind, string PackageId);

    private sealed record AppRegisteredSettingsView(IServiceProvider ServiceProvider, Type ImplementationType, AppRegistrationKind RegistrationKind, string PackageId);

    private enum AppRegistrationKind
    {
        View = 0,
        Factory = 1,
    }
}
