using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sunder.Package.Template.PackageViews;

public sealed class DefaultPackageView : UserControl
{
    public DefaultPackageView(DefaultPackageViewModel viewModel)
    {
        DataContext = viewModel;

        Content = new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Sunder Package Template",
                        FontSize = 24,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = "Start replacing this view with your package UI.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = $"Package id: {viewModel.PackageId}",
                    },
                }
            }
        };
    }
}
