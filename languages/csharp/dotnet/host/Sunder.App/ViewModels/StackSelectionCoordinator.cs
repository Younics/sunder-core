namespace Sunder.App.ViewModels;

internal sealed class StackSelectionCoordinator(CancellationToken lifetimeToken) : IDisposable
{
    private CancellationTokenSource? _localCancellation;
    private CancellationTokenSource? _registryCancellation;
    private int _localGeneration;
    private int _registryGeneration;
    private bool _disposed;

    public StackSelectionRequest BeginLocal(bool hasSelection)
        => Begin(StackSelectionKind.Local, ref _localCancellation, ref _localGeneration, hasSelection);

    public StackSelectionRequest BeginRegistry(bool hasSelection)
        => Begin(StackSelectionKind.Registry, ref _registryCancellation, ref _registryGeneration, hasSelection);

    public bool IsCurrent(StackSelectionRequest request)
        => !_disposed
           && !request.Token.IsCancellationRequested
           && request.Generation == (request.Kind == StackSelectionKind.Local ? _localGeneration : _registryGeneration);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel(ref _localCancellation);
        Cancel(ref _registryCancellation);
    }

    private StackSelectionRequest Begin(
        StackSelectionKind kind,
        ref CancellationTokenSource? cancellation,
        ref int generation,
        bool hasSelection)
    {
        Cancel(ref cancellation);
        generation++;
        if (!hasSelection || _disposed)
        {
            return new StackSelectionRequest(kind, generation, new CancellationToken(canceled: true));
        }

        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        return new StackSelectionRequest(kind, generation, cancellation.Token);
    }

    private static void Cancel(ref CancellationTokenSource? cancellation)
    {
        var current = cancellation;
        cancellation = null;
        if (current is null)
        {
            return;
        }

        current.Cancel();
        current.Dispose();
    }
}

internal enum StackSelectionKind
{
    Local,
    Registry,
}

internal readonly record struct StackSelectionRequest(
    StackSelectionKind Kind,
    int Generation,
    CancellationToken Token);
