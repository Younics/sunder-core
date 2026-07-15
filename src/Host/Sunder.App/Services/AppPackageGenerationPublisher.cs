using System.Reflection;

namespace Sunder.App.Services;

internal sealed class AppPackageGenerationPublisher
{
    private readonly object _commitGate = new();
    private readonly IUiDispatcher _uiDispatcher;
    private readonly PackageIconGenerationCoordinator _iconCoordinator;
    private readonly AppPackageResourceAssemblyRegistry? _resourceAssemblyRegistry;
    private readonly Action<AppPackageGeneration> _attachGeneration;
    private readonly Action<AppPackageGeneration> _detachGeneration;
    private AppPackageGeneration _currentGeneration;
    private long _latestCandidateRequestId;

    public AppPackageGenerationPublisher(
        AppPackageGeneration initialGeneration,
        IUiDispatcher uiDispatcher,
        PackageIconGenerationCoordinator iconCoordinator,
        AppPackageResourceAssemblyRegistry? resourceAssemblyRegistry,
        Action<AppPackageGeneration> attachGeneration,
        Action<AppPackageGeneration> detachGeneration)
    {
        _currentGeneration = initialGeneration;
        _uiDispatcher = uiDispatcher;
        _iconCoordinator = iconCoordinator;
        _resourceAssemblyRegistry = resourceAssemblyRegistry;
        _attachGeneration = attachGeneration;
        _detachGeneration = detachGeneration;
        _attachGeneration(initialGeneration);
    }

    public AppPackageGeneration CurrentGeneration => Volatile.Read(ref _currentGeneration);

    public bool HasCommittedGeneration { get; private set; }

    public AppPackageGenerationTransaction BeginTransaction(CancellationToken cancellationToken)
    {
        long requestId;
        lock (_commitGate)
        {
            requestId = ++_latestCandidateRequestId;
        }

        return new AppPackageGenerationTransaction(this, requestId, cancellationToken);
    }

    public async Task<AppPackageGeneration> CommitAsync(
        AppPackageGenerationTransaction transaction,
        AppPackageGeneration candidate,
        Action? commitPresentation,
        CancellationToken cancellationToken)
    {
        AppPackageGeneration? retiredGeneration = null;
        PackageIconGeneration? previousIcons = null;
        await InvokeOnUiThreadAsync(() =>
        {
            lock (_commitGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (transaction.RequestId != _latestCandidateRequestId)
                {
                    throw new OperationCanceledException(
                        "The App package candidate was superseded by a newer generation.",
                        cancellationToken);
                }

                var previousGeneration = CurrentGeneration;
                _detachGeneration(previousGeneration);
                Volatile.Write(ref _currentGeneration, candidate);
                _attachGeneration(candidate);
                try
                {
                    previousIcons = _iconCoordinator.Publish(candidate.IconGeneration);
                    PublishResourceAssemblies(candidate.SnapshotPackageAssemblies());
                    commitPresentation?.Invoke();
                }
                catch
                {
                    candidate.Composition.UnpublishServices();
                    _detachGeneration(candidate);
                    Volatile.Write(ref _currentGeneration, previousGeneration);
                    _attachGeneration(previousGeneration);
                    if (previousIcons is not null)
                    {
                        _iconCoordinator.Restore(previousIcons);
                    }
                    PublishResourceAssemblies(previousGeneration.SnapshotPackageAssemblies());
                    throw;
                }

                previousGeneration.Composition.UnpublishServices();
                candidate.Composition.PublishServices();
                retiredGeneration = previousGeneration;
                HasCommittedGeneration = true;
            }
        }).ConfigureAwait(false);

        return retiredGeneration
            ?? throw new InvalidOperationException("The App package transaction did not retire the previous generation.");
    }

    public async Task PublishPreparedIconsAsync(
        AppPackageGeneration generation,
        PackageIconGeneration prepared,
        CancellationToken cancellationToken)
    {
        PackageIconGeneration? retired = null;
        var published = false;
        try
        {
            await InvokeOnUiThreadAsync(() =>
            {
                lock (_commitGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(generation, CurrentGeneration))
                    {
                        throw new OperationCanceledException(
                            "The App package generation changed while its icons were loading.",
                            cancellationToken);
                    }

                    retired = generation.ReplaceIconGeneration(prepared);
                    _iconCoordinator.Publish(prepared);
                    published = true;
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (published)
            {
                retired?.Dispose();
            }
            else
            {
                prepared.Dispose();
            }
        }
    }

    public Task PublishEmptyResourcesAsync()
        => InvokeOnUiThreadAsync(() => PublishResourceAssemblies([]));

    public void PublishCurrentResources()
        => PublishResourceAssemblies(CurrentGeneration.SnapshotPackageAssemblies());

    public void DetachCurrentGeneration()
        => _detachGeneration(CurrentGeneration);

    internal void Supersede(long requestId)
    {
        lock (_commitGate)
        {
            if (_latestCandidateRequestId == requestId)
            {
                _latestCandidateRequestId++;
            }
        }
    }

    private void PublishResourceAssemblies(IReadOnlyList<(string PackageId, Assembly Assembly)> assemblies)
    {
        if (_resourceAssemblyRegistry is null)
        {
            return;
        }

        AppPackageAvaloniaAssetLoader.TryInvalidateAssemblyCache(
            _resourceAssemblyRegistry.ReplacePackageAssemblies(assemblies));
    }

    private Task InvokeOnUiThreadAsync(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _uiDispatcher.InvokeAsync(action);
    }
}

internal sealed class AppPackageGenerationTransaction : IDisposable
{
    private readonly AppPackageGenerationPublisher _publisher;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private int _disposed;

    public AppPackageGenerationTransaction(
        AppPackageGenerationPublisher publisher,
        long requestId,
        CancellationToken cancellationToken)
    {
        _publisher = publisher;
        RequestId = requestId;
        _cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var transaction = (AppPackageGenerationTransaction)state!;
                transaction._publisher.Supersede(transaction.RequestId);
            },
            this);
    }

    internal long RequestId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cancellationRegistration.Dispose();
        }
    }
}
