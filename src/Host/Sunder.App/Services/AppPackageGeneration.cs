using System.Reflection;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

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
        await Composition.ViewFacade.CancelAllViewOperationsAsync().ConfigureAwait(false);
        foreach (var packageId in State.SnapshotLoadedPackageIds())
        {
            await Composition.UnloadPackageAsync(packageId).ConfigureAwait(false);
        }

        await Composition.DisposeRemainingOwnedResourcesAsync().ConfigureAwait(false);
        Composition.DisposeSharedAssemblies();
        Composition.Dispose();
        IconGeneration.Dispose();
        if (Folder is not null)
        {
            AppPackageSourcePreparer.TryDeleteDirectory(Folder);
        }
    }
}
