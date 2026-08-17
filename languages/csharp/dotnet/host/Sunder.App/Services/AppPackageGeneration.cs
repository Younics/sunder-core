using System.Reflection;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageCleanupUnprovenException(Guid generationId)
    : Exception(
        $"App package generation '{generationId}' cleanup could not be proven.");

internal sealed class AppPackageGeneration(
    Guid id,
    string? folder,
    IReadOnlyList<ActivePackageDescriptor> activePackages,
    AppPackageViewRegistry viewRegistry,
    AppPackageHostState state,
    AppPackageHostComposition composition,
    PackageIconGeneration iconGeneration) : IAsyncDisposable
{
    private int _disposed;

    public Guid Id { get; } = id;

    public string? Folder { get; } = folder;

    public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; } = activePackages;

    public AppPackageViewRegistry ViewRegistry { get; } = viewRegistry;

    public AppPackageHostState State { get; } = state;

    public AppPackageHostComposition Composition { get; } = composition;

    public PackageIconGeneration IconGeneration { get; private set; } = iconGeneration;

    public AppPackageQuarantineContext QuarantineContext { get; set; } =
        AppPackageQuarantineContext.Empty;

    public IReadOnlyList<(string PackageId, Assembly Assembly)> SnapshotPackageAssemblies()
        => Composition.AssemblyTracker.SnapshotPackageAssemblies();

    public PackageIconGeneration ReplaceIconGeneration(PackageIconGeneration replacement)
    {
        var previous = IconGeneration;
        IconGeneration = replacement;
        return previous;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Composition.UnpublishServices();
        var retirementDrains = Composition.BeginGenerationRetirement().ToList();
        var packageRetirements = State
            .SnapshotLoadedPackageIds()
            .Select(packageId => Task.Run(
                () => Composition.UnloadPackageForGenerationRetirementAsync(packageId),
                CancellationToken.None))
            .ToArray();
        retirementDrains.AddRange(packageRetirements);
        await Task.WhenAll(retirementDrains).ConfigureAwait(false);

        var cleanupProven = Composition.CleanupOutcome == AppPackageUiCleanupOutcome.Proven
            && packageRetirements.All(task =>
                task.Result.CleanupOutcome == AppPackageUiCleanupOutcome.Proven);
        cleanupProven &= await Composition.DisposeRemainingOwnedResourcesAsync()
            .ConfigureAwait(false) == AppPackageUiCleanupOutcome.Proven;
        Composition.DisposeSharedAssemblies();
        Composition.Dispose();
        IconGeneration.Dispose();
        if (Folder is not null)
        {
            AppPackageSourcePreparer.TryDeleteDirectory(Folder);
        }
        if (!cleanupProven)
        {
            throw new AppPackageCleanupUnprovenException(Id);
        }
    }
}
