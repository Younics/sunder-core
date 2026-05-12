using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class MainWindow : Window
{
    private const double SplitterThickness = 4;
    private const double MinimumMiddleContentWidth = 320;
    private const double MinimumVerticalRegionRatio = 0.10;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private MainWindowViewModel? _attachedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => ApplyAdaptiveLayout();
        SizeChanged += (_, _) => ApplyAdaptiveLayout();
        Closing += OnClosing;
        KeyDown += MainWindow_OnKeyDown;
        AddHandler(InputElement.PointerPressedEvent, MainWindow_OnPointerPressed, RoutingStrategies.Tunnel, true);

        LeftColumnGridSplitter.DragDelta += LayoutTopColumnSplitter_OnDragDelta;
        LeftColumnGridSplitter.DragCompleted += LayoutSplitter_OnDragCompleted;
        RightColumnGridSplitter.DragDelta += LayoutTopColumnSplitter_OnDragDelta;
        RightColumnGridSplitter.DragCompleted += LayoutSplitter_OnDragCompleted;
        BottomRowGridSplitter.DragDelta += LayoutBottomSplitter_OnDragDelta;
        BottomRowGridSplitter.DragCompleted += LayoutSplitter_OnDragCompleted;
        BottomColumnGridSplitter.DragDelta += LayoutBottomSplitter_OnDragDelta;
        BottomColumnGridSplitter.DragCompleted += LayoutSplitter_OnDragCompleted;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _attachedViewModel = DataContext as MainWindowViewModel;
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyAdaptiveLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.HasLeftTopPanelContent)
            or nameof(MainWindowViewModel.HasRightTopPanelContent)
            or nameof(MainWindowViewModel.HasLeftBottomPanelContent)
            or nameof(MainWindowViewModel.HasRightBottomPanelContent)
            or nameof(MainWindowViewModel.HasAnyBottomPanelContent)
            or nameof(MainWindowViewModel.LeftPanelWidth)
            or nameof(MainWindowViewModel.RightPanelWidth)
            or nameof(MainWindowViewModel.TopRowHeightRatio)
            or nameof(MainWindowViewModel.BottomSplitRatio))
        {
            ApplyAdaptiveLayout();
        }
    }

    private void ApplyAdaptiveLayout()
    {
        if (ViewModel is null)
        {
            return;
        }

        var hasLeftTop = ViewModel.HasLeftTopPanelContent;
        var hasRightTop = ViewModel.HasRightTopPanelContent;
        var hasLeftBottom = ViewModel.HasLeftBottomPanelContent;
        var hasRightBottom = ViewModel.HasRightBottomPanelContent;
        var hasBottom = hasLeftBottom || hasRightBottom;
        var hasBottomSplit = hasLeftBottom && hasRightBottom;

        var leftSplitterWidth = hasLeftTop ? SplitterThickness : 0;
        var rightSplitterWidth = hasRightTop ? SplitterThickness : 0;
        var bottomColumnSplitterWidth = hasBottomSplit ? SplitterThickness : 0;

        var topWidths = CalculateTopColumnWidths(
            GetTopContentWidth(),
            ViewModel.LeftPanelWidth,
            ViewModel.RightPanelWidth,
            hasLeftTop,
            hasRightTop,
            leftSplitterWidth,
            rightSplitterWidth);
        var verticalWeights = CalculateVerticalWeights(ViewModel.TopRowHeightRatio, hasBottom);
        var bottomWeights = CalculateBottomColumnWeights(ViewModel.BottomSplitRatio, hasLeftBottom, hasRightBottom);

        ShellContentGrid.RowDefinitions[0].Height = ToStarLength(verticalWeights.TopWeight);
        ShellContentGrid.RowDefinitions[1].Height = new GridLength(verticalWeights.SplitterHeight);
        ShellContentGrid.RowDefinitions[2].Height = ToStarLength(verticalWeights.BottomWeight);

        TopContentGrid.ColumnDefinitions[0].Width = ToPixelLength(topWidths.LeftWidth);
        TopContentGrid.ColumnDefinitions[1].Width = new GridLength(leftSplitterWidth);
        TopContentGrid.ColumnDefinitions[2].Width = ToStarLength(1);
        TopContentGrid.ColumnDefinitions[3].Width = new GridLength(rightSplitterWidth);
        TopContentGrid.ColumnDefinitions[4].Width = ToPixelLength(topWidths.RightWidth);

        BottomContentGrid.ColumnDefinitions[0].Width = ToStarLength(bottomWeights.LeftWeight);
        BottomContentGrid.ColumnDefinitions[1].Width = new GridLength(bottomColumnSplitterWidth);
        BottomContentGrid.ColumnDefinitions[2].Width = ToStarLength(bottomWeights.RightWeight);

        LeftColumnGridSplitter.IsVisible = hasLeftTop;
        RightColumnGridSplitter.IsVisible = hasRightTop;
        BottomRowGridSplitter.IsVisible = hasBottom;
        BottomColumnGridSplitter.IsVisible = hasBottomSplit;
    }

    internal static (double LeftWidth, double RightWidth) CalculateTopColumnWidths(
        double totalWidth,
        double requestedLeftWidth,
        double requestedRightWidth,
        bool hasLeftPanel,
        bool hasRightPanel,
        double leftSplitterWidth = SplitterThickness,
        double rightSplitterWidth = SplitterThickness)
    {
        if (!hasLeftPanel && !hasRightPanel)
        {
            return (0, 0);
        }

        var leftWidth = hasLeftPanel ? Math.Max(0, requestedLeftWidth) : 0;
        var rightWidth = hasRightPanel ? Math.Max(0, requestedRightWidth) : 0;
        if (totalWidth <= 0)
        {
            return (leftWidth, rightWidth);
        }

        var fixedWidth = (hasLeftPanel ? leftSplitterWidth : 0) + (hasRightPanel ? rightSplitterWidth : 0);
        var resizableWidth = Math.Max(0, totalWidth - fixedWidth);
        var maximumSideWidth = Math.Max(0, resizableWidth - MinimumMiddleContentWidth);
        var requestedSideWidth = leftWidth + rightWidth;
        if (requestedSideWidth <= maximumSideWidth || requestedSideWidth <= 0)
        {
            return (leftWidth, rightWidth);
        }

        var scale = maximumSideWidth / requestedSideWidth;
        return (leftWidth * scale, rightWidth * scale);
    }

    private static (double TopWeight, double SplitterHeight, double BottomWeight) CalculateVerticalWeights(
        double requestedTopRatio,
        bool hasBottom)
    {
        if (!hasBottom)
        {
            return (1, 0, 0);
        }

        var topWeight = Math.Clamp(requestedTopRatio, MinimumVerticalRegionRatio, 1 - MinimumVerticalRegionRatio);
        return (topWeight, SplitterThickness, 1 - topWeight);
    }

    private static (double LeftWeight, double RightWeight) CalculateBottomColumnWeights(
        double requestedLeftRatio,
        bool hasLeftBottom,
        bool hasRightBottom)
    {
        if (hasLeftBottom && hasRightBottom)
        {
            var leftWeight = Math.Clamp(requestedLeftRatio, 0.01, 0.99);
            return (leftWeight, 1 - leftWeight);
        }

        if (hasLeftBottom)
        {
            return (1, 0);
        }

        if (hasRightBottom)
        {
            return (0, 1);
        }

        return (0, 0);
    }

    private static GridLength ToStarLength(double weight)
    {
        if (weight <= 0)
        {
            return new GridLength(0);
        }

        return new GridLength(weight, GridUnitType.Star);
    }

    private static GridLength ToPixelLength(double width) => new(Math.Max(0, width));

    internal static double CalculateResizableExtent(double totalExtent, params double[] fixedExtents)
    {
        var resizableExtent = totalExtent;
        foreach (var fixedExtent in fixedExtents)
        {
            if (fixedExtent > 0)
            {
                resizableExtent -= fixedExtent;
            }
        }

        return Math.Max(0, resizableExtent);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ViewModel?.Dispose();
    }

    private void LayoutSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        ViewModel?.CommitLayoutState();
        ApplyAdaptiveLayout();
    }

    private void LayoutBottomSplitter_OnDragDelta(object? sender, VectorEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ReferenceEquals(sender, BottomRowGridSplitter))
        {
            var resizableHeight = CalculateResizableExtent(
                ShellContentGrid.Bounds.Height,
                BottomRowGridSplitter.IsVisible ? SplitterThickness : 0);
            if (resizableHeight <= 0)
            {
                return;
            }

            ViewModel.AdjustLiveTopRowHeightRatio(e.Vector.Y / resizableHeight);
        }
        else if (ReferenceEquals(sender, BottomColumnGridSplitter))
        {
            var resizableWidth = CalculateResizableExtent(
                BottomContentGrid.Bounds.Width,
                BottomColumnGridSplitter.IsVisible ? SplitterThickness : 0);
            if (resizableWidth <= 0)
            {
                return;
            }

            ViewModel.AdjustLiveBottomSplitRatio(e.Vector.X / resizableWidth);
        }

        ApplyAdaptiveLayout();
    }

    private void LayoutTopColumnSplitter_OnDragDelta(object? sender, VectorEventArgs e)
    {
        if (ViewModel is null || GetTopContentWidth() <= 0)
        {
            return;
        }

        if (ReferenceEquals(sender, LeftColumnGridSplitter))
        {
            ViewModel.AdjustLiveLeftPanelWidth(e.Vector.X, GetMaximumLeftPanelWidth());
        }
        else if (ReferenceEquals(sender, RightColumnGridSplitter))
        {
            ViewModel.AdjustLiveRightPanelWidth(-e.Vector.X, GetMaximumRightPanelWidth());
        }

        ApplyAdaptiveLayout();
    }

    private double GetTopContentWidth()
        => TopContentGrid.Bounds.Width > 0 ? TopContentGrid.Bounds.Width : ShellContentGrid.Bounds.Width;

    private double GetMaximumLeftPanelWidth()
    {
        if (ViewModel is null)
        {
            return double.PositiveInfinity;
        }

        var resizableWidth = CalculateResizableExtent(
            GetTopContentWidth(),
            ViewModel.HasLeftTopPanelContent ? SplitterThickness : 0,
            ViewModel.HasRightTopPanelContent ? SplitterThickness : 0);
        return resizableWidth - MinimumMiddleContentWidth - (ViewModel.HasRightTopPanelContent ? ViewModel.RightPanelWidth : 0);
    }

    private double GetMaximumRightPanelWidth()
    {
        if (ViewModel is null)
        {
            return double.PositiveInfinity;
        }

        var resizableWidth = CalculateResizableExtent(
            GetTopContentWidth(),
            ViewModel.HasLeftTopPanelContent ? SplitterThickness : 0,
            ViewModel.HasRightTopPanelContent ? SplitterThickness : 0);
        return resizableWidth - MinimumMiddleContentWidth - (ViewModel.HasLeftTopPanelContent ? ViewModel.LeftPanelWidth : 0);
    }

    private void ToolbarDragHost_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Source is Visual visual)
        {
            if (!ReferenceEquals(TopLevel.GetTopLevel(visual), this))
            {
                return;
            }

            var ancestors = visual.GetSelfAndVisualAncestors().OfType<StyledElement>().ToArray();
            if (ancestors.Any(x => x is Button or TextBox or Menu or MenuItem))
            {
                return;
            }
        }

        if (!e.GetCurrentPoint(ToolbarDragHost).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizedState();
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximizedState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void MoreActionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ShowToolbarMainMenu();
        e.Handled = true;
    }

    private void ShowToolbarMainMenu()
    {
        ToolbarMainMenu.ItemsSource = BuildToolbarMainMenuItems();
        ToolbarDefaultActions.IsVisible = false;
        MiddlePackageIconBar.IsVisible = false;
        ToolbarMainMenu.IsVisible = true;
        ToolbarMainMenu.Focus();
    }

    private void HideToolbarMainMenu()
    {
        if (!ToolbarMainMenu.IsVisible)
        {
            return;
        }

        ToolbarMainMenu.IsVisible = false;
        ToolbarMainMenu.ItemsSource = null;
        MiddlePackageIconBar.IsVisible = true;
        ToolbarDefaultActions.IsVisible = true;
    }

    private object[] BuildToolbarMainMenuItems()
    {
        var packageMenuGroups = ViewModel?.GetPackageViewGroups() ?? Array.Empty<PackageViewMenuGroup>();
        var packagesMenu = CreateToolbarMenuItem("Packages");
        packagesMenu.ItemsSource = packageMenuGroups.Select(BuildPackageGroupMenuItem).ToArray();

        var viewMenu = new MenuItem { Header = "View", Classes = { "toolbar-menu-root-item" } };
        viewMenu.ItemsSource = new object[] { packagesMenu };
        viewMenu.PointerEntered += (_, _) => viewMenu.IsSubMenuOpen = true;

        return new object[] { viewMenu };
    }

    private void MainWindow_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ToolbarMainMenu.IsVisible)
        {
            return;
        }

        if (e.Source is not Visual visual)
        {
            return;
        }

        if (!ReferenceEquals(TopLevel.GetTopLevel(visual), this))
        {
            return;
        }

        var ancestors = visual.GetSelfAndVisualAncestors().OfType<StyledElement>().ToArray();
        if (ancestors.Contains(ToolbarLeftMenuHost) || ancestors.Any(x => x is Menu or MenuItem))
        {
            return;
        }

        Dispatcher.UIThread.Post(HideToolbarMainMenu);
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ToolbarMainMenu.IsVisible && e.Key == Key.Escape)
        {
            HideToolbarMainMenu();
            e.Handled = true;
        }
    }

    private void NotificationsButton_OnClick(object? sender, RoutedEventArgs e)
        => ViewModel?.MarkNotificationsRead();

    private MenuItem BuildPackageGroupMenuItem(PackageViewMenuGroup group)
    {
        var menuItem = CreateToolbarMenuItem(group.PackageDisplayName, group.PackageGlyph, group.PackageIconUri);
        menuItem.ItemsSource = group.Views.Select(BuildPackageViewMenuItem).ToArray();
        return menuItem;
    }

    private MenuItem BuildPackageViewMenuItem(PackageViewMenuItem item)
    {
        var menuItem = CreateToolbarMenuItem(item.Title, item.Glyph, item.IconUri);
        menuItem.IsEnabled = !item.IsInHotbar;
        if (!item.IsInHotbar)
        {
            menuItem.Click += async (_, _) =>
            {
                HideToolbarMainMenu();
                if (ViewModel is not null)
                {
                    await ViewModel.OpenPackageViewPanelAsync(item.ViewId);
                }
            };
        }

        return menuItem;
    }

    private static MenuItem CreateToolbarMenuItem(string header, string? glyph = null, Uri? iconUri = null)
        => new() { Header = CreateToolbarMenuHeader(header, glyph, iconUri), Classes = { "toolbar-menu-item" } };

    private static object CreateToolbarMenuHeader(string header, string? glyph, Uri? iconUri)
    {
        if (string.IsNullOrWhiteSpace(glyph) && iconUri is null)
        {
            return header;
        }

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconImage = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            IsVisible = false,
        };
        var glyphText = new TextBlock
        {
            Text = glyph,
            Classes = { "toolbar-menu-icon-text" },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = !string.IsNullOrWhiteSpace(glyph),
        };
        var iconContent = new Grid();
        iconContent.Children.Add(iconImage);
        iconContent.Children.Add(glyphText);
        content.Children.Add(new Border
        {
            Classes = { "toolbar-menu-icon-badge" },
            Child = iconContent,
        });
        content.Children.Add(new TextBlock
        {
            Text = header,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (iconUri is not null)
        {
            _ = LoadToolbarMenuIconAsync(iconImage, glyphText, iconUri);
        }

        return content;
    }

    private static async Task LoadToolbarMenuIconAsync(Image iconImage, TextBlock glyphText, Uri iconUri)
    {
        var result = await PackageIconImageLoader.LoadAsync(iconUri);
        if (result.Image is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            iconImage.Source = result.Image;
            iconImage.IsVisible = true;
            glyphText.IsVisible = false;
        });
    }

    public void ShowPackageDragGhost(ShellItemViewModel item, bool compact, Point centerPosition)
    {
        PackageDragGhostIcon.Source = item.IconImage;
        PackageDragGhostIcon.IsVisible = item.HasIconImage;
        PackageDragGhostGlyph.Text = item.Glyph;
        PackageDragGhostGlyph.IsVisible = item.ShowGlyphFallback;
        PackageDragGhost.Classes.Set("top-bar-surface", compact);
        PackageDragGhost.IsVisible = true;
        MovePackageDragGhost(centerPosition);
    }

    public void MovePackageDragGhost(Point centerPosition)
    {
        if (!PackageDragGhost.IsVisible)
        {
            return;
        }

        var width = PackageDragGhost.Bounds.Width > 0 ? PackageDragGhost.Bounds.Width : (PackageDragGhost.Classes.Contains("top-bar-surface") ? 36 : 38);
        var height = PackageDragGhost.Bounds.Height > 0 ? PackageDragGhost.Bounds.Height : (PackageDragGhost.Classes.Contains("top-bar-surface") ? 36 : 38);
        Canvas.SetLeft(PackageDragGhost, centerPosition.X - width / 2);
        Canvas.SetTop(PackageDragGhost, centerPosition.Y - height / 2);
    }

    public void HidePackageDragGhost()
    {
        PackageDragGhost.IsVisible = false;
        PackageDragGhostIcon.Source = null;
        PackageDragGhostGlyph.Text = string.Empty;
    }
}
