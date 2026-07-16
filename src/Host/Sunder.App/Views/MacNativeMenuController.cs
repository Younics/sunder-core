using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sunder.App.Features.Shell.Menus;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

internal sealed class MacNativeMenuController : IDisposable
{
    private const int NativeMenuIconSize = 16;
    private static readonly Uri DefaultIconUri = new("avares://Sunder.App/Assets/Images/icon.png");
    private readonly Window _window;
    private readonly Func<MainWindowViewModel?> _viewModelAccessor;
    private readonly OwnedTaskObserver _tasks = new("macOS native menu");
    private readonly Dictionary<Bitmap, Bitmap> _scaledIcons = new(ReferenceEqualityComparer.Instance);
    private NativeMenu? _rootMenu;
    private Bitmap? _defaultIcon;
    private MainWindowViewModel? _subscribedViewModel;
    private bool _menuDirty = true;
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
        _tasks.Dispose();
        _window.DataContextChanged -= Window_OnDataContextChanged;
        _window.Opened -= Window_OnOpened;
        _window.Activated -= Window_OnActivated;
        UnsubscribeFromViewModel();
        if (_rootMenu is not null)
        {
            _rootMenu.NeedsUpdate -= Menu_OnNeedsUpdate;
            _rootMenu.Items.Clear();
            _rootMenu = null;
        }

        foreach (var icon in _scaledIcons.Values)
        {
            icon.Dispose();
        }
        _scaledIcons.Clear();
        _defaultIcon?.Dispose();
        _defaultIcon = null;
    }

    private void Window_OnOpened(object? sender, EventArgs e) => AttachMenu();

    private void Window_OnActivated(object? sender, EventArgs e) => AttachMenu();

    private void Window_OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeToCurrentViewModel();
        _menuDirty = true;
        AttachMenu();
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
        UpdateMenu();
        NativeMenu.SetMenu(_window, _rootMenu);
    }

    private void Menu_OnNeedsUpdate(object? sender, EventArgs e) => UpdateMenuIfDirty();

    private void UpdateMenu()
    {
        if (_disposed || _rootMenu is null)
        {
            return;
        }

        _menuDirty = false;
        _rootMenu.Items.Clear();
        foreach (var item in _viewModelAccessor()?.GetMainMenuItems() ?? [])
        {
            _rootMenu.Add(BuildNativeItem(item));
        }
    }

    private NativeMenuItem BuildNativeItem(ShellMenuItem item)
    {
        var nativeItem = new NativeMenuItem
        {
            Header = item.Title,
            Icon = ResolveMenuIcon(item.IconImage),
            IsEnabled = item.IsEnabled,
        };
        if (item.Children.Count > 0)
        {
            var submenu = new NativeMenu();
            foreach (var child in item.Children)
            {
                submenu.Add(BuildNativeItem(child));
            }
            nativeItem.Menu = submenu;
        }
        else if (item.ExecuteAsync is not null)
        {
            nativeItem.Click += (_, _) =>
                _tasks.Run(item.ExecuteAsync, $"executing menu command '{item.Id}'");
        }

        return nativeItem;
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

    private void ViewModel_OnShellViewStateChanged() => _menuDirty = true;

    private void UpdateMenuIfDirty()
    {
        if (_menuDirty)
        {
            UpdateMenu();
        }
    }

    private Bitmap ResolveMenuIcon(IImage? iconImage)
    {
        if (iconImage is not Bitmap bitmap)
        {
            return DefaultIcon;
        }

        var sourceSize = bitmap.PixelSize;
        if (sourceSize.Width <= NativeMenuIconSize && sourceSize.Height <= NativeMenuIconSize)
        {
            return bitmap;
        }

        if (!_scaledIcons.TryGetValue(bitmap, out var scaled))
        {
            var scale = Math.Min(
                (double)NativeMenuIconSize / sourceSize.Width,
                (double)NativeMenuIconSize / sourceSize.Height);
            scaled = bitmap.CreateScaledBitmap(
                new PixelSize(
                    Math.Max(1, (int)Math.Round(sourceSize.Width * scale)),
                    Math.Max(1, (int)Math.Round(sourceSize.Height * scale))),
                BitmapInterpolationMode.HighQuality);
            _scaledIcons.Add(bitmap, scaled);
        }

        return scaled;
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
            return _defaultIcon = Bitmap.DecodeToWidth(
                stream,
                NativeMenuIconSize,
                BitmapInterpolationMode.HighQuality);
        }
    }
}
