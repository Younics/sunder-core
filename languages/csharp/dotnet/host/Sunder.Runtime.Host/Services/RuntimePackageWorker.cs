using Sunder.Sdk.Abstractions;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Settings;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageGeneration(Guid ActivationId, long SessionGeneration);

internal interface IRuntimePackageWorker : IAsyncDisposable
{
    IReadOnlyList<PackageStorageMigrationPlanIdentity> StorageMigrationPlans => [];

    PackageStorageMigrationJournal? StorageMigrationJournal => null;

    string? WorkspaceRootPath => null;

    PackageSettingsSchema? SettingsSchema => null;

    Task GenerationCompletion { get; }

    IPackageAuthHandler? AuthHandler { get; }

    IPackageCallbackHandler? GetCallbackHandler(string callbackHandlerId);

    Task StartAsync(CancellationToken cancellationToken = default);
    Task ComposeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task CommitGenerationAsync(
        RuntimePackageGeneration generation,
        CancellationToken cancellationToken = default);

    Task PrepareStorageMigrationsAsync(
        RuntimePackageGeneration generation,
        CancellationToken cancellationToken = default);

    Task ActivateGenerationAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
