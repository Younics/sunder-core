using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageViewRegistry
{
    private readonly object _syncRoot = new();
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
        => RegisterPackageView(packageId, viewId, serviceProvider, typeof(TView), AppRegistrationKind.View);

    public void RegisterPackageViewFactory<TFactory>(string packageId, string viewId, IServiceProvider serviceProvider)
        where TFactory : class, IPackageWorkspaceFactory
        => RegisterPackageView(packageId, viewId, serviceProvider, typeof(TFactory), AppRegistrationKind.Factory);

    private void RegisterPackageView(
        string packageId,
        string viewId,
        IServiceProvider serviceProvider,
        Type implementationType,
        AppRegistrationKind registrationKind)
    {
        lock (_syncRoot)
        {
            if (_registeredViews.TryGetValue(viewId, out var existingRegistration)
                && !string.Equals(existingRegistration.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Package view id '{viewId}' is already registered by package '{existingRegistration.PackageId}'.");
            }

            _registeredViews[viewId] = new AppRegisteredPackageView(serviceProvider, implementationType, registrationKind, packageId);
            TrackPackageViewId(packageId, viewId);
        }
    }

    public void RegisterSettingsView<TView>(string packageId, IServiceProvider serviceProvider)
        where TView : Control
    {
        lock (_syncRoot)
        {
            _registeredSettingsViews[packageId] = new AppRegisteredSettingsView(serviceProvider, typeof(TView), AppRegistrationKind.View, packageId);
        }
    }

    public void RegisterSettingsViewFactory<TFactory>(string packageId, IServiceProvider serviceProvider)
        where TFactory : class, IPackageWorkspaceFactory
    {
        lock (_syncRoot)
        {
            _registeredSettingsViews[packageId] = new AppRegisteredSettingsView(serviceProvider, typeof(TFactory), AppRegistrationKind.Factory, packageId);
        }
    }

    public Control? GetOrCreateView(
        string viewId,
        Func<string, bool> isPackageDisabled,
        Action<string, string, Exception> reportFailure)
    {
        AppRegisteredPackageView registration;
        lock (_syncRoot)
        {
            if (!_registeredViews.TryGetValue(viewId, out var foundRegistration))
            {
                return null;
            }
            registration = foundRegistration;
        }

        if (isPackageDisabled(registration.PackageId))
        {
            return null;
        }

        Control control;
        try
        {
            control = CreateControl(registration.ServiceProvider, registration.ImplementationType, registration.RegistrationKind);
        }
        catch (Exception ex)
        {
            reportFailure(registration.PackageId, $"Failed to create package view '{viewId}': {ex.Message}", ex);
            return null;
        }

        Control? unusedControl = null;
        Control? result = null;
        lock (_syncRoot)
        {
            if (!_registeredViews.TryGetValue(viewId, out var currentRegistration)
                || currentRegistration != registration)
            {
                unusedControl = control;
            }
            else if (_viewCache.TryGetValue(viewId, out var cachedView))
            {
                unusedControl = control;
                result = cachedView;
            }
            else
            {
                _viewCache[viewId] = control;
                result = control;
            }
        }

        DisposeCachedControl(unusedControl);
        return result;
    }

    public bool HasSettingsView(string packageId)
    {
        lock (_syncRoot)
        {
            return _registeredSettingsViews.ContainsKey(packageId);
        }
    }

    public void SetSettingsViewPackage(PackageSettingsViewDescriptor descriptor)
    {
        lock (_syncRoot)
        {
            _settingsViewPackagesById[descriptor.PackageId] = descriptor;
        }
    }

    public IReadOnlyList<string> ListPackageViewIds(string packageId)
    {
        lock (_syncRoot)
        {
            return _viewIdsByPackageId.TryGetValue(packageId, out var viewIds) ? viewIds.ToArray() : [];
        }
    }

    public IReadOnlyList<PackageSettingsViewDescriptor> ListSettingsViewPackages(Func<string, bool> isPackageDisabled)
    {
        PackageSettingsViewDescriptor[] descriptors;
        lock (_syncRoot)
        {
            descriptors = _registeredSettingsViews.Keys
                .Select(packageId => _settingsViewPackagesById.TryGetValue(packageId, out var descriptor)
                    ? descriptor
                    : new PackageSettingsViewDescriptor(packageId, packageId, $"Configure {packageId}."))
                .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return descriptors
            .Where(package => !isPackageDisabled(package.PackageId))
            .ToArray();
    }

    public Control? GetOrCreateSettingsView(
        string packageId,
        Func<string, bool> isPackageDisabled,
        Action<string, string, Exception> reportFailure)
    {
        AppRegisteredSettingsView registration;
        lock (_syncRoot)
        {
            if (!_registeredSettingsViews.TryGetValue(packageId, out var foundRegistration))
            {
                return null;
            }
            registration = foundRegistration;
        }

        if (isPackageDisabled(registration.PackageId))
        {
            return null;
        }

        Control control;
        try
        {
            control = CreateControl(registration.ServiceProvider, registration.ImplementationType, registration.RegistrationKind);
        }
        catch (Exception ex)
        {
            reportFailure(registration.PackageId, $"Failed to create package settings view for '{packageId}': {ex.Message}", ex);
            return null;
        }

        Control? unusedControl = null;
        Control? result = null;
        lock (_syncRoot)
        {
            if (!_registeredSettingsViews.TryGetValue(packageId, out var currentRegistration)
                || currentRegistration != registration)
            {
                unusedControl = control;
            }
            else if (_settingsViewCache.TryGetValue(packageId, out var cachedView))
            {
                unusedControl = control;
                result = cachedView;
            }
            else
            {
                _settingsViewCache[packageId] = control;
                result = control;
            }
        }

        DisposeCachedControl(unusedControl);
        return result;
    }

    public void RemoveCachedViews(string packageId)
    {
        var controlsToDispose = new List<Control>();
        lock (_syncRoot)
        {
            if (_viewIdsByPackageId.TryGetValue(packageId, out var viewIds))
            {
                foreach (var viewId in viewIds)
                {
                    if (_viewCache.Remove(viewId, out var cachedView))
                    {
                        controlsToDispose.Add(cachedView);
                    }
                }
            }

            if (_settingsViewCache.Remove(packageId, out var cachedSettingsView))
            {
                controlsToDispose.Add(cachedSettingsView);
            }
        }

        DisposeCachedControls(controlsToDispose);
    }

    public async Task RemoveCachedViewsAsync(string packageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess() || Application.Current is null)
        {
            RemoveCachedViews(packageId);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            () => RemoveCachedViews(packageId),
            DispatcherPriority.Normal);
    }

    public bool RemoveCachedView(string viewId)
    {
        Control? controlToDispose;
        lock (_syncRoot)
        {
            if (!_viewCache.Remove(viewId, out var cachedView))
            {
                return false;
            }

            controlToDispose = cachedView;
        }

        DisposeCachedControl(controlToDispose);
        return true;
    }

    public IReadOnlyList<string> UnregisterPackage(string packageId)
    {
        var controlsToDispose = new List<Control>();
        string[] removedViewIds;
        lock (_syncRoot)
        {
            removedViewIds = _viewIdsByPackageId.TryGetValue(packageId, out var viewIds) ? viewIds.ToArray() : [];
            foreach (var viewId in removedViewIds)
            {
                _registeredViews.Remove(viewId);
                if (_viewCache.Remove(viewId, out var cachedView))
                {
                    controlsToDispose.Add(cachedView);
                }
            }

            _registeredSettingsViews.Remove(packageId);
            if (_settingsViewCache.Remove(packageId, out var cachedSettingsView))
            {
                controlsToDispose.Add(cachedSettingsView);
            }
            _settingsViewPackagesById.Remove(packageId);
            _viewIdsByPackageId.Remove(packageId);
        }

        DisposeCachedControls(controlsToDispose);
        return removedViewIds;
    }

    public async Task<IReadOnlyList<string>> UnregisterPackageAsync(string packageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess() || Application.Current is null)
        {
            return UnregisterPackage(packageId);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => UnregisterPackage(packageId),
            DispatcherPriority.Normal);
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

    private static void DisposeCachedControls(IEnumerable<Control> controls)
    {
        foreach (var control in controls)
        {
            DisposeCachedControl(control);
        }
    }

    private static void DisposeCachedControl(Control? control)
    {
        if (control is null)
        {
            return;
        }

        object? dataContext = null;
        try
        {
            dataContext = control.DataContext;
        }
        catch (InvalidOperationException)
        {
            // Avalonia controls are thread-affined; cleanup can run after callers leave the owning UI thread.
        }

        if (dataContext is IDisposable dataContextDisposable && !ReferenceEquals(dataContextDisposable, control))
        {
            try
            {
                dataContextDisposable.Dispose();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to dispose package view data context.", ex);
            }
        }

        if (control is IDisposable controlDisposable)
        {
            try
            {
                controlDisposable.Dispose();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to dispose package view control.", ex);
            }
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
