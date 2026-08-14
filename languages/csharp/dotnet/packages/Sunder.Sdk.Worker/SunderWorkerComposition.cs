using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Worker;

/// <summary>Owns package-local services and lifecycle aggregation for one Worker V3 activation.</summary>
/// <remarks>The package constructs its own service provider; the Host never composes package DI over IPC.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.BackgroundServicesV1)]
[SunderSdkCapability(SunderSdkCapabilities.RuntimeGenerationsV1)]
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV3)]
public sealed class SunderWorkerComposition : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<ISunderWorkerLifecycleService> _lifecycleServices;
    private readonly List<ISunderWorkerLifecycleService> _preparedServices = [];
    private SunderWorkerGenerationContext? _generation;
    private Task? _candidatePreparationTask;
    private Task? _commitTask;
    private Task? _activationTask;
    private Task? _shutdownTask;
    private Task _completion = Task.CompletedTask;
    private bool _candidatePrepared;
    private bool _bound;

    /// <summary>Creates a composition that owns the supplied service provider and ordered lifecycle services.</summary>
    public SunderWorkerComposition(
        IServiceProvider services,
        IEnumerable<ISunderWorkerLifecycleService>? lifecycleServices = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        var snapshot = lifecycleServices?.ToArray() ?? [];
        if (snapshot.Any(static service => service is null))
        {
            throw new ArgumentException(
                "Worker lifecycle services cannot contain null.",
                nameof(lifecycleServices));
        }
        if (snapshot.Distinct(ReferenceEqualityComparer.Instance).Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "A worker lifecycle service instance cannot be registered more than once.",
                nameof(lifecycleServices));
        }
        _lifecycleServices = snapshot;
    }

    /// <summary>Gets the package-owned service provider.</summary>
    public IServiceProvider Services { get; }

    internal Task Completion
    {
        get
        {
            lock (_gate) return _completion;
        }
    }

    internal void Bind()
    {
        lock (_gate)
        {
            if (_bound)
            {
                throw new InvalidOperationException(
                    "A worker composition can be bound to only one Worker V3 activation.");
            }
            _bound = true;
        }
    }

    internal Task PrepareCandidateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_shutdownTask is not null)
            {
                throw new ObjectDisposedException(nameof(SunderWorkerComposition));
            }
            return _candidatePreparationTask ??= PrepareCandidateCoreAsync(cancellationToken);
        }
    }

    internal Task CommitGenerationAsync(
        SunderWorkerGenerationContext generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (_gate)
        {
            if (!_candidatePrepared)
            {
                throw new InvalidOperationException(
                    "A worker composition must prepare its candidate before generation commit.");
            }
            if (_shutdownTask is not null)
            {
                throw new ObjectDisposedException(nameof(SunderWorkerComposition));
            }
            if (_generation is not null
                && (_generation.ActivationId != generation.ActivationId
                    || _generation.SessionGeneration != generation.SessionGeneration))
            {
                throw new InvalidOperationException(
                    "A worker composition is already committed to another Runtime generation.");
            }
            _generation ??= generation;
            return _commitTask ??= CommitGenerationCoreAsync(generation, cancellationToken);
        }
    }

    internal Task ActivateAsync(
        SunderWorkerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            if (!_candidatePrepared)
            {
                throw new InvalidOperationException(
                    "A worker composition must prepare its candidate before activation.");
            }
            if (_generation is null)
            {
                throw new InvalidOperationException(
                    "A worker composition must commit its Runtime generation before activation.");
            }
            if (_shutdownTask is not null)
            {
                throw new ObjectDisposedException(nameof(SunderWorkerComposition));
            }
            return _activationTask ??= ActivateCoreAsync(context, cancellationToken);
        }
    }

    internal Task ShutdownAsync(
        SunderWorkerShutdownContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            return _shutdownTask ??= ShutdownCoreAsync(context, cancellationToken);
        }
    }

    /// <summary>Shuts down prepared lifecycle services in reverse order and disposes the package-owned service provider.</summary>
    public async ValueTask DisposeAsync()
        => await ShutdownAsync(
            new SunderWorkerShutdownContext("composition-disposed"),
            CancellationToken.None).ConfigureAwait(false);

    private async Task PrepareCandidateCoreAsync(CancellationToken cancellationToken)
    {
        foreach (var service in _lifecycleServices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await service.PrepareCandidateAsync(cancellationToken).ConfigureAwait(false);
            _preparedServices.Add(service);
        }
        _candidatePrepared = true;
    }

    private async Task CommitGenerationCoreAsync(
        SunderWorkerGenerationContext generation,
        CancellationToken cancellationToken)
    {
        foreach (var service in _lifecycleServices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await service.CommitGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ActivateCoreAsync(
        SunderWorkerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var service in _lifecycleServices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await service.ActivateAsync(context, cancellationToken).ConfigureAwait(false);
        }
        var completions = _lifecycleServices
            .Select(static service => service.Completion
                ?? throw new InvalidOperationException("A worker lifecycle service returned a null completion task."))
            .Where(static completion => !completion.IsCompletedSuccessfully)
            .ToArray();
        foreach (var completion in completions)
        {
            if (completion.IsFaulted || completion.IsCanceled)
            {
                await completion.ConfigureAwait(false);
            }
        }
        lock (_gate)
        {
            _completion = ObserveCompletionsAsync(completions);
        }
    }

    private static async Task ObserveCompletionsAsync(IReadOnlyList<Task> completions)
    {
        if (completions.Count == 0) return;
        var pending = completions.ToList();
        while (pending.Count > 0)
        {
            var completion = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completion);
            await completion.ConfigureAwait(false);
        }
    }

    private async Task ShutdownCoreAsync(
        SunderWorkerShutdownContext context,
        CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        foreach (var service in _preparedServices.AsEnumerable().Reverse())
        {
            try
            {
                await service.ShutdownAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures ??= [];
                failures.Add(exception);
            }
        }
        _preparedServices.Clear();
        try
        {
            if (Services is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exception)
        {
            failures ??= [];
            failures.Add(exception);
        }
        if (failures is { Count: > 0 })
        {
            throw new AggregateException("Worker composition shutdown failed.", failures);
        }
    }
}
