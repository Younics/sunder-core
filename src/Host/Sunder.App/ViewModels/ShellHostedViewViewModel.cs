using CommunityToolkit.Mvvm.ComponentModel;

namespace Sunder.App.ViewModels;

public sealed partial class ShellHostedViewViewModel(string viewId, object view) : ViewModelBase
{
    public string ViewId { get; } = viewId;

    [ObservableProperty]
    private object _view = view;

    [ObservableProperty]
    private bool _isActive;
}
