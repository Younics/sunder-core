using Avalonia.Controls;

namespace Sunder.Package.Template.PackageViews;

public partial class DefaultPackageView : UserControl
{
    public DefaultPackageView()
    {
        InitializeComponent();
    }

    public DefaultPackageView(DefaultPackageViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
