using CommunityToolkit.Mvvm.ComponentModel;

namespace Sunder.App.ViewModels;

public sealed partial class ShellHostedViewViewModel(string viewId, object view) : ViewModelBase
{
    public string ViewId { get; } = viewId;

    [ObservableProperty]
    private object _view = view;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLayoutVisible))]
    [NotifyPropertyChangedFor(nameof(PresentationOpacity))]
    [NotifyPropertyChangedFor(nameof(IsPresentationHitTestVisible))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLayoutVisible))]
    private bool _isStaged;

    public bool IsLayoutVisible => IsActive || IsStaged;

    public double PresentationOpacity => IsActive ? 1 : 0;

    public bool IsPresentationHitTestVisible => IsActive;
}
