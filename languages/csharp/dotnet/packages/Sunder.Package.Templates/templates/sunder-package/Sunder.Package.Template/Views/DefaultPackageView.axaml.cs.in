using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Template.Views;

[SunderPackageView("SUNDER_PACKAGE_ID_CSHARP.default", "SUNDER_PACKAGE_NAME_CSHARP", ViewModel = typeof(DefaultPackageViewModel), IconPath = "assets/icon.png")]
public partial class DefaultPackageView : UserControl, IDisposable
{
    private CancellationTokenSource? _greeting;
    public DefaultPackageView()
    {
        InitializeComponent();
    }

    private async void GreetOnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DefaultPackageViewModel model) return;
        _greeting?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _greeting = cancellation;
        try
        {
            StatusText.Text = "Creating greeting...";
            var message = await model.GreetAsync(NameInput.Text ?? string.Empty, cancellation.Token);
            if (ReferenceEquals(_greeting, cancellation)) StatusText.Text = message;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_greeting, cancellation)) StatusText.Text = "Greeting cancelled.";
        }
        catch (Exception)
        {
            if (ReferenceEquals(_greeting, cancellation)) StatusText.Text = "The greeting failed. Check package logs.";
        }
        finally
        {
            if (ReferenceEquals(_greeting, cancellation)) _greeting = null;
        }
    }

    public void Dispose()
    {
        var cancellation = _greeting;
        _greeting = null;
        cancellation?.Cancel();
    }
}
