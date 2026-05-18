using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Views.Controls;

public partial class ShellWorkspace : UserControl
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private MainWindowViewModel? _attachedViewModel;
    private readonly MainWindowLayoutController _layoutController;

    public ShellWorkspace()
    {
        InitializeComponent();
        _layoutController = new MainWindowLayoutController(
            ShellContentGrid,
            TopContentGrid,
            BottomContentGrid,
            LeftColumnGridSplitter,
            RightColumnGridSplitter,
            BottomRowGridSplitter,
            BottomColumnGridSplitter,
            () => ViewModel);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => ApplyAdaptiveLayout();
        DetachedFromVisualTree += (_, _) => DetachViewModel();
        SizeChanged += (_, _) => ApplyAdaptiveLayout();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();

        _attachedViewModel = DataContext as MainWindowViewModel;
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyAdaptiveLayout();
    }

    private void DetachViewModel()
    {
        if (_attachedViewModel is null)
        {
            return;
        }

        _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _attachedViewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnViewModelPropertyChanged(sender, e));
            return;
        }

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
        => _layoutController.ApplyAdaptiveLayout();
}
