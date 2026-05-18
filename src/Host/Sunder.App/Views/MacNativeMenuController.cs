using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Sunder.App.Features.Shell.Menus;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

internal sealed class MacNativeMenuController : IDisposable
{
    private static readonly Uri DefaultIconUri = new("avares://Sunder.App/Assets/Images/icon.png");

    private readonly Window _window;
    private readonly Func<MainWindowViewModel?> _viewModelAccessor;
    private readonly Dictionary<string, Bitmap> _iconCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingIconKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackageMenuEntry> _packageEntries = new(StringComparer.OrdinalIgnoreCase);
    private NativeMenu? _rootMenu;
    private NativeMenu? _viewSubmenu;
    private NativeMenu? _packagesSubmenu;
    private NativeMenu? _developerSubmenu;
    private NativeMenuItem? _packagesMenu;
    private NativeMenuItem? _developerLogsItem;
    private Bitmap? _defaultIcon;
    private MainWindowViewModel? _subscribedViewModel;
    private bool _packagesMenuDirty = true;
    private bool _packagesMenuRefreshScheduled;
    private bool _disposed;

    public MacNativeMenuController(Window window, Func<MainWindowViewModel?> viewModelAccessor)
    {
        _window = window;
        _viewModelAccessor = viewModelAccessor;

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        _window.DataContextChanged += Window_OnDataContextChanged;
        _window.Opened += Window_OnOpened;
        _window.Activated += Window_OnActivated;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.DataContextChanged -= Window_OnDataContextChanged;
        _window.Opened -= Window_OnOpened;
        _window.Activated -= Window_OnActivated;
        UnsubscribeFromViewModel();

        if (_rootMenu is not null)
        {
            _rootMenu.NeedsUpdate -= Menu_OnNeedsUpdate;
            _rootMenu = null;
        }

        if (_viewSubmenu is not null)
        {
            _viewSubmenu.NeedsUpdate -= Menu_OnNeedsUpdate;
            _viewSubmenu = null;
        }

        if (_packagesSubmenu is not null)
        {
            _packagesSubmenu.NeedsUpdate -= Menu_OnNeedsUpdate;
            _packagesSubmenu = null;
        }

        if (_developerLogsItem is not null)
        {
            _developerLogsItem.Click -= DeveloperLogsItem_OnClick;
            _developerLogsItem = null;
        }

        _developerSubmenu = null;

        _packagesMenu = null;
        _packageEntries.Clear();
        DisposeCachedIcons();
    }

    private void Window_OnOpened(object? sender, EventArgs e)
        => AttachMenu();

    private void Window_OnActivated(object? sender, EventArgs e)
        => AttachMenu();

