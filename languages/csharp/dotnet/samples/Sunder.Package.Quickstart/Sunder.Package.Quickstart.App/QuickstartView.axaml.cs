using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.Package.Quickstart.Protocol;
using Sunder.Package.Quickstart.Protocol.Generated;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Quickstart.App;

public partial class QuickstartView : UserControl, IPackageViewNavigationTarget, IPackageViewWarmupTarget, IDisposable
{
    private readonly IPackageContext? _context;
    private readonly ISunderRpcClient? _rpc;
    private CancellationTokenSource? _requestCancellation;
    private string _defaultName = "Sunder";

    public QuickstartView()
    {
        InitializeComponent();
    }

    public QuickstartView(IPackageContext context, ISunderRpcClient rpc)
        : this()
    {
        _context = context;
        _rpc = rpc;
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
        if (_rpc is null)
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
            var descriptor = QuickstartProtocol.Greeting;
            var catalog = await _rpc.DiscoverAsync(descriptor.ContractId, requestCancellation.Token);
            var provider = catalog.Providers.SingleOrDefault(candidate =>
                candidate.ProviderId == "docs.sunder.quickstart.greeting"
                && candidate.State == SunderRpcProviderState.Active
                && candidate.AbiSha256 == descriptor.AbiSha256)
                ?? throw new InvalidOperationException("The Quickstart greeting provider is unavailable.");
            await using var scope = await _rpc.CreateCallScopeAsync(cancellationToken: requestCancellation.Token);
            var client = new GreetingClient(scope, provider.Endpoint);
            var response = await client.CreateAsync(
                new GreetRequest { Name = NameInput.Text?.Trim() ?? string.Empty },
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
