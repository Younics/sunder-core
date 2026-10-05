using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Package.Projections;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLoadService
{
    private readonly PackageSessionPreparer _preparer;
    private readonly RuntimePackageActivator _activator;
    private readonly ILogger _logger;
    private readonly PackageSessionConcurrencyOptions _concurrency;

    public PackageSessionLoadService(
        ILogger logger,
        RuntimePackagePaths? paths = null,
        string? runtimeIdentifier = null,
        RuntimeRpcBroker? rpcBroker = null,
        RuntimeProcessPolicyOptions? processPolicy = null,
        CancellationToken hostStopping = default,
        PackageSessionState? packageSessionState = null,
        IExecutionRuntimeLaunchResolver? executionRuntimeLaunchResolver = null,
        IEphemeralExecutionRuntimeBindingResolver? ephemeralExecutionRuntimeBindingResolver = null,
        ExecutionRuntimePhaseTelemetry? executionRuntimeTelemetry = null,
        PackageProjectionCache? projectionCache = null,
        Guid? hostInstanceId = null,
        PackageSessionConcurrencyOptions? concurrency = null,
        DevelopmentImportAuthorityStore? developmentImports = null)
    {
        _logger = logger;
        _concurrency = concurrency ?? new PackageSessionConcurrencyOptions();
        _concurrency.Validate();
        var resolvedPaths = paths ?? new RuntimePackagePaths();
        var launchResolver = executionRuntimeLaunchResolver
                             ?? UnavailableExecutionRuntimeLaunchResolver.Instance;
        _preparer = new PackageSessionPreparer(
            runtimeIdentifier,
            logger,
            ephemeralExecutionRuntimeBindingResolver
            ?? launchResolver as IEphemeralExecutionRuntimeBindingResolver,
            projectionCache ?? new PackageProjectionCache(resolvedPaths.RootPath),
            hostInstanceId is { } instanceId && instanceId != Guid.Empty ? instanceId : Guid.NewGuid(), developmentImports);
        _activator = new RuntimePackageActivator(
            logger,
            resolvedPaths,
            rpcBroker,
            processPolicy,
            hostStopping,
            packageSessionState,
            launchResolver,
            executionRuntimeTelemetry);
    }

    public async Task<PackageSessionLoadResult> LoadInstalledAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        IReadOnlyDictionary<string, string>? preparationSourcePaths = null,
        CancellationToken cancellationToken = default,
        PackageSessionStartupTraceAttempt? startupTrace = null,
        ExecutionRuntimeLifecyclePolicySnapshot? runtimePolicyAuthority = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var errors = new List<string>();
        RuntimePackageSessionDirectories.ScheduleStaleSessionCleanup();
        var sessionFolder = RuntimePackageSessionDirectories.CreateInstalledSessionFolder();
        Directory.CreateDirectory(sessionFolder);
        var preparedCandidates = new List<PreparedRuntimePackage>();
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase);
        var enabledPackages = packages.Where(static package => package.IsEnabled).ToArray();

        var preparationResults = new InstalledPreparationResult?[packages.Count];
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, packages.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _concurrency.MaximumPreparationConcurrency,
                    CancellationToken = cancellationToken,
                },
                async (index, token) =>
                {
                    var package = packages[index];
                    var physicalSourceRoot = preparationSourcePaths?.GetValueOrDefault(package.PackageId)
                        ?? package.InstallPath;
                    preparationResults[index] = await PrepareInstalledCandidateAsync(
                            index,
                            package,
                            physicalSourceRoot,
                            sessionFolder,
                            token,
                            startupTrace?.GetInstalledPackage(package),
                            runtimePolicyAuthority)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch
        {
            await DisposeExecutionRuntimeRetentionsAsync(
                    preparationResults
                        .Where(static result => result?.PreparedPackage is not null)
                        .Select(static result => result!.PreparedPackage!))
                .ConfigureAwait(false);
            TryDeleteDirectory(sessionFolder);
            throw;
        }

        foreach (var result in preparationResults.Select(static result => result!))
        {
            foreach (var error in result.Errors)
            {
                errors.Add(error);
            }
            if (result.PreparedPackage is not null)
            {
                preparedCandidates.Add(result.PreparedPackage);
            }
            if (result.SessionPackage is not null)
            {
                sessionPackages[result.PackageId] = result.SessionPackage;
            }
        }

        if (enabledPackages.Length == 0 && sessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.CreateEmpty(startupTrace), warnings, errors);
        }

        ThrowIfCancellationRequested(cancellationToken, sessionFolder);
        return await LoadPreparedPackagesAsync(
            sessionFolder,
            preparedCandidates,
            warnings,
            errors,
            sessionPackages,
            cancellationToken,
            startupTrace);
    }

    private async ValueTask<InstalledPreparationResult> PrepareInstalledCandidateAsync(
        int index,
        InstalledPackageRecord package,
        string physicalSourceRoot,
        string sessionFolder,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? packageTrace,
        ExecutionRuntimeLifecyclePolicySnapshot? runtimePolicyAuthority)
    {
        var errors = new List<string>();
        packageTrace?.BeginProjection();
        try
        {
            var validation = await _preparer.ValidateInstalledPackageAsync(
                    package,
                    physicalSourceRoot,
                    errors,
                    cancellationToken,
                    packageTrace)
                .ConfigureAwait(false);
            if (validation is null)
            {
                packageTrace?.CompleteProjection("failed");
                return new InstalledPreparationResult(
                    package.PackageId,
                    null,
                    BuildSessionDescriptor(
                        new RuntimePackageActivationState(
                            package.PackageId,
                            package.Name,
                            package.Version,
                            PackageHostRoles.None,
                            package.Icon),
                        isEnabled: false,
                        readiness: PackageReadinessState.Failed,
                        failureOrigin: PackageFailureOrigin.RuntimeActivation,
                        lastError: errors.LastOrDefault() ?? "Installed package failed strict validation.",
                        failureCount: 1),
                    errors);
            }
            if (!package.IsEnabled)
            {
                packageTrace?.CompleteProjection("not-required");
                return new InstalledPreparationResult(
                    package.PackageId,
                    null,
                    BuildSessionDescriptor(
                        validation.Activation,
                        isEnabled: false,
                        readiness: PackageReadinessState.Disabled),
                    errors);
            }
            if (package.Policy is { IsBlocked: true } policy)
            {
                packageTrace?.CompleteProjection("policy-blocked");
                return new InstalledPreparationResult(package.PackageId, null,
                    BuildSessionDescriptor(validation.Activation, isEnabled: true, readiness: PackageReadinessState.Failed,
                        failureOrigin: PackageFailureOrigin.RuntimeActivation, lastError: policy.ErrorCode, failureCount: 1), errors);
            }

            var preparedPackage = await _preparer.PrepareInstalledPackageAsync(
                    index,
                    package,
                    physicalSourceRoot,
                    sessionFolder,
                    errors,
                    cancellationToken,
                    packageTrace,
                    runtimePolicyAuthority,
                    validation)
                .ConfigureAwait(false);
            if (preparedPackage is not null)
            {
                packageTrace?.SetPreparedIdentity(preparedPackage);
                packageTrace?.CompleteProjection("succeeded");
                return new InstalledPreparationResult(package.PackageId, preparedPackage, null, errors);
            }

            packageTrace?.CompleteProjection("failed");
            return new InstalledPreparationResult(
                package.PackageId,
                null,
                BuildSessionDescriptor(
                    validation.Activation,
                    isEnabled: false,
                    readiness: PackageReadinessState.Failed,
                    failureOrigin: PackageFailureOrigin.RuntimeActivation,
                    lastError: errors.LastOrDefault() ?? "Installed package could not be prepared for loading.",
                    failureCount: 1),
                errors);
        }
        catch (OperationCanceledException)
        {
            packageTrace?.CompleteProjection("canceled");
            throw;
        }
        catch
        {
            packageTrace?.CompleteProjection("failed");
            throw;
        }
    }

    private async Task<PackageSessionLoadResult> LoadPreparedPackagesAsync(
        string sessionFolder,
        IReadOnlyList<PreparedRuntimePackage> preparedCandidates,
        ICollection<string> warnings,
        ICollection<string> errors,
        IDictionary<string, SessionPackageDescriptor> initialSessionPackages,
        CancellationToken cancellationToken,
        PackageSessionStartupTraceAttempt? startupTrace)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (preparedCandidates.Count == 0 && initialSessionPackages.Count == 0)
            {
                TryDeleteDirectory(sessionFolder);
                return new PackageSessionLoadResult(ActivePackageSession.CreateEmpty(startupTrace), warnings.ToArray(), errors.ToArray());
            }

            ThrowIfCancellationRequested(cancellationToken, sessionFolder);
            preparedCandidates = PackageCompatibilityAuthority.ResolveDevelopmentGraph(preparedCandidates);
            PackageCompatibilityAuthority.ValidateGraph(preparedCandidates.Select(package => package.Source).ToArray());
            var loadWaves = new PackageLoadPlanner().ResolveLoadWaves(preparedCandidates, errors);
            startupTrace?.RecordGraphPlan(
                loadWaves,
                _concurrency.MaximumPreparationConcurrency,
                _concurrency.MaximumWorkerStartupConcurrency);
            var orderedPackageCount = loadWaves.Sum(static wave => wave.Count);
            var loadedPackages = new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase);
            var readySources = new List<RuntimePackageSource>(orderedPackageCount);
            var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(initialSessionPackages, StringComparer.OrdinalIgnoreCase);
            var readyPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var waveIndex = 0; waveIndex < loadWaves.Count; waveIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var wave = loadWaves[waveIndex];
                    var activationPackages = new List<PreparedRuntimePackage>(wave.Count);
                    foreach (var preparedPackage in wave)
                    {
                        if (!preparedPackage.Dependencies.All(dependency => readyPackageIds.Contains(dependency.PackageId)))
                        {
                            var message = $"Skipped '{preparedPackage.PackageId}' because one of its dependencies did not load successfully.";
                            errors.Add(message);
                            sessionPackages[preparedPackage.PackageId] = BuildSessionDescriptor(
                                preparedPackage.Activation,
                                isEnabled: false,
                                readiness: PackageReadinessState.Failed,
                                failureOrigin: PackageFailureOrigin.RuntimeActivation,
                                lastError: message,
                                failureCount: 1);
                            continue;
                        }

                        if (RuntimePackageRoleActivation.TryCompleteWithoutRuntime(
                                preparedPackage,
                                readyPackageIds,
                                readySources,
                                sessionPackages))
                        {
                            continue;
                        }
                        activationPackages.Add(preparedPackage);
                    }

                    var attempts = new PackageActivationAttempt?[activationPackages.Count];
                    var waveFailureSignaled = 0;
                    using var waveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    try
                    {
                        await Parallel.ForEachAsync(
                            Enumerable.Range(0, activationPackages.Count),
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = _concurrency.MaximumWorkerStartupConcurrency,
                                CancellationToken = waveCancellation.Token,
                            },
                            async (index, token) =>
                            {
                                var attempt = await ActivatePreparedPackageAsync(
                                        activationPackages[index],
                                        token)
                                    .ConfigureAwait(false);
                                attempts[index] = attempt;
                                if (!attempt.Activation.Success
                                && Interlocked.CompareExchange(ref waveFailureSignaled, 1, 0) == 0)
                                {
                                    waveCancellation.Cancel();
                                }
                            }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                             && Volatile.Read(ref waveFailureSignaled) != 0)
                    {
                    }
                    catch
                    {
                        foreach (var attempt in attempts.Where(static attempt => attempt is not null))
                        {
                            ApplyActivationAttempt(attempt!);
                        }
                        throw;
                    }

                    foreach (var attempt in attempts.Where(static attempt => attempt is not null))
                    {
                        ApplyActivationAttempt(attempt!);
                    }
                    var waveFailurePackageId = attempts.FirstOrDefault(
                        static attempt => attempt is { Activation.Success: false })?.Package.PackageId;
                    if (waveFailurePackageId is null)
                    {
                        continue;
                    }

                    foreach (var index in Enumerable.Range(0, activationPackages.Count)
                                 .Where(index => attempts[index] is null))
                    {
                        MarkCanceled(activationPackages[index], waveFailurePackageId);
                    }
                    foreach (var pendingWave in loadWaves.Skip(waveIndex + 1))
                    {
                        foreach (var pendingPackage in pendingWave)
                        {
                            MarkCanceled(pendingPackage, waveFailurePackageId);
                        }
                    }
                    break;
                }

                void ApplyActivationAttempt(PackageActivationAttempt attempt)
                {
                    _logger.LogInformation(
                        "Activated Runtime package {PackageId} in {ElapsedMilliseconds} ms",
                        attempt.Package.PackageId,
                        attempt.Elapsed.TotalMilliseconds);
                    foreach (var warning in attempt.Warnings)
                    {
                        warnings.Add(warning);
                    }
                    foreach (var error in attempt.Errors)
                    {
                        errors.Add(error);
                    }
                    if (!attempt.Activation.Success)
                    {
                        sessionPackages[attempt.Package.PackageId] = attempt.Activation.SessionPackage;
                        return;
                    }

                    RuntimePackageRoleActivation.MarkReady(attempt.Package, readyPackageIds, readySources);
                    loadedPackages[attempt.Package.PackageId] = attempt.Activation.LoadedPackage!;
                    sessionPackages[attempt.Package.PackageId] = attempt.Activation.SessionPackage;
                }

                void MarkCanceled(PreparedRuntimePackage package, string failedPackageId)
                {
                    var message =
                        $"Canceled '{package.PackageId}' because Runtime package '{failedPackageId}' failed during bounded startup.";
                    sessionPackages[package.PackageId] = BuildSessionDescriptor(
                        package.Activation,
                        isEnabled: false,
                        readiness: PackageReadinessState.Failed,
                        failureOrigin: PackageFailureOrigin.RuntimeActivation,
                        lastError: message,
                        failureCount: 1);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                startupTrace?.TryFail("canceled");
                var partialSession = new ActivePackageSession(
                    sessionFolder,
                    loadedPackages,
                    sessionPackages,
                    false,
                    readySources)
                {
                    StartupTrace = startupTrace,
                };
                await partialSession.StopWorkersAsync(_logger, TimeSpan.FromSeconds(5));
                await partialSession.DisposeAsync();
                throw;
            }
            catch
            {
                startupTrace?.TryFail("activation-failed");
                var partialSession = new ActivePackageSession(
                    sessionFolder,
                    loadedPackages,
                    sessionPackages,
                    false,
                    readySources)
                {
                    StartupTrace = startupTrace,
                };
                await partialSession.StopWorkersAsync(_logger, TimeSpan.FromSeconds(5));
                await partialSession.DisposeAsync();
                throw;
            }

            var session = new ActivePackageSession(
                sessionFolder,
                loadedPackages,
                sessionPackages,
                false,
                readySources,
                loadWaves
                    .Select(wave => (IReadOnlyList<string>)wave
                        .Select(static package => package.PackageId)
                        .Where(loadedPackages.ContainsKey)
                        .ToArray())
                    .Where(static wave => wave.Count > 0),
                _concurrency.MaximumWorkerStartupConcurrency)
            {
                StartupTrace = startupTrace,
            };
            _logger.LogInformation(
                "Loaded Runtime package session with {PreparedPackageCount} prepared package(s) in {ElapsedMilliseconds} ms",
                preparedCandidates.Count,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new PackageSessionLoadResult(session, warnings.ToArray(), errors.ToArray());
        }
        finally
        {
            await DisposeExecutionRuntimeRetentionsAsync(preparedCandidates).ConfigureAwait(false);
        }
    }

    private async ValueTask<PackageActivationAttempt> ActivatePreparedPackageAsync(
        PreparedRuntimePackage package,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var started = Stopwatch.GetTimestamp();
        var activation = await _activator.ActivateAsync(
                package,
                warnings,
                errors,
                cancellationToken)
            .ConfigureAwait(false);
        return new PackageActivationAttempt(
            package,
            activation,
            warnings,
            errors,
            Stopwatch.GetElapsedTime(started));
    }

    private static async Task DisposeExecutionRuntimeRetentionsAsync(
        IEnumerable<PreparedRuntimePackage> packages)
    {
        foreach (var package in packages)
        {
            if (package.ExecutionRuntimeRetention is { } retention)
            {
                await retention.DisposeAsync().ConfigureAwait(false);
            }
            if (package.PackageProjectionLease is { } projectionLease)
            {
                await projectionLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup for failed reload attempts.
        }
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, string sessionFolder)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        TryDeleteDirectory(sessionFolder);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed record InstalledPreparationResult(
        string PackageId,
        PreparedRuntimePackage? PreparedPackage,
        SessionPackageDescriptor? SessionPackage,
        IReadOnlyList<string> Errors);

    private sealed record PackageActivationAttempt(
        PreparedRuntimePackage Package,
        PackageActivationResult Activation,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Errors,
        TimeSpan Elapsed);

}
