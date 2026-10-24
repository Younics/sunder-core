using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class PackageViewHostService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _cooperativelyRetired = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ActivationRegistration> _activations = new();

    // Retirement authority must outlive presentation replacement and GC. Keep
    // the actual owner until its exact local drain and Host ack are proven.
    private sealed record ActivationRegistration(AppPackageGeneration Group, Task Ready);

    private void RegisterActivation(AppPackageGeneration group, Task ready)
    {
        foreach (var prior in _activations)
            if (prior.Value.Ready.IsCompletedSuccessfully && prior.Value.Group.ActivePackages is [var package]
                && prior.Value.Group.Composition.HasAcknowledgedRetirement(package.PackageId))
            {
                _cooperativelyRetired.TryAdd(prior.Key, package.PackageId);
                _activations.TryRemove(prior);
            }
        if (!_activations.TryAdd(group.Composition.ActivationOwnerId, new(group, ready)))
            throw new InvalidOperationException("An App activation owner was reused.");
    }

    // The caller may be a package owner's DI disposer while the generation gate is
    // held. Exact activation retirement joins the activation's own drain instead of
    // re-entering presentation publication. Host commit remains the graph authority.
    internal async Task RetireActivationsAsync(IReadOnlyList<RuntimeAppActivationRetirementRequirement> requirements, CancellationToken token)
    {
        foreach (var requirement in requirements.Where(_packagePolicy.OwnsRetirement).DistinctBy(item => item.AppActivationId))
        {
            token.ThrowIfCancellationRequested();
            if (_packagePolicy.HasRetiredActivation(requirement)) continue;
            if (_cooperativelyRetired.TryGetValue(requirement.AppActivationId, out var retiredPackage))
            {
                if (retiredPackage != requirement.Package.PackageId) throw new InvalidDataException("Retirement package identity differs.");
                continue;
            }
            AppPackageLoadAdmission.RequireOutsideCleanup(requirement.AppActivationId);
            if (!_activations.TryGetValue(requirement.AppActivationId, out var registration))
                throw new InvalidOperationException("The requested App activation has no local retirement proof.");
            await registration.Ready.WaitAsync(token).ConfigureAwait(false);
            if (_packagePolicy.HasRetiredActivation(requirement)) continue;
            var group = registration.Group;
            if (group.ActivePackages.Count != 1 || group.ActivePackages[0].PackageId != requirement.Package.PackageId)
                throw new InvalidOperationException("The requested App activation has no matching local owner.");

            using var stateHandoff = group.Composition.BeginDataHandoff();
            await stateHandoff.Drained.WaitAsync(AppShutdownBudgets.GenerationRetirement, token).ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() => _generationPublisher.FenceActivationForRetirement(group, () =>
            {
                foreach (var boundary in SnapshotActivationBoundaries(requirement.AppActivationId))
                {
                    boundary.Release();
                    if (boundary.CleanupOutcome != AppPackageUiCleanupOutcome.Proven)
                        throw new InvalidOperationException("The package view could not be detached for activation retirement.");
                }
            }), token).ConfigureAwait(false);
            stateHandoff.Commit();
            await group.Composition.ViewFacade.CancelPackageViewOperationsAsync(requirement.Package.PackageId).WaitAsync(token).ConfigureAwait(false);
            await group.Composition.RetireForOwnerReleaseAsync(requirement.Package.PackageId, token).ConfigureAwait(false);
            _cooperativelyRetired.TryAdd(requirement.AppActivationId, requirement.Package.PackageId);
            _activations.TryRemove(requirement.AppActivationId, out _);
        }
    }

    private Sunder.App.Views.Controls.HostedPackageViewBoundary[] SnapshotActivationBoundaries(Guid activationId)
    {
        lock (_boundarySyncRoot)
        {
            // Select inside the publication fence: presentation generations may have
            // changed while this exact activation's preparation/drain was awaited.
            return _boundariesByGeneration.Values.SelectMany(items => items)
                .Select(reference => reference.TryGetTarget(out var boundary) ? boundary : null)
                .OfType<Sunder.App.Views.Controls.HostedPackageViewBoundary>()
                .Where(boundary => boundary.ActivationOwnerId == activationId).Distinct().ToArray();
        }
    }
}
