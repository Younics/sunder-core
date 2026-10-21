using System.Text.Json;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

// Exercises the production Worker SDK through the real Host process boundary.
internal sealed class LifecycleFaultFixture(SunderWorkerContext context)
    : IServiceProvider, IAsyncDisposable, ISunderWorkerLifecycleService, ISunderRpcServiceHandler
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _journal;
    private bool _disposed;

    internal static SunderWorkerOptions Configure(SunderWorkerContext context, SunderRpcContractDescriptor descriptor)
    {
        var fixture = new LifecycleFaultFixture(context);
        return new SunderWorkerOptions([
            new("sunder.interop.dotnet.provider", descriptor.ContractId, descriptor.Version,
                descriptor.Sha256, descriptor.AbiSha256!, fixture),
        ]) { Composition = new SunderWorkerComposition(fixture, [fixture]) };
    }

    public Task Completion => _completion.Task;
    public object? GetService(Type serviceType) => null;
    public ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask CommitGenerationAsync(SunderWorkerGenerationContext generation, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask ActivateAsync(SunderWorkerContext activation, CancellationToken cancellationToken = default)
    {
        _journal = activation.RoleLocalWorkspace.GetLocalPath("lifecycle-cleanup.txt");
        return ValueTask.CompletedTask;
    }

    public async ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext invocation, string serviceId,
        string methodId, JsonElement request, CancellationToken cancellationToken = default)
    {
        _completion.TrySetException(new InvalidOperationException("The SDK integration lifecycle failed."));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return request;
        }
        finally
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await context.Logging.Events.InformationAsync("worker.fault-cleanup", "Handler cleanup is using live services.");
            Record("handler-drained");
        }
    }

    public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext invocation,
        string serviceId, string methodId, JsonElement request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask ShutdownAsync(SunderWorkerShutdownContext shutdown, CancellationToken cancellationToken = default)
    {
        Record("lifecycle-stopped");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Record("services-disposed");
        return ValueTask.CompletedTask;
    }

    private void Record(string value) => File.AppendAllText(_journal!, value + "\n");
}
