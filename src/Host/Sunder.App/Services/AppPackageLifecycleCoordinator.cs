using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class AppPackageLifecycleCoordinator
{
    private readonly PackageViewHostService _packageViewHostService;
    private readonly AppPackageGenerationBuilder? _generationBuilder;
    private readonly AppPackageGenerationPublisher? _generationPublisher;
    private readonly PackageIconGenerationCoordinator? _iconCoordinator;
    private readonly AppPackageGenerationRetirementQueue? _retirementOwner;
    private readonly AppPackageLifecycleGate? _lifecycleGate;
    private readonly AppPackageLifecycleCoordinator? _orchestrator;

    public AppPackageLifecycleCoordinator(PackageViewHostService packageViewHostService)
    {
        _packageViewHostService = packageViewHostService;
        _orchestrator = packageViewHostService.LifecycleCoordinator;
    }

    internal AppPackageLifecycleCoordinator(
        PackageViewHostService packageViewHostService,
        AppPackageGenerationBuilder generationBuilder,
        AppPackageGenerationPublisher generationPublisher,
        PackageIconGenerationCoordinator iconCoordinator,
        AppPackageGenerationRetirementQueue retirementOwner,
        AppPackageLifecycleGate lifecycleGate)
    {
        _packageViewHostService = packageViewHostService;
        _generationBuilder = generationBuilder;
        _generationPublisher = generationPublisher;
        _iconCoordinator = iconCoordinator;
        _retirementOwner = retirementOwner;
        _lifecycleGate = lifecycleGate;
    }

    public Task<IReadOnlyList<ActivePackageDescriptor>> ApplyPackageSnapshotAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyCollection<string>? retryDisabledPackageIds = null,
        Action<IReadOnlyList<ActivePackageDescriptor>>? commitPresentation = null,
        CancellationToken cancellationToken = default)
        => Target.ApplyPackageSnapshotCoreAsync(
            snapshot,
            retryDisabledPackageIds,
            commitPresentation is null
                ? null
                : (_, activePackages, _) => Task.FromResult<Action?>(() => commitPresentation(activePackages)),
            cancellationToken);

    internal Task<IReadOnlyList<ActivePackageDescriptor>> ApplyPackageSnapshotAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        Func<PackageViewHostService.AppPackagePresentationCandidate, IReadOnlyList<ActivePackageDescriptor>, CancellationToken, Task<Action?>>? preparePresentation,
        CancellationToken cancellationToken)
        => Target.ApplyPackageSnapshotCoreAsync(
            snapshot,
            retryDisabledPackageIds,
            preparePresentation,
            cancellationToken);

    internal Task<IReadOnlyList<ActivePackageDescriptor>> ApplyPackageGenerationAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        Action<IReadOnlyList<ActivePackageDescriptor>>? commitPresentation,
        CancellationToken cancellationToken)
        => Target.ApplyGenerationCoreAsync(
            activePackages,
            packageSources,
            retryDisabledPackageIds,
            snapshot: null,
            commitPresentation is null
                ? null
                : (_, enabledPackages, _) => Task.FromResult<Action?>(() => commitPresentation(enabledPackages)),
            cancellationToken);

    internal async Task PrewarmCurrentPackageIconsAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        CancellationToken cancellationToken)
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.PrewarmCurrentPackageIconsAsync(snapshot, activePackages, cancellationToken).ConfigureAwait(false);
            return;
        }

        var generation = Publisher.CurrentGeneration;
        var prepared = await Icons.PrepareAsync(
            generation,
            snapshot,
            activePackages,
            cancellationToken).ConfigureAwait(false);
        await Publisher.PublishPreparedIconsAsync(generation, prepared, cancellationToken).ConfigureAwait(false);
    }

    private AppPackageLifecycleCoordinator Target => _orchestrator ?? this;

    private Task<IReadOnlyList<ActivePackageDescriptor>> ApplyPackageSnapshotCoreAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        Func<PackageViewHostService.AppPackagePresentationCandidate, IReadOnlyList<ActivePackageDescriptor>, CancellationToken, Task<Action?>>? preparePresentation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retryDisabledPackages = retryDisabledPackageIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(retryDisabledPackageIds, StringComparer.OrdinalIgnoreCase);
        return ApplyGenerationCoreAsync(
            snapshot.ActivePackages,
            snapshot.PackageUiSnapshots,
            retryDisabledPackages,
            snapshot,
            preparePresentation,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ActivePackageDescriptor>> ApplyGenerationCoreAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        RuntimePackageSnapshot? snapshot,
        Func<PackageViewHostService.AppPackagePresentationCandidate, IReadOnlyList<ActivePackageDescriptor>, CancellationToken, Task<Action?>>? preparePresentation,
        CancellationToken cancellationToken)
    {
        _packageViewHostService.ThrowIfDisposedForLifecycle();
        using var transaction = Publisher.BeginTransaction(cancellationToken);
        using var lifecycle = await Gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        AppPackageGeneration? candidate = null;
        var committed = false;
        try
        {
            candidate = await Builder.BuildAsync(
                Publisher.CurrentGeneration,
                activePackages,
                packageSources,
                retryDisabledPackageIds,
                Publisher.HasCommittedGeneration,
                RejectCandidate,
                cancellationToken).ConfigureAwait(false);
            var enabledPackages = AppPackageGenerationBuilder.FilterEnabledPackages(candidate, activePackages);
            if (snapshot is not null)
            {
                var iconGeneration = await Icons.PrepareAsync(
                    candidate,
                    snapshot,
                    enabledPackages,
                    cancellationToken).ConfigureAwait(false);
                candidate.ReplaceIconGeneration(iconGeneration).Dispose();
            }

            var commitPresentation = preparePresentation is null
                ? null
                : await preparePresentation(
                    new PackageViewHostService.AppPackagePresentationCandidate(_packageViewHostService, candidate),
                    enabledPackages,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var retiredGeneration = await Publisher.CommitAsync(
                transaction,
                candidate,
                commitPresentation,
                cancellationToken).ConfigureAwait(false);
            committed = true;
            Retirement.Enqueue(retiredGeneration);
            return enabledPackages;
        }
        finally
        {
            if (!committed && candidate is not null)
            {
                RejectCandidate(candidate);
            }
        }
    }

    private void RejectCandidate(AppPackageGeneration candidate)
    {
        candidate.Composition.UnpublishServices();
        Retirement.Enqueue(candidate);
    }

    private AppPackageGenerationBuilder Builder
        => _generationBuilder ?? throw new InvalidOperationException("The lifecycle coordinator is not the package host orchestrator.");

    private AppPackageGenerationPublisher Publisher
        => _generationPublisher ?? throw new InvalidOperationException("The lifecycle coordinator is not the package host orchestrator.");

    private PackageIconGenerationCoordinator Icons
        => _iconCoordinator ?? throw new InvalidOperationException("The lifecycle coordinator is not the package host orchestrator.");

    private AppPackageGenerationRetirementQueue Retirement
        => _retirementOwner ?? throw new InvalidOperationException("The lifecycle coordinator is not the package host orchestrator.");

    private AppPackageLifecycleGate Gate
        => _lifecycleGate ?? throw new InvalidOperationException("The lifecycle coordinator is not the package host orchestrator.");
}
