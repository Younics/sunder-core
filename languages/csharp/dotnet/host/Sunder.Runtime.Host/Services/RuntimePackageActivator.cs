using Microsoft.Extensions.Logging;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageActivator(
    ILogger logger,
    RuntimePackagePaths paths,
    RuntimeRpcBroker? rpcBroker = null,
    RuntimeProcessPolicyOptions? processPolicy = null,
    CancellationToken hostStopping = default,
    PackageSessionState? packageSessionState = null,
    IExecutionRuntimeLaunchResolver? executionRuntimeLaunchResolver = null,
    ExecutionRuntimePhaseTelemetry? telemetry = null)
{
    private readonly IExecutionRuntimeLaunchResolver _executionRuntimeLaunchResolver =
        executionRuntimeLaunchResolver ?? UnavailableExecutionRuntimeLaunchResolver.Instance;
    private readonly ExecutionRuntimePhaseTelemetry _telemetry =
        telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;

    public async Task<PackageActivationResult> ActivateAsync(
        PreparedRuntimePackage package,
        ICollection<string> warnings,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        ResolvedExecutionRuntimeLaunch? launch = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageManifestAdmissionPolicy.EnsureSupported(
                package.Source.Manifest ?? throw new InvalidDataException("Package source is missing its manifest."));
            if (package.SelectedTargetKey is not { } targetKey
                || package.SelectedTarget is not { } target
                || package.EntryAssemblyPath is null)
            {
                throw new InvalidOperationException(
                    $"Package '{package.PackageId}' does not have a selected exact Runtime target.");
            }
            if (!string.Equals(
                    target.Kind,
                    SunderPackageFormat.WorkerTargetKind,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Package '{package.PackageId}' Runtime target '{targetKey}' kind '{target.Kind}' is unsupported; expected '{SunderPackageFormat.WorkerTargetKind}'.");
            }
            if (rpcBroker is null)
            {
                throw new InvalidOperationException(
                    $"Package '{package.PackageId}' worker Runtime target requires the Runtime RPC broker.");
            }
            if (packageSessionState is null)
            {
                throw new InvalidOperationException(
                    $"Package '{package.PackageId}' V1 worker Runtime target requires active-session authority.");
            }
            var activationId = Guid.NewGuid();
            await rpcBroker.RequirePackagePolicyAsync(package, cancellationToken).ConfigureAwait(false);
            package.StartupTrace?.BindActivation(activationId);
            package.StartupTrace?.BeginRuntimeBinding();
            launch = await _executionRuntimeLaunchResolver.ResolveAsync(
                    package,
                    activationId,
                    cancellationToken)
                .ConfigureAwait(false);
            package.StartupTrace?.CompleteRuntimeBinding(package.ExecutionRuntimeBinding, "succeeded");
            var loadedPackage = CreateLoadedPackage(
                package,
                launch,
                activationId,
                rpcBroker,
                packageSessionState);
            AddWarning(warnings, launch.ActivationWarning);
            launch = null;
            var providers = loadedPackage.RpcProviders;
            if (providers.Count == 0)
            {
                warnings.Add($"Package '{package.PackageId}' worker Runtime target loaded without any declared RPC providers.");
            }
            return new PackageActivationResult(
                true,
                loadedPackage,
                BuildSessionDescriptor(package.Activation, true, PackageReadinessState.Ready, packageViews: []));
        }
        catch (Exception exception)
        {
            package.StartupTrace?.CompleteRuntimeBinding(
                package.ExecutionRuntimeBinding,
                exception is OperationCanceledException ? "canceled" : "failed");
            if (launch is not null)
            {
                await launch.DisposeAsync().ConfigureAwait(false);
            }
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            logger.LogError(exception, "Failed to activate package {PackageId}", package.PackageId);
            errors.Add($"Failed to activate package '{package.PackageId}': {exception.Message}");
            return Failed(package, exception.Message);
        }
    }

    private ActiveLoadedPackage CreateLoadedPackage(
        PreparedRuntimePackage package,
        ResolvedExecutionRuntimeLaunch launch,
        Guid activationId,
        RuntimeRpcBroker rpcBroker,
        PackageSessionState packageSessionState)
    {
        RuntimePackageDataResources? packageData = null;
        ProcessRuntimeWorker? worker = null;
        try
        {
            packageData = new RuntimePackageDataResources(
                package.PackageId,
                package.Version,
                paths.PackageDataRootPath);
            worker = new ProcessRuntimeWorker(
                logger,
                package,
                packageData,
                activationId,
                rpcBroker,
                launch,
                processPolicy,
                hostStopping,
                packageSessionState,
                _telemetry);
            var recoveryPackage = package with { StartupTrace = null };
            var loadedPackage = new ActiveLoadedPackage(
                BuildDescriptor(package.Activation, true, PackageReadinessState.Ready, []),
                package.Source,
                SettingsSchema: null,
                packageData.Storage.State,
                packageData.Secrets,
                packageData.Storage.Files,
                packageData.Settings,
                worker)
            {
                RuntimeActivationId = activationId,
                RpcProviders = CreateWorkerProviderRegistrations(package, worker),
                RpcContracts = package.RpcContracts
                               ?? new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal),
                RpcContractUses = (package.Source.Manifest?.UsesContracts ?? [])
                    .Where(static use => use is not null)
                    .Select(static use => use!)
                    .ToArray(),
                RpcManifestSha256 = package.ManifestSha256 ?? string.Empty,
                MigrationJournal = packageData.MigrationJournal,
                ExecutionRuntimeBinding = package.ExecutionRuntimeBinding,
                RecoveryFactory = () => CreateRecoveryLoadedPackage(
                    recoveryPackage,
                    rpcBroker,
                    packageSessionState),
            };
            packageData = null;
            worker = null;
            return loadedPackage;
        }
        catch
        {
            if (worker is not null)
            {
                worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else
            {
                packageData?.Dispose();
            }
            throw;
        }
    }

    private ActiveLoadedPackage CreateRecoveryLoadedPackage(
        PreparedRuntimePackage package,
        RuntimeRpcBroker rpcBroker,
        PackageSessionState packageSessionState)
    {
        var activationId = Guid.NewGuid();
        var recoveryProjectionLease = package.PackageProjectionLease?.Reacquire(activationId.ToString("N"));
        var recoveryPackage = package with
        {
            PackageProjectionLease = recoveryProjectionLease,
        };
        ResolvedExecutionRuntimeLaunch? launch = null;
        try
        {
            rpcBroker.RequirePackagePolicy(package.Source);
            launch = _executionRuntimeLaunchResolver.ResolveAsync(
                    recoveryPackage,
                    activationId,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return CreateLoadedPackage(
                recoveryPackage,
                launch,
                activationId,
                rpcBroker,
                packageSessionState);
        }
        catch
        {
            launch?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            recoveryProjectionLease?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static PackageActivationResult Failed(PreparedRuntimePackage package, string message)
        => new(false, null, BuildSessionDescriptor(
            package.Activation,
            false,
            PackageReadinessState.Failed,
            failureOrigin: PackageFailureOrigin.RuntimeActivation,
            lastError: message,
            failureCount: 1));

    private static IReadOnlyDictionary<string, RuntimeRpcProviderRegistration> CreateWorkerProviderRegistrations(
        PreparedRuntimePackage package,
        ProcessRuntimeWorker worker)
    {
        var contracts = package.RpcContracts
                        ?? new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        var registrations = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal);
        foreach (var declaration in (package.Source.Manifest?.Provides ?? [])
                     .Where(static provider => provider is not null
                                               && string.Equals(
                                                   provider.Role,
                                                   SunderPackageFormat.RuntimeHostRole,
                                                   StringComparison.Ordinal))
                     .Select(static provider => provider!))
        {
            if (!contracts.TryGetValue(
                    PackageSessionPreparer.ContractKey(declaration.ContractId!, declaration.ContractVersion!),
                    out var contract)
                || contract.DescriptorVersion != SunderRpcProtocol.DescriptorVersion
                || !string.Equals(contract.ContractId, declaration.ContractId, StringComparison.Ordinal)
                || !string.Equals(contract.Version, declaration.ContractVersion, StringComparison.Ordinal)
                || !string.Equals(contract.Sha256, declaration.DescriptorSha256, StringComparison.Ordinal)
                || !string.Equals(contract.AbiSha256, declaration.AbiSha256, StringComparison.Ordinal)
                || !SunderPackageFormat.IsProviderAudience(declaration.Audience))
            {
                throw new InvalidDataException(
                    $"Worker provider '{declaration.ProviderId}' does not resolve its exact validated local RPC contract.");
            }
            if (!registrations.TryAdd(
                    declaration.ProviderId!,
                    new RuntimeRpcProviderRegistration(
                        declaration.ProviderId!,
                        declaration.ContractId!,
                        declaration.ContractVersion!,
                        declaration.DescriptorSha256!,
                        contract,
                        new ProcessRpcProviderHandler(worker, declaration.ProviderId!),
                        declaration)))
            {
                throw new InvalidDataException(
                    $"Worker provider id '{declaration.ProviderId}' is declared more than once for the Runtime role.");
            }
        }
        return registrations;
    }

    private static void AddWarning(ICollection<string> warnings, string? warning)
    {
        if (warning is null || warnings.Any(existing => string.Equals(existing, warning, StringComparison.Ordinal)))
        {
            return;
        }
        warnings.Add(warning);
    }

}
