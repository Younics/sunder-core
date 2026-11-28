using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace Sunder.App.Views.Controls;

public partial class PackageInformationView : UserControl
{
    public static readonly StyledProperty<bool> OverviewOnlyProperty =
        AvaloniaProperty.Register<PackageInformationView, bool>(nameof(OverviewOnly));
    public static readonly StyledProperty<ICommand?> AccessCommandProperty =
        AvaloniaProperty.Register<PackageInformationView, ICommand?>(nameof(AccessCommand));

    public bool OverviewOnly { get => GetValue(OverviewOnlyProperty); set => SetValue(OverviewOnlyProperty, value); }
    public ICommand? AccessCommand { get => GetValue(AccessCommandProperty); set => SetValue(AccessCommandProperty, value); }
    public PackageInformationView() => InitializeComponent();
}
