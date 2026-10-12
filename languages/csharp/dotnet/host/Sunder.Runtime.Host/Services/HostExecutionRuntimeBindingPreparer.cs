using System.Collections.ObjectModel;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal interface IHostExecutionRuntimeBindingPreparer
{
    Task<PreparedExecutionRuntimeBindings> PrepareAsync(IReadOnlyList<RegistryPackagePlanResolution> planContexts,
        IReadOnlyDictionary<string, InstalledPackageRecord> initiallyInstalledPackages, CancellationToken cancellationToken);
}

internal sealed class PreparedExecutionRuntimeBindings(
    IReadOnlyDictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>> bindings,
    IReadOnlyList<IAsyncDisposable> retentions) : IAsyncDisposable
{
    private IAsyncDisposable[]? _retentions = retentions.ToArray();

    public IReadOnlyDictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>> Bindings { get; }
        = bindings;

    public async ValueTask DisposeAsync()
    {
        var retained = Interlocked.Exchange(ref _retentions, null);
        if (retained is null)
        {
            return;
        }
        for (var index = retained.Length - 1; index >= 0; index--)
        {
            await retained[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class HostExecutionRuntimeBindingPreparer(
    LocalExecutionRuntimeBindingResolver localResolver,
    ExecutionRuntimeStore store,
    IExecutionRuntimeRetentionManager retentionManager,
    TimeProvider timeProvider,
    ExecutionRuntimePhaseTelemetry? telemetry = null) : IHostExecutionRuntimeBindingPreparer
{
    private readonly ExecutionRuntimePhaseTelemetry _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;

    public async Task<PreparedExecutionRuntimeBindings> PrepareAsync(
        IReadOnlyList<RegistryPackagePlanResolution> planContexts,
        IReadOnlyDictionary<string, InstalledPackageRecord> initiallyInstalledPackages,
        CancellationToken cancellationToken)
    {
        using var operation = _telemetry.BeginOperation();
        using var phase = _telemetry.BeginPhase(ExecutionRuntimePhaseNames.Bind);
        ArgumentNullException.ThrowIfNull(planContexts);
        ArgumentNullException.ThrowIfNull(initiallyInstalledPackages);

        var byPackage = new Dictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>>(StringComparer.OrdinalIgnoreCase);
        var retentions = new List<IAsyncDisposable>();
        var retainedIdentities = new HashSet<ExecutionRuntimeIdentity>();
        try
        {
            foreach (var context in planContexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (context?.Plan is null || !context.Plan.Success)
                {
                    throw new InvalidDataException("A successful Registry package plan is required before preparing execution-runtime bindings.");
                }

                foreach (var item in context.Plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item is null || string.IsNullOrWhiteSpace(item.PackageId))
                    {
                        throw new InvalidDataException("Registry package plan items must identify canonical packages.");
                    }

                    if (item.ActionKind == RegistryPackageActionKinds.Keep)
                    {
                        if (!initiallyInstalledPackages.TryGetValue(item.PackageId, out var installed)
                            || installed.Version != item.Version || InstalledPackagePolicyRecord.Subject(installed).PackageContentIdentity != item.PackageContentIdentity)
                            throw new InvalidDataException("A kept Registry package must match the authoritative installed package.");
                        foreach (var binding in installed.ExecutionRuntimeBindings)
                        {
                            var identity = ExecutionRuntimeIdentity.FromContract(binding.Runtime);
                            var receipt = await store.GetReceiptAsync(identity, cancellationToken).ConfigureAwait(false);
                            if (receipt is null || !RuntimePackageExecutionRuntimeBinding.FromInstalled(binding).MatchesReceipt(receipt)
                                || !(await store.VerifyAsync(identity, false, cancellationToken).ConfigureAwait(false)).Healthy)
                                throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.Unhealthy,
                                    "Repair the exact committed runtime of the kept package before continuing.");
                            if (retainedIdentities.Add(identity)) retentions.Add(await retentionManager.RetainAsync(new(identity), cancellationToken).ConfigureAwait(false));
                        }
                        byPackage[item.PackageId] = installed.ExecutionRuntimeBindings;
                        continue;
                    }
                    if (item.ActionKind is not (RegistryPackageActionKinds.PackageInstall or RegistryPackageActionKinds.PackageUpdate))
                        throw new InvalidDataException("The Registry package plan contains an unsupported package action.");

                    var records = new List<InstalledPackageExecutionRuntimeBindingRecord>();
                    foreach (var target in item.Targets
                                 .Where(target => target.ExecutionRuntime is not null && target.Rid == PackageTargetSelection.GetCurrentRuntimeIdentifier()
                                     && item.Artifacts.Any(artifact => artifact.Kind == target.Role && artifact.Rid == target.Rid))
                                 .OrderBy(static target => target.Role, StringComparer.Ordinal)
                                 .ThenBy(static target => target.Rid, StringComparer.Ordinal))
                    {
                        var requirement = target.ExecutionRuntime!;
                        var runtimeRequirement = new RuntimeExecutionRuntimeRequirement(
                            requirement.RuntimeId,
                            requirement.VersionRange,
                            requirement.ModuleAbi);
                        var receipt = await localResolver.FindHealthyAsync(
                                runtimeRequirement,
                                target.Rid,
                                cancellationToken)
                            .ConfigureAwait(false)
                            ?? throw new InvalidDataException(
                                $"Package '{item.PackageId}' target '{target.Role}/{target.Rid}' requires official runtime '{requirement.RuntimeId}' range '{requirement.VersionRange}' ABI '{requirement.ModuleAbi}', but no healthy installed root satisfies it.");
                        if (retainedIdentities.Add(receipt.Identity))
                        {
                            retentions.Add(await retentionManager.RetainAsync(
                                    new ExecutionRuntimeRetention(Identity: receipt.Identity),
                                    cancellationToken)
                                .ConfigureAwait(false));
                        }

                        records.Add(new InstalledPackageExecutionRuntimeBindingRecord(
                            target.Role,
                            target.Rid,
                            runtimeRequirement,
                            receipt.Identity.ToContract(),
                            receipt.InstalledArtifact,
                            timeProvider.GetUtcNow().ToUniversalTime()));
                    }

                    byPackage[item.PackageId] = Array.AsReadOnly(records.ToArray());
                }
            }

            phase.Succeeded();
            return new PreparedExecutionRuntimeBindings(
                new ReadOnlyDictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>>(byPackage),
                retentions);
        }
        catch (OperationCanceledException)
        {
            phase.Canceled();
            await DisposeRetentionsAsync(retentions).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            phase.Failed(exception);
            await DisposeRetentionsAsync(retentions).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeRetentionsAsync(List<IAsyncDisposable> retentions)
    {
        for (var index = retentions.Count - 1; index >= 0; index--)
        {
            await retentions[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}
