using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Tests;

internal class TestRuntimePackageWorker(
    IPackageAuthHandler? authHandler = null,
    IReadOnlyDictionary<string, IPackageCallbackHandler>? callbackHandlers = null,
    Func<ValueTask>? disposeAsync = null) : IRuntimePackageWorker
{
    private readonly IReadOnlyDictionary<string, IPackageCallbackHandler> _callbackHandlers =
        callbackHandlers ?? new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase);

    public virtual Task GenerationCompletion => Task.CompletedTask;

    public virtual IReadOnlyList<PackageStorageMigrationPlanIdentity> StorageMigrationPlans => [];

    public virtual PackageStorageMigrationJournal? StorageMigrationJournal => null;

    public virtual string? WorkspaceRootPath => null;

    public IPackageAuthHandler? AuthHandler { get; } = authHandler;

    public IPackageCallbackHandler? GetCallbackHandler(string callbackHandlerId)
        => _callbackHandlers.GetValueOrDefault(callbackHandlerId);

    public virtual Task StartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task CommitGenerationAsync(
        RuntimePackageGeneration generation,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task PrepareStorageMigrationsAsync(
        RuntimePackageGeneration generation,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task ActivateGenerationAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task StopAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual ValueTask DisposeAsync()
        => disposeAsync?.Invoke() ?? ValueTask.CompletedTask;
}
