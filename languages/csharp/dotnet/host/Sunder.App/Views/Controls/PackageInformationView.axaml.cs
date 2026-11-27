using Avalonia;
using Avalonia.Controls;

namespace Sunder.App.Views.Controls;

public partial class PackageInformationView : UserControl
{
    public static readonly StyledProperty<bool> OverviewOnlyProperty =
        AvaloniaProperty.Register<PackageInformationView, bool>(nameof(OverviewOnly));

    public bool OverviewOnly { get => GetValue(OverviewOnlyProperty); set => SetValue(OverviewOnlyProperty, value); }
    public PackageInformationView() => InitializeComponent();
}
