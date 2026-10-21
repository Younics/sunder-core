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
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds = null,
        Action<IReadOnlyList<ActivePackageDescriptor>>? commitPresentation = null,
        CancellationToken cancellationToken = default)
        => Target.ApplyPackageSnapshotCoreAsync(
            snapshot,
            packageSources,
            retryDisabledPackageIds,
            commitPresentation is null
                ? null
                : (_, activePackages, _) => Task.FromResult<Action?>(() => commitPresentation(activePackages)),
            resetPresentation: null,
            cancellationToken);

    internal Task<IReadOnlyList<ActivePackageDescriptor>> ApplyPackageSnapshotAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        Func<PackageViewHostService.AppPackagePresentationCandidate, IReadOnlyList<ActivePackageDescriptor>, CancellationToken, Task<Action?>>? preparePresentation,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? resetPresentation = null)
        => Target.ApplyPackageSnapshotCoreAsync(
            snapshot,
            packageSources,
            retryDisabledPackageIds,
            preparePresentation,
            resetPresentation,
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
            resetPresentation: null,
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
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        Func<PackageViewHostService.AppPackagePresentationCandidate, IReadOnlyList<ActivePackageDescriptor>, CancellationToken, Task<Action?>>? preparePresentation,
        Func<CancellationToken, Task>? resetPresentation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyCollection<string> retryDisabledPackages = retryDisabledPackageIds switch
        {
            AppPackageQuarantineRetryIntent retryIntent => retryIntent.Snapshot(),
            null => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(retryDisabledPackageIds, StringComparer.OrdinalIgnoreCase),
        };
        return ApplyGenerationCoreAsync(
            snapshot.ActivePackages,
            packageSources,
            retryDisabledPackages,
            snapshot,
            preparePresentation,
            resetPresentation,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ActivePackageDescriptor>> ApplyGenerationCoreAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds,
        RuntimePackageSnapshot? snapshot,
        Func<PackageViewHostService.AppPackagePresentationCandidate, IReadOnlyList<ActivePackageDescriptor>, CancellationToken, Task<Action?>>? preparePresentation,
        Func<CancellationToken, Task>? resetPresentation,
        CancellationToken cancellationToken)
    {
        _packageViewHostService.ThrowIfDisposedForLifecycle();
        using var transaction = Publisher.BeginTransaction(cancellationToken);
        using var lifecycle = await Gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var effectiveRetryPackageIds = retryDisabledPackageIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(retryDisabledPackageIds, StringComparer.OrdinalIgnoreCase);
        var forceDisabledPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var quarantineContext = _packageViewHostService.CaptureCandidateQuarantineContext(
            retryDisabledPackageIds);
        while (true)
        {
            AppPackageGeneration? candidate = null;
            var committed = false;
            var presentationStarted = false;
            try
            {
                candidate = await Builder.BuildAsync(
                    Publisher.CurrentGeneration,
                    activePackages,
                    packageSources,
                    effectiveRetryPackageIds,
                    forceDisabledPackageIds,
                    Publisher.HasCommittedGeneration,
                    RejectCandidate,
                    cancellationToken).ConfigureAwait(false);
                candidate.QuarantineContext = quarantineContext;
                candidate.RuntimeStamp = snapshot is null ? null : new(snapshot.RuntimeInstanceId, snapshot.SessionGeneration);
                await _packageViewHostService.ApplyInheritedQuarantinesAsync(
                    candidate,
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

                await _packageViewHostService.ApplyInheritedQuarantinesAsync(
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                enabledPackages = AppPackageGenerationBuilder.FilterEnabledPackages(candidate, activePackages);
                Action? commitPresentation = null;
                if (preparePresentation is not null)
                {
                    presentationStarted = true;
                    commitPresentation = await preparePresentation(
                        new PackageViewHostService.AppPackagePresentationCandidate(_packageViewHostService, candidate),
                        enabledPackages,
                        cancellationToken).ConfigureAwait(false);
                }
                await _packageViewHostService.ApplyInheritedQuarantinesAsync(
                    candidate,
                    cancellationToken).ConfigureAwait(false);
                enabledPackages = AppPackageGenerationBuilder.FilterEnabledPackages(candidate, activePackages);
                cancellationToken.ThrowIfCancellationRequested();
                var retiredGeneration = await Publisher.CommitAsync(
                    transaction,
                    candidate,
                    commitPresentation,
                    cancellationToken).ConfigureAwait(false);
                committed = true;
                Retirement.Enqueue(retiredGeneration);
                _packageViewHostService.QueueCommittedQuarantineCleanup(candidate);
                return AppPackageGenerationBuilder.FilterEnabledPackages(candidate, activePackages);
            }
            catch (AppPackageCandidateRebuildRequiredException exception)
            {
                var addedPackage = false;
                foreach (var packageId in exception.PackageIds)
                {
                    addedPackage |= forceDisabledPackageIds.Add(packageId);
                    effectiveRetryPackageIds.Remove(packageId);
                }
                if (!addedPackage)
                {
                    throw new InvalidOperationException(
                        "An App package candidate remained active after it was selected for quarantine.",
                        exception);
                }
                if (presentationStarted && resetPresentation is not null)
                {
                    await resetPresentation(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!committed && candidate is not null)
                {
                    RejectCandidate(candidate);
                }
            }
        }
    }

    private void RejectCandidate(AppPackageGeneration candidate)
    {
        candidate.RevokeUnretainedServices();
        candidate.ReleaseDataHandoffs();
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