    private void Window_OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeToCurrentViewModel();
        SchedulePackagesMenuRefresh();
        if (_rootMenu is null)
        {
            AttachMenu();
        }
    }

    private void AttachMenu()
    {
        if (_disposed || !OperatingSystem.IsMacOS() || _rootMenu is not null || _viewModelAccessor() is null)
        {
            return;
        }

        SubscribeToCurrentViewModel();

        _rootMenu = new NativeMenu();
        _rootMenu.NeedsUpdate += Menu_OnNeedsUpdate;

        _viewSubmenu = new NativeMenu();
        _viewSubmenu.NeedsUpdate += Menu_OnNeedsUpdate;
        var viewMenu = new NativeMenuItem { Header = "View", Menu = _viewSubmenu };

        _packagesSubmenu = new NativeMenu();
        _packagesSubmenu.NeedsUpdate += Menu_OnNeedsUpdate;
        _packagesMenu = new NativeMenuItem { Header = "Packages", Menu = _packagesSubmenu };

        _viewSubmenu.Add(_packagesMenu);
        _rootMenu.Add(viewMenu);
        if (_viewModelAccessor()?.IsDeveloperMode == true)
        {
            _developerSubmenu = new NativeMenu();
            _developerLogsItem = new NativeMenuItem { Header = "Logs" };
            _developerLogsItem.Click += DeveloperLogsItem_OnClick;
            _developerSubmenu.Add(_developerLogsItem);
            _rootMenu.Add(new NativeMenuItem { Header = "Developer", Menu = _developerSubmenu });
        }

        UpdatePackagesMenu();
        NativeMenu.SetMenu(_window, _rootMenu);
    }

    private void DeveloperLogsItem_OnClick(object? sender, EventArgs e)
        => _viewModelAccessor()?.OpenDeveloperLogsCommand.Execute(null);

    private void Menu_OnNeedsUpdate(object? sender, EventArgs e)
    {
        UpdatePackagesMenuIfDirty();
    }

    private void UpdatePackagesMenu()
    {
        if (_disposed || _packagesSubmenu is null)
        {
            return;
        }

        _packagesMenuDirty = false;
        var viewModel = _viewModelAccessor();
        if (viewModel is null)
        {
            HideAllPackageEntries();
            EnsureNoPackageViewsItem(isVisible: true);
            return;
        }

        var packageGroups = viewModel.GetPackageViewGroups();
        if (packageGroups.Count == 0)
        {
            HideAllPackageEntries();
            EnsureNoPackageViewsItem(isVisible: true);
            return;
        }

        EnsureNoPackageViewsItem(isVisible: false);
        var visiblePackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in packageGroups)
        {
            visiblePackageIds.Add(group.PackageId);
            var entry = GetOrCreatePackageEntry(group.PackageId);
            entry.Item.Header = group.PackageDisplayName;
            entry.Item.Icon = ResolveMenuIcon(group.PackageIconUri);
            entry.Item.IsVisible = true;
            UpdatePackageViewEntries(entry, viewModel, group.Views);
        }

        foreach (var entry in _packageEntries.Values)
        {
            if (!visiblePackageIds.Contains(entry.PackageId))
            {
                entry.Item.IsVisible = false;
            }
        }
    }

    private void UpdatePackageViewEntries(
        PackageMenuEntry packageEntry,
        MainWindowViewModel viewModel,
        IReadOnlyList<PackageViewMenuItem> packageViews)
    {
        var visibleViewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageView in packageViews)
        {
            visibleViewIds.Add(packageView.ViewId);
            var viewItem = GetOrCreatePackageViewItem(packageEntry, packageView.ViewId);
            viewItem.Header = packageView.Title;
            viewItem.Icon = ResolveMenuIcon(packageView.IconUri);
            viewItem.IsEnabled = !viewModel.IsViewInHotbar(packageView.ViewId);
            viewItem.IsVisible = true;
        }

        foreach (var viewEntry in packageEntry.ViewItems)
        {
            if (!visibleViewIds.Contains(viewEntry.Key))
            {
                viewEntry.Value.IsVisible = false;
            }
        }
    }

    private PackageMenuEntry GetOrCreatePackageEntry(string packageId)
    {
        if (_packageEntries.TryGetValue(packageId, out var entry))
        {
            return entry;
        }

        var packageMenu = new NativeMenu();
        var packageItem = new NativeMenuItem
        {
            Menu = packageMenu,
            IsVisible = false,
        };
        entry = new PackageMenuEntry(packageId, packageItem, packageMenu);
        _packageEntries.Add(packageId, entry);
        _packagesSubmenu?.Add(packageItem);
        return entry;
    }

    private NativeMenuItem GetOrCreatePackageViewItem(PackageMenuEntry packageEntry, string viewId)
    {
        if (packageEntry.ViewItems.TryGetValue(viewId, out var viewItem))
        {
            return viewItem;
        }

        viewItem = new NativeMenuItem { IsVisible = false };
        viewItem.Click += async (_, _) =>
        {
            var viewModel = _viewModelAccessor();
            if (viewModel is not null && await viewModel.OpenPackageViewPanelAsync(viewId))
            {
                SchedulePackagesMenuRefresh();
            }
        };
        packageEntry.ViewItems.Add(viewId, viewItem);
        packageEntry.Menu.Add(viewItem);
        return viewItem;
    }

    private void EnsureNoPackageViewsItem(bool isVisible)
    {
        var entry = GetOrCreatePackageEntry("__empty__");
        entry.Item.Header = "No package views";
        entry.Item.Icon = DefaultIcon;
        entry.Item.IsEnabled = false;
        entry.Item.IsVisible = isVisible;
    }

    private void HideAllPackageEntries()
    {
        foreach (var entry in _packageEntries.Values)
        {
            entry.Item.IsVisible = false;
        }
    }

    private void SubscribeToCurrentViewModel()
    {
        var viewModel = _viewModelAccessor();
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        UnsubscribeFromViewModel();
        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ShellViewStateChanged += ViewModel_OnShellViewStateChanged;
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is null)
        {
            return;
        }

        _subscribedViewModel.ShellViewStateChanged -= ViewModel_OnShellViewStateChanged;
        _subscribedViewModel = null;
    }

    private void ViewModel_OnShellViewStateChanged()
        => SchedulePackagesMenuRefresh();

    private void MarkPackagesMenuDirty()
    {
        _packagesMenuDirty = true;
    }

    private void SchedulePackagesMenuRefresh()
    {
        MarkPackagesMenuDirty();
        if (_disposed || _rootMenu is null || _packagesMenuRefreshScheduled)
        {
            return;
        }

        _packagesMenuRefreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _packagesMenuRefreshScheduled = false;
            UpdatePackagesMenuIfDirty();
        }, DispatcherPriority.Background);
    }

    private void UpdatePackagesMenuIfDirty()
    {
        if (_packagesMenuDirty)
        {
            UpdatePackagesMenu();
        }
    }

    private Bitmap ResolveMenuIcon(Uri? iconUri)
    {
        if (iconUri is null)
        {
            return DefaultIcon;
        }

        var key = iconUri.AbsoluteUri;
        if (_iconCache.TryGetValue(key, out var cachedIcon))
        {
            return cachedIcon;
        }

        StartIconLoad(iconUri, key);
        return DefaultIcon;
    }

    private Bitmap DefaultIcon
    {
        get
        {
            if (_defaultIcon is not null)
            {
                return _defaultIcon;
            }

            using var stream = AssetLoader.Open(DefaultIconUri);
            _defaultIcon = new Bitmap(stream);
            return _defaultIcon;
        }
    }

    private void StartIconLoad(Uri iconUri, string key)
    {
        if (_iconCache.ContainsKey(key) || !_loadingIconKeys.Add(key))
        {
            return;
        }

        _ = LoadIconAsync(iconUri, key);
    }

    private async Task LoadIconAsync(Uri iconUri, string key)
    {
        Bitmap? bitmap = null;
        try
        {
            var result = await PackageIconImageLoader.LoadAsync(iconUri).ConfigureAwait(false);
            if (result.Image is Bitmap loadedBitmap)
            {
                bitmap = loadedBitmap;
            }
            else
            {
                PackageIconImageViewModelLoader.DisposeImage(result.Image);
            }
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError($"Failed to load native menu icon '{iconUri}': {ex.Message}", ex);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _loadingIconKeys.Remove(key);
            if (_disposed)
            {
                bitmap?.Dispose();
                return;
            }

            _iconCache[key] = bitmap ?? DefaultIcon;
            SchedulePackagesMenuRefresh();
        });
    }

    private void DisposeCachedIcons()
    {
        var defaultIcon = _defaultIcon;
        foreach (var icon in _iconCache.Values.Distinct())
        {
            if (!ReferenceEquals(icon, defaultIcon))
            {
                icon.Dispose();
            }
        }

        _iconCache.Clear();
        _loadingIconKeys.Clear();
        defaultIcon?.Dispose();
        _defaultIcon = null;
    }

    private sealed class PackageMenuEntry(string packageId, NativeMenuItem item, NativeMenu menu)
    {
        public string PackageId { get; } = packageId;

        public NativeMenuItem Item { get; } = item;

        public NativeMenu Menu { get; } = menu;

        public Dictionary<string, NativeMenuItem> ViewItems { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
