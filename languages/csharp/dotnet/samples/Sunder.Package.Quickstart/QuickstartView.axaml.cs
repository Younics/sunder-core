using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Quickstart;

public partial class QuickstartView : UserControl, IPackageViewNavigationTarget, IPackageViewWarmupTarget, IDisposable
{
    private readonly IPackageContext? _context;
    private readonly IPackageRuntimeClient _runtime;
    private CancellationTokenSource? _requestCancellation;
    private string _defaultName = "Sunder";

    public QuickstartView()
        : this(null, NullPackageRuntimeClient.Instance)
    {
    }

    public QuickstartView(IPackageContext? context, IPackageRuntimeClient runtime)
    {
        _context = context;
        _runtime = runtime;
        InitializeComponent();
    }

    public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    {
        if (_context is null)
        {
            return;
        }

        var configuredName = await _context.Settings.GetValueAsync("default-name", cancellationToken);
        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            _defaultName = configuredName;
        }
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
        if (!_runtime.IsAvailable)
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
            var response = await _runtime.InvokeAsync(
                QuickstartOperations.Greet,
                new GreetRequest(NameInput.Text?.Trim() ?? string.Empty),
                requestCancellation.Token);
            if (ReferenceEquals(_requestCancellation, requestCancellation))
            {
                StatusText.Text = $"{response.Message} Invocation {response.InvocationCount}.";
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
