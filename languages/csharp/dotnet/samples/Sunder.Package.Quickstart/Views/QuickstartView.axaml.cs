using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Quickstart.Views;

[SunderPackageView("docs.sunder.quickstart.main", "Sunder Quickstart", ViewModel = typeof(QuickstartViewModel))]
public partial class QuickstartView : UserControl, IPackageViewNavigationTarget, IPackageViewWarmupTarget, IDisposable
{
    private CancellationTokenSource? _requestCancellation;
    private string _defaultName = "Sunder";

    public QuickstartView()
    {
        InitializeComponent();
    }

    public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    {
        if (DataContext is QuickstartViewModel model)
            _defaultName = await model.GetDefaultNameAsync(cancellationToken);
    }

    public ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NameInput.Text = context.Parameters.TryGetValue("name", out var name)
                         && !string.IsNullOrWhiteSpace(name)
            ? name
            : _defaultName;
        return ValueTask.CompletedTask;
    }

    private async void GreetOnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QuickstartViewModel model)
        {
            StatusText.Text = "Runtime is unavailable.";
            return;
        }

        _requestCancellation?.Cancel();
        var requestCancellation = new CancellationTokenSource();
        _requestCancellation = requestCancellation;

        try
        {
            StatusText.Text = "Creating greeting...";
            var message = await model.GreetAsync(NameInput.Text?.Trim() ?? string.Empty, requestCancellation.Token);
            if (ReferenceEquals(_requestCancellation, requestCancellation))
            {
                StatusText.Text = message;
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_requestCancellation, requestCancellation))
            {
                StatusText.Text = "Greeting cancelled.";
            }
        }
        catch (Exception)
        {
            if (ReferenceEquals(_requestCancellation, requestCancellation))
            {
                StatusText.Text = "The greeting failed. Check package logs.";
            }
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, requestCancellation))
            {
                _requestCancellation = null;
            }
            requestCancellation.Dispose();
        }
    }

    public void Dispose()
    {
        var requestCancellation = _requestCancellation;
        _requestCancellation = null;
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
    }
}
