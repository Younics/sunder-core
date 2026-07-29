using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Runtime.Contracts;

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
    private readonly IUiDispatcher _uiDispatcher;

    public AppPackageViewRegistry(IUiDispatcher? uiDispatcher = null)
        : this(new Dictionary<string, PackageSettingsViewDescriptor>(StringComparer.OrdinalIgnoreCase), uiDispatcher)
    {
    }

    public AppPackageViewRegistry(
        IDictionary<string, PackageSettingsViewDescriptor> settingsViewPackagesById,
        IUiDispatcher? uiDispatcher = null)
    {
        _settingsViewPackagesById = new Dictionary<string, PackageSettingsViewDescriptor>(settingsViewPackagesById, StringComparer.OrdinalIgnoreCase);
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
    }

    public void RegisterPackageView<TView>(string packageId, PackageViewRegistration registration, IServiceProvider serviceProvider)
        where TView : Control
        => RegisterPackageView(packageId, registration, serviceProvider, typeof(TView));

    internal void RegisterPackageView<TView>(string packageId, string viewId, IServiceProvider serviceProvider)
        where TView : Control
        => RegisterPackageView<TView>(packageId, new PackageViewRegistration(viewId, viewId), serviceProvider);

    internal string? GetPackageId(string viewId)
    {
        lock (_syncRoot)
        {
            return _registeredViews.TryGetValue(viewId, out var registration)
                ? registration.PackageId
                : null;
        }
    }

    private void RegisterPackageView(
        string packageId,
        PackageViewRegistration registration,
        IServiceProvider serviceProvider,
        Type implementationType)
    {
        var viewId = registration.Id;
        lock (_syncRoot)
        {
            if (_registeredViews.TryGetValue(viewId, out var existingRegistration)
                && !string.Equals(existingRegistration.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Package view id '{viewId}' is already registered by package '{existingRegistration.PackageId}'.");
            }

            _registeredViews[viewId] = new AppRegisteredPackageView(serviceProvider, implementationType, packageId, registration);
            TrackPackageViewId(packageId, viewId);
        }
    }

    public IReadOnlyList<PackageViewDescriptor> GetPackageViewDescriptors(string packageId)
    {
        lock (_syncRoot)
        {
            return _registeredViews.Values
                .Where(view => string.Equals(view.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                .Select(view => new PackageViewDescriptor(
                    view.Registration.Id,
                    packageId,
                    view.Registration.Name,
                    string.IsNullOrWhiteSpace(view.Registration.IconAssetPath) ? null : new PackageIconDescriptor(null, view.Registration.IconAssetPath),
                    view.Registration.DefaultPlacement.ToString(),
                    view.Registration.ShowInHotbarByDefault))
                .ToArray();
        }
    }

    public void RegisterSettingsView<TView>(string packageId, IServiceProvider serviceProvider)
        where TView : Control
    {
        lock (_syncRoot)
        {
            _registeredSettingsViews[packageId] = new AppRegisteredSettingsView(serviceProvider, typeof(TView), packageId);
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

        lock (_syncRoot)
        {
            if (_viewCache.TryGetValue(viewId, out var cachedView))
            {
                return cachedView;
            }
        }

        Control control;
        try
        {
            control = CreateControl(registration.ServiceProvider, registration.ImplementationType);
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

        lock (_syncRoot)
        {
            if (_settingsViewCache.TryGetValue(packageId, out var cachedView))
            {
                return cachedView;
            }
        }

        Control control;
        try
        {
            control = CreateControl(registration.ServiceProvider, registration.ImplementationType);
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

    public AppPackageSettingsViewNavigationTarget? GetSettingsViewForNavigation(
        string packageId,
        bool requireReplacement,
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

        lock (_syncRoot)
        {
            if (!_registeredSettingsViews.TryGetValue(packageId, out var currentRegistration)
                || currentRegistration != registration)
            {
                return null;
            }
            if (!requireReplacement && _settingsViewCache.TryGetValue(packageId, out var cachedView))
            {
                return new AppPackageSettingsViewNavigationTarget(cachedView, Candidate: null);
            }
        }

        Control control;
        try
        {
            control = CreateControl(registration.ServiceProvider, registration.ImplementationType);
        }
        catch (Exception ex)
        {
            reportFailure(registration.PackageId, $"Failed to create package settings view for '{packageId}': {ex.Message}", ex);
            return null;
        }

        var isCurrent = false;
        lock (_syncRoot)
        {
            isCurrent = _registeredSettingsViews.TryGetValue(packageId, out var currentRegistration)
                && currentRegistration == registration;
        }
        if (!isCurrent)
        {
            DisposeCachedControl(control);
            return null;
        }

        var candidate = new AppPackageSettingsViewCandidate(
            this,
            packageId,
            registration,
            control,
            isPackageDisabled);
        return new AppPackageSettingsViewNavigationTarget(control, candidate);
    }

    internal bool PromoteSettingsViewCandidate(AppPackageSettingsViewCandidate candidate)
    {
        if (!ReferenceEquals(candidate.Owner, this)
            || candidate.IsPackageDisabled(candidate.PackageId))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (candidate.IsCompleted
                || candidate.IsPromoted
                || !_registeredSettingsViews.TryGetValue(candidate.PackageId, out var registration)
                || !Equals(registration, candidate.Registration))
            {
                return false;
            }

            _settingsViewCache.Remove(candidate.PackageId, out var displacedView);
            _settingsViewCache[candidate.PackageId] = candidate.View;
            candidate.MarkPromoted(displacedView);
            return true;
        }
    }

    internal void ReleaseSettingsViewCandidate(AppPackageSettingsViewCandidate candidate)
    {
        Control? controlToDispose;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(candidate.Owner, this) || !candidate.TryComplete(out controlToDispose))
            {
                return;
            }
        }

        DisposeCachedControl(controlToDispose);
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
        if (_uiDispatcher.CheckAccess() || Application.Current is null)
        {
            RemoveCachedViews(packageId);
            return;
        }

        await _uiDispatcher.InvokeAsync(() => RemoveCachedViews(packageId));
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
        if (_uiDispatcher.CheckAccess() || Application.Current is null)
        {
            return UnregisterPackage(packageId);
        }

        return await _uiDispatcher.InvokeAsync(() => UnregisterPackage(packageId));
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

    private static Control CreateControl(IServiceProvider serviceProvider, Type implementationType)
        => (Control)ActivatorUtilities.CreateInstance(serviceProvider, implementationType);

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

        try
        {
            switch (control.Parent)
            {
                case ContentControl contentParent when ReferenceEquals(contentParent.Content, control):
                    contentParent.Content = null;
                    break;
                case Decorator decorator when ReferenceEquals(decorator.Child, control):
                    decorator.Child = null;
                    break;
                case Panel panel:
                    panel.Children.Remove(control);
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            // A generation may be torn down after its UI thread exits; disposal remains best effort.
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

    private sealed record AppRegisteredPackageView(
        IServiceProvider ServiceProvider,
        Type ImplementationType,
        string PackageId,
        PackageViewRegistration Registration);

    private sealed record AppRegisteredSettingsView(IServiceProvider ServiceProvider, Type ImplementationType, string PackageId);
}

internal sealed record AppPackageSettingsViewNavigationTarget(
    Control View,
    AppPackageSettingsViewCandidate? Candidate);

internal sealed class AppPackageSettingsViewCandidate(
    AppPackageViewRegistry owner,
    string packageId,
    object registration,
    Control view,
    Func<string, bool> isPackageDisabled) : IDisposable
{
    private Control? _displacedView;
    private bool _promoted;
    private bool _completed;

    internal AppPackageViewRegistry Owner { get; } = owner;
    internal string PackageId { get; } = packageId;
    internal object Registration { get; } = registration;
    internal Control View { get; } = view;
    internal Func<string, bool> IsPackageDisabled { get; } = isPackageDisabled;
    internal bool IsCompleted => _completed;
    internal bool IsPromoted => _promoted;

    public bool Promote() => Owner.PromoteSettingsViewCandidate(this);

    internal void MarkPromoted(Control? displacedView)
    {
        _promoted = true;
        _displacedView = displacedView;
    }

    internal bool TryComplete(out Control? controlToDispose)
    {
        if (_completed)
        {
            controlToDispose = null;
            return false;
        }

        _completed = true;
        controlToDispose = _promoted ? _displacedView : View;
        _displacedView = null;
        return true;
    }

    public void Dispose() => Owner.ReleaseSettingsViewCandidate(this);
}
