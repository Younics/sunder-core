using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeStore : IExecutionRuntimeRetentionManager
{
    internal static readonly TimeSpan GarbageCollectionGraceInterval = TimeSpan.FromDays(1);

    private const int TombstoneDeletionAttemptCount = 5;
    private static readonly TimeSpan TombstoneDeletionRetryDelay = TimeSpan.FromMilliseconds(50);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    private readonly ExecutionRuntimeStoreLayout _layout;
    private readonly ExecutionRuntimeHostTrustService _trust;
    private readonly IExecutionRuntimeGarbageCollectionAuthority _garbageCollectionAuthority;
    private readonly ISunderExecutionRuntimeArchiveSignatureVerifier _signatureVerifier;
    private readonly IExecutionRuntimeVerificationIoObserver? _verificationIoObserver;
    private readonly TimeProvider _timeProvider;
    private readonly IExecutionRuntimeStoreFaultInjector? _faultInjector;
    private readonly ExecutionRuntimeStoreRecovery _recovery;
    private readonly ExecutionRuntimeLeaseRegistry _leases;
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;
    private readonly bool _developmentAllowInsecureLoopbackRegistry;
    private readonly ExecutionRuntimeRetentionRegistry _retentions = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ExecutionRuntimeSingleFlight> _singleFlights =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _deferredCommittedTombstones =
        new(PathComparer);
    private volatile bool _initialized;

    public ExecutionRuntimeStore(
        ExecutionRuntimeStoreLayout layout,
        ExecutionRuntimeHostTrustService trust,
        IExecutionRuntimeGarbageCollectionAuthority garbageCollectionAuthority,
        TimeProvider? timeProvider = null,
        IExecutionRuntimeStoreFaultInjector? faultInjector = null,
        ISunderExecutionRuntimeArchiveSignatureVerifier? signatureVerifier = null,
        Guid? hostInstanceId = null,
        IExecutionRuntimeProcessController? processController = null,
        IExecutionRuntimeProcessContainmentController? containmentController = null,
        IExecutionRuntimeVerificationIoObserver? verificationIoObserver = null,
        ExecutionRuntimePhaseTelemetry? telemetry = null,
        bool developmentAllowInsecureLoopbackRegistry = false)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _garbageCollectionAuthority = garbageCollectionAuthority
                                      ?? throw new ArgumentNullException(nameof(garbageCollectionAuthority));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultInjector = faultInjector;
        _signatureVerifier = signatureVerifier ?? new ExecutionRuntimeEd25519SignatureVerifier();
        _verificationIoObserver = verificationIoObserver;
        _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;
        _developmentAllowInsecureLoopbackRegistry = developmentAllowInsecureLoopbackRegistry;
        _leases = new ExecutionRuntimeLeaseRegistry(
            _layout,
            hostInstanceId is { } instanceId && instanceId != Guid.Empty ? instanceId : Guid.NewGuid(),
            _timeProvider,
            processController,
            containmentController,
            _telemetry);
        _recovery = new ExecutionRuntimeStoreRecovery(_layout, this);
    }

    internal ExecutionRuntimeStoreLayout Layout => _layout;

    internal Guid HostInstanceId => _leases.HostInstanceId;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }
            await _recovery.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task<RuntimeFileLock> EnterInitializedRecoveryReadAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            var recoveryFence = await RuntimeFileLock.AcquireSharedAsync(
                    _layout.RecoveryFencePath,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (_initialized && !HasPendingRecoveryEvidence())
                {
                    return recoveryFence;
                }
            }
            catch
            {
                recoveryFence.Dispose();
                throw;
            }
            recoveryFence.Dispose();
            _initialized = false;
        }
    }

    private bool HasPendingRecoveryEvidence()
        => File.Exists(_layout.LeaseRecoveryFailurePath)
           || Directory.EnumerateDirectories(_layout.TombstoneRootPath)
               .Any(path => !PathsEqual(path, _layout.GarbageCollectionMarkRootPath)
                            && !_deferredCommittedTombstones.ContainsKey(Path.GetFullPath(path)));

    public Task<ExecutionRuntimeEnsureResult> ImportLocalArchiveAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.AcquisitionSource != RuntimeExecutionRuntimeAcquisitionSources.LocalImport)
        {
            throw new ArgumentException(
                "Local archive import requires the local-import acquisition source.",
                nameof(plan));
        }
        return EnsureAsync(plan, archivePath, cancellationToken);
    }

    public async Task<ExecutionRuntimeEnsureResult> ImportLocalArchiveAsync(
        string archivePath,
        string artifactSha256,
        long artifactSize,
        CancellationToken cancellationToken = default)
    {
        var (manifest, manifestSha256) = await SunderExecutionRuntimeArchiveInspector
            .ReadManifestMetadataAsync(archivePath, cancellationToken)
            .ConfigureAwait(false);
        var currentRid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        if (!string.Equals(manifest.Rid, currentRid, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Execution runtime import RID '{manifest.Rid}' does not match current Host RID '{currentRid}'.");
        }

        var plan = new ExecutionRuntimeAcquisitionPlan(
            new ExecutionRuntimeIdentity(
                manifest.Id!,
                manifest.Version!,
                manifest.PackagingRevision,
                manifest.Rid!,
                manifest.ModuleAbi!),
            artifactSha256,
            artifactSize,
            manifestSha256,
            ContentIdentity: null,
            manifest.Signing!.KeyId!,
            _trust.Generation,
            ExecutionRuntimeTrustEnvironment.Production,
            RuntimeExecutionRuntimeAcquisitionSources.LocalImport,
            RegistryOrigin: null,
            manifest.Provenance!.AttestationSha256,
            manifest.Provenance.AttestationKeyId);
        return await ImportLocalArchiveAsync(plan, archivePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionRuntimeEnsureResult?> RepairAsync(
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var receipt = await GetReceiptAsync(identity, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }

        var plan = new ExecutionRuntimeAcquisitionPlan(
            receipt.Identity,
            receipt.ArtifactSha256,
            receipt.ArtifactSize,
            receipt.ManifestSha256,
            receipt.ContentIdentity,
            receipt.SigningKeyId,
            receipt.TrustGeneration,
            receipt.TrustEnvironment,
            receipt.AcquisitionSource,
            receipt.RegistryOrigin,
            receipt.NativeAttestationSha256,
            receipt.NativeAttestationKeyId);
        return await EnsureAsync(
                plan,
                _layout.GetArchivePath(receipt.ArtifactSha256),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ExecutionRuntimeEnsureResult> EnsureAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        string? archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate(_developmentAllowInsecureLoopbackRegistry);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return EnsureSourceCoreAsync(
                plan,
                operationToken => CaptureArchiveSourceAsync(plan, archivePath, null, operationToken),
                cancellationToken);
        }

        var fullPath = Path.GetFullPath(archivePath);
        PackageStorePathGuard.EnsureNotReparsePoint(fullPath);
        FileStream? source = null;
        try
        {
            source = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            PackageStorePathGuard.EnsureNotReparsePoint(fullPath);
        }
        catch
        {
            source?.Dispose();
            throw;
        }
        return EnsureTransferredArchiveStreamAsync(
            plan,
            fullPath,
            source,
            cancellationToken);
    }

    internal Task<ExecutionRuntimeEnsureResult> EnsureOwnedArchiveAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        ExecutionRuntimeOwnedArchive ownedArchive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownedArchive);
        return EnsureTransferredOwnedArchiveAsync(plan, ownedArchive, cancellationToken);
    }

    internal Task<ExecutionRuntimeEnsureResult> EnsureArchiveAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        Func<CancellationToken, Task<ExecutionRuntimeOwnedArchive>> sourceFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        return EnsureSourceCoreAsync(
            plan,
            async operationToken => await sourceFactory(operationToken).ConfigureAwait(false),
            cancellationToken,
            allowExactLocalImportReuse: true);
    }

    private async Task<ExecutionRuntimeEnsureResult> EnsureTransferredOwnedArchiveAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        ExecutionRuntimeOwnedArchive ownedArchive,
        CancellationToken cancellationToken)
    {
        var ownership = 0;
        try
        {
            return await EnsureSourceCoreAsync(
                    plan,
                    _ =>
                    {
                        if (Interlocked.CompareExchange(ref ownership, 3, 1) != 1)
                        {
                            throw new InvalidOperationException("The owned execution-runtime archive was already claimed.");
                        }
                        return Task.FromResult<ExecutionRuntimeOwnedArchive?>(ownedArchive);
                    },
                    cancellationToken,
                    claimSource: () =>
                    {
                        if (Interlocked.CompareExchange(ref ownership, 1, 0) != 0)
                        {
                            throw new InvalidOperationException("The owned execution-runtime archive was already transferred.");
                        }
                    },
                    discardSource: () =>
                    {
                        if (Interlocked.CompareExchange(ref ownership, 2, 0) == 0)
                        {
                            ownedArchive.Dispose();
                        }
                    },
                    releaseClaimedSource: () =>
                    {
                        if (Interlocked.CompareExchange(ref ownership, 2, 1) == 1)
                        {
                            ownedArchive.Dispose();
                        }
                    })
                .ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref ownership, 2, 0) == 0)
            {
                ownedArchive.Dispose();
            }
        }
    }

    private async Task<ExecutionRuntimeEnsureResult> EnsureTransferredArchiveStreamAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        string archivePath,
        FileStream source,
        CancellationToken cancellationToken)
    {
        var ownership = 0;
        try
        {
            return await EnsureSourceCoreAsync(
                    plan,
                    async operationToken =>
                    {
                        if (Interlocked.CompareExchange(ref ownership, 3, 1) != 1)
                        {
                            throw new InvalidOperationException("The execution-runtime archive stream was already claimed.");
                        }
                        using (source)
                        {
                            return await CaptureArchiveSourceAsync(
                                    plan,
                                    archivePath,
                                    source,
                                    operationToken)
                                .ConfigureAwait(false);
                        }
                    },
                    cancellationToken,
                    claimSource: () =>
                    {
                        if (Interlocked.CompareExchange(ref ownership, 1, 0) != 0)
                        {
                            throw new InvalidOperationException("The execution-runtime archive stream was already transferred.");
                        }
                    },
                    discardSource: () =>
                    {
                        if (Interlocked.CompareExchange(ref ownership, 2, 0) == 0)
                        {
                            source.Dispose();
                        }
                    },
                    releaseClaimedSource: () =>
                    {
                        if (Interlocked.CompareExchange(ref ownership, 2, 1) == 1)
                        {
                            source.Dispose();
                        }
                    })
                .ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref ownership, 2, 0) == 0)
            {
                source.Dispose();
            }
        }
    }

    private async Task<ExecutionRuntimeEnsureResult> EnsureSourceCoreAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        Func<CancellationToken, Task<ExecutionRuntimeOwnedArchive?>> sourceFactory,
        CancellationToken cancellationToken,
        Action? claimSource = null,
        Action? discardSource = null,
        Action? releaseClaimedSource = null,
        bool allowExactLocalImportReuse = false)
    {
        using var operation = _telemetry.BeginOperation();
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        plan.Validate(_developmentAllowInsecureLoopbackRegistry);
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var flightKey = plan.Identity.SingleFlightKey;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_singleFlights.TryGetValue(flightKey, out var existing))
            {
                if (existing.TryJoin(plan, out var planConflict))
                {
                    discardSource?.Invoke();
                    return await WaitForFlightAsync(flightKey, existing, cancellationToken).ConfigureAwait(false);
                }
                if (planConflict)
                {
                    throw new ExecutionRuntimePlanMismatchException(
                        "Concurrent acquisition for the exact execution runtime identity used conflicting immutable plans.");
                }

                try
                {
                    await existing.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A closed flight's outcome belongs to its waiters; this caller retries with its own source.
                }
                _singleFlights.TryRemove(
                    new KeyValuePair<string, ExecutionRuntimeSingleFlight>(flightKey, existing));
                continue;
            }

            var candidate = new ExecutionRuntimeSingleFlight(
                plan,
                operationToken => EnsureCoreAsync(
                    plan,
                    sourceFactory,
                    allowExactLocalImportReuse,
                    operationToken),
                releaseClaimedSource);
            if (_singleFlights.TryAdd(flightKey, candidate))
            {
                claimSource?.Invoke();
                _ = candidate.Task.ContinueWith(
                    static (_, state) =>
                    {
                        var cleanup = ((ExecutionRuntimeStore Store, string Key, ExecutionRuntimeSingleFlight Flight))state!;
                        cleanup.Store._singleFlights.TryRemove(
                            new KeyValuePair<string, ExecutionRuntimeSingleFlight>(cleanup.Key, cleanup.Flight));
                    },
                    (this, flightKey, candidate),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                candidate.Start();
                return await WaitForFlightAsync(flightKey, candidate, cancellationToken).ConfigureAwait(false);
            }
            candidate.DisposeUnused();
        }
    }

    public async Task<ExecutionRuntimeReceiptV1?> GetReceiptAsync(
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var recoveryFence = await EnterInitializedRecoveryReadAsync(cancellationToken).ConfigureAwait(false);
        using var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var path = _layout.GetReceiptPath(identity);
        if (!File.Exists(path))
        {
            return null;
        }
        var receipt = await ReadReceiptAsync(path, cancellationToken).ConfigureAwait(false);
        ValidateReceipt(receipt, path);
        return receipt;
    }

    internal async Task<IReadOnlyList<ExecutionRuntimeReceiptV1>> ListReceiptsAsync(
        CancellationToken cancellationToken = default)
    {
        using var recoveryFence = await EnterInitializedRecoveryReadAsync(cancellationToken).ConfigureAwait(false);
        using var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var receipts = new List<ExecutionRuntimeReceiptV1>();
        foreach (var path in EnumerateFiles(_layout.ReceiptRootPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = await ReadReceiptAsync(path, cancellationToken).ConfigureAwait(false);
            ValidateReceipt(receipt, path);
            receipts.Add(receipt);
        }
        return Array.AsReadOnly(receipts
            .OrderBy(static receipt => receipt.Identity.SingleFlightKey, StringComparer.Ordinal)
            .ToArray());
    }

    public async Task<ExecutionRuntimeVerificationResult> VerifyAsync(
        ExecutionRuntimeIdentity identity,
        bool deep,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var recoveryFence = await EnterInitializedRecoveryReadAsync(cancellationToken).ConfigureAwait(false);
        using var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var receiptPath = _layout.GetReceiptPath(identity);
        if (!File.Exists(receiptPath))
        {
            return new ExecutionRuntimeVerificationResult(
                false,
                deep,
                _timeProvider.GetUtcNow(),
                "The execution runtime receipt does not exist.");
        }

        ExecutionRuntimeReceiptV1 receipt;
        try
        {
            receipt = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
            ValidateReceipt(receipt, receiptPath);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new ExecutionRuntimeVerificationResult(
                false,
                deep,
                _timeProvider.GetUtcNow(),
                exception.Message);
        }
        return await VerifyReceiptAsync(receipt, deep, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionRuntimeGarbageCollectionResult> CollectGarbageAsync(
        TimeSpan minimumUnreferencedAge,
        CancellationToken cancellationToken = default)
    {
        using var operation = _telemetry.BeginOperation();
        using var phase = _telemetry.BeginPhase(ExecutionRuntimePhaseNames.GarbageCollection);
        try
        {
            if (minimumUnreferencedAge < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumUnreferencedAge));
            }
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            var result = await _garbageCollectionAuthority.ExecuteAsync(
                    (authority, authorityToken) => CollectGarbageUnderAuthorityAsync(
                        authority,
                        minimumUnreferencedAge,
                        authorityToken),
                    cancellationToken)
                .ConfigureAwait(false);
            phase.Succeeded();
            return result;
        }
        catch (OperationCanceledException)
        {
            phase.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            phase.Failed(exception);
            throw;
        }
    }

    public Task<ExecutionRuntimeGarbageCollectionResult> CollectGarbageAsync(
        CancellationToken cancellationToken = default)
        => CollectGarbageAsync(GarbageCollectionGraceInterval, cancellationToken);

    public async ValueTask<IAsyncDisposable> RetainAsync(
        ExecutionRuntimeRetention retention,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retention);
        ValidateRetention(retention);
        using var recoveryFence = await EnterInitializedRecoveryReadAsync(cancellationToken).ConfigureAwait(false);
        using var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        var crossProcessPin = await RuntimeFileLock.AcquireSharedAsync(
                _layout.GarbageCollectionRetentionFencePath,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return _retentions.RetainUnderFence(retention, crossProcessPin);
        }
        catch
        {
            crossProcessPin.Dispose();
            throw;
        }
    }

    private async Task<ExecutionRuntimeGarbageCollectionResult> CollectGarbageUnderAuthorityAsync(
        ExecutionRuntimeGarbageCollectionAuthoritySnapshot authority,
        TimeSpan minimumUnreferencedAge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);

        using var recoveryFence = await RuntimeFileLock.AcquireExclusiveAsync(
                _layout.RecoveryFencePath,
                cancellationToken)
            .ConfigureAwait(false);
        _initialized = false;
        var result = await CollectGarbageCoreUnderAuthorityAsync(
                authority,
                minimumUnreferencedAge,
                cancellationToken)
            .ConfigureAwait(false);
        _initialized = true;
        return result;
    }

    private async Task<ExecutionRuntimeGarbageCollectionResult> CollectGarbageCoreUnderAuthorityAsync(
        ExecutionRuntimeGarbageCollectionAuthoritySnapshot authority,
        TimeSpan minimumUnreferencedAge,
        CancellationToken cancellationToken)
    {

        using var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        using var crossProcessPin = RuntimeFileLock.TryAcquireExclusive(
            _layout.GarbageCollectionRetentionFencePath);
        if (crossProcessPin is null)
        {
            return new ExecutionRuntimeGarbageCollectionResult(0, 0, 0);
        }
        await RecoverTombstonesUnderFenceAsync(cancellationToken).ConfigureAwait(false);
        var receiptPaths = EnumerateFiles(_layout.ReceiptRootPath, "*.json");
        var archivePaths = EnumerateFiles(_layout.ArchiveRootPath, "*");
        var manifestPaths = EnumerateFiles(_layout.ManifestRootPath, "*");
        var allContent = EnumerateContentRootIdentitiesUnderFence();
        var boundIdentities = new HashSet<string>(StringComparer.Ordinal);
        var activeContent = new HashSet<string>(StringComparer.Ordinal);
        var reachableArchives = new HashSet<string>(StringComparer.Ordinal);
        var reachableManifests = new HashSet<string>(StringComparer.Ordinal);
        var protectedReceiptPaths = new HashSet<string>(PathComparer);

        foreach (var installedBinding in authority.PackageBindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(installedBinding, nameof(authority));
            var binding = RuntimePackageExecutionRuntimeBinding.FromInstalled(installedBinding);
            var receiptPath = _layout.GetReceiptPath(binding.Identity);
            protectedReceiptPaths.Add(receiptPath);
            if (!File.Exists(receiptPath))
            {
                throw new InvalidDataException(
                    $"Authoritative execution-runtime binding '{binding.Identity.SingleFlightKey}' has no immutable receipt.");
            }

            ExecutionRuntimeReceiptV1 receipt;
            try
            {
                receipt = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
                ValidateReceipt(receipt, receiptPath);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"Authoritative execution-runtime binding '{binding.Identity.SingleFlightKey}' has an invalid immutable receipt.",
                    exception);
            }
            if (!binding.MatchesReceipt(receipt))
            {
                throw new InvalidDataException(
                    $"Authoritative execution-runtime binding '{binding.Identity.SingleFlightKey}' does not match its immutable receipt.");
            }
            var verification = await VerifyReceiptAsync(
                    receipt,
                    deep: false,
                    cancellationToken,
                    persistHealthMarker: false)
                .ConfigureAwait(false);
            if (!verification.Healthy)
            {
                throw new InvalidDataException(
                    $"Authoritative execution-runtime binding '{binding.Identity.SingleFlightKey}' failed integrity verification: {verification.Message}");
            }

            boundIdentities.Add(binding.Identity.SingleFlightKey);
            activeContent.Add(receipt.ContentIdentity);
            reachableArchives.Add(binding.RuntimeArtifactSha256);
            reachableManifests.Add(binding.RuntimeManifestSha256);
        }

        foreach (var retention in _retentions.SnapshotUnderFence())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retention.ContentIdentity is not null)
            {
                activeContent.Add(retention.ContentIdentity);
            }
            if (retention.ArtifactSha256 is not null)
            {
                reachableArchives.Add(retention.ArtifactSha256);
            }
            if (retention.ManifestSha256 is not null)
            {
                reachableManifests.Add(retention.ManifestSha256);
            }
            if (retention.Identity is null)
            {
                continue;
            }

            boundIdentities.Add(retention.Identity.SingleFlightKey);
            var receiptPath = _layout.GetReceiptPath(retention.Identity);
            protectedReceiptPaths.Add(receiptPath);
            if (!File.Exists(receiptPath))
            {
                continue;
            }
            var receipt = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
            ValidateReceipt(receipt, receiptPath);
            if (!RetentionMatchesReceipt(retention, receipt))
            {
                throw new InvalidDataException(
                    $"Execution-runtime retention for '{retention.Identity.SingleFlightKey}' does not match its immutable receipt.");
            }
            var verification = await VerifyReceiptAsync(
                    receipt,
                    deep: false,
                    cancellationToken,
                    persistHealthMarker: false)
                .ConfigureAwait(false);
            if (!verification.Healthy)
            {
                throw new InvalidDataException(
                    $"Execution-runtime retention for '{retention.Identity.SingleFlightKey}' failed integrity verification: {verification.Message}");
            }
            activeContent.Add(receipt.ContentIdentity);
            reachableArchives.Add(receipt.ArtifactSha256);
            reachableManifests.Add(receipt.ManifestSha256);
        }

        var leasedContentIdentities = await _leases
            .ReadActiveContentIdentitiesUnderFenceAsync(cancellationToken)
            .ConfigureAwait(false);
        activeContent.UnionWith(leasedContentIdentities);
        var pendingReceipts = await ReadPendingTransactionReceiptsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var pendingReceipt in pendingReceipts)
        {
            activeContent.Add(pendingReceipt.ContentIdentity);
            protectedReceiptPaths.Add(_layout.GetReceiptPath(pendingReceipt.Identity));
        }

        var receipts = new List<(string Path, ExecutionRuntimeReceiptV1 Receipt)>();
        foreach (var receiptPath in receiptPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var receipt = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
                ValidateReceipt(receipt, receiptPath);
                receipts.Add((receiptPath, receipt));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                var protectedByAuthority = protectedReceiptPaths.Contains(receiptPath);
                var mayBeLeased = leasedContentIdentities.Count > 0;
                if (protectedByAuthority || mayBeLeased)
                {
                    throw new InvalidDataException(
                        protectedByAuthority
                            ? $"Authoritative execution-runtime receipt '{receiptPath}' became invalid during garbage collection."
                            : $"Execution-runtime receipt '{receiptPath}' is invalid and may be reachable from an active lease; garbage collection cannot prove ownership.",
                        exception);
                }

                QuarantineUnderFence(receiptPath, "invalid-receipt");
            }
        }

        var receiptsByContent = receipts
            .GroupBy(static item => item.Receipt.ContentIdentity, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        allContent.UnionWith(receiptsByContent.Keys);
        allContent.UnionWith(activeContent);
        var now = _timeProvider.GetUtcNow();
        var retainedReceipts = new List<ExecutionRuntimeReceiptV1>();
        var rootsRemoved = 0;
        foreach (var contentIdentity in allContent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentReceipts = receiptsByContent.GetValueOrDefault(contentIdentity) ?? [];
            var retained = activeContent.Contains(contentIdentity)
                           || contentReceipts.Any(item =>
                               boundIdentities.Contains(item.Receipt.Identity.SingleFlightKey));
            if (retained)
            {
                DeleteGarbageCollectionMarkUnderFence(contentIdentity);
                retainedReceipts.AddRange(contentReceipts.Select(static item => item.Receipt));
                continue;
            }

            var (mark, markCreated) = await GetOrCreateGarbageCollectionMarkUnderFenceAsync(
                    contentIdentity,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (markCreated || now - mark.UnreferencedAtUtc < minimumUnreferencedAge)
            {
                retainedReceipts.AddRange(contentReceipts.Select(static item => item.Receipt));
                continue;
            }

            var rootExisted = Directory.Exists(_layout.GetContentRootPath(contentIdentity));
            var completed = await TombstoneContentUnderFenceAsync(
                    contentIdentity,
                    contentReceipts,
                    cancellationToken)
                .ConfigureAwait(false);
            if (rootExisted && !Directory.Exists(_layout.GetContentRootPath(contentIdentity)))
            {
                rootsRemoved++;
            }
            if (!completed)
            {
                retainedReceipts.AddRange(contentReceipts.Select(static item => item.Receipt));
            }
        }
        CleanupOrphanGarbageCollectionMarksUnderFence(allContent);

        foreach (var receipt in retainedReceipts.Concat(pendingReceipts))
        {
            reachableArchives.Add(receipt.ArtifactSha256);
            reachableManifests.Add(receipt.ManifestSha256);
        }
        var cutoff = now - minimumUnreferencedAge;
        var archivesRemoved = CollectContentAddressedFiles(
            archivePaths,
            reachableArchives,
            cutoff,
            cancellationToken);
        var manifestsRemoved = CollectContentAddressedFiles(
            manifestPaths,
            reachableManifests,
            cutoff,
            cancellationToken);
        return new ExecutionRuntimeGarbageCollectionResult(
            rootsRemoved,
            archivesRemoved,
            manifestsRemoved);
    }

    internal async Task<IExecutionRuntimeProcessLease> ReserveProcessLeaseAsync(
        ExecutionRuntimeLeaseOwner owner,
        string contentIdentity,
        string runtimeRootPath,
        string executablePath,
        CancellationToken cancellationToken)
        => await ReserveProcessLeaseAsync(
                owner,
                contentIdentity,
                runtimeRootPath,
                executablePath,
                identity: null,
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<IExecutionRuntimeProcessLease> ReserveProcessLeaseAsync(
        ExecutionRuntimeLeaseOwner owner,
        string contentIdentity,
        string runtimeRootPath,
        string executablePath,
        ExecutionRuntimeIdentity? identity,
        CancellationToken cancellationToken)
    {
        using var recoveryFence = await EnterInitializedRecoveryReadAsync(cancellationToken).ConfigureAwait(false);
        return await _leases.ReserveAsync(
                owner,
                contentIdentity,
                runtimeRootPath,
                executablePath,
                identity,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task RecoverProcessLeasesAsync(CancellationToken cancellationToken)
        => _leases.RecoverAsync(cancellationToken);

    internal async Task RecoverTombstonesAsync(CancellationToken cancellationToken)
    {
        using var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        await RecoverTombstonesUnderFenceAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ExecutionRuntimeReceiptV1> ReadReceiptAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > SunderExecutionRuntimeFormat.MaxMetadataBytes)
            {
                throw new InvalidDataException($"Execution runtime receipt '{path}' is too large.");
            }
            return await JsonSerializer.DeserializeAsync<ExecutionRuntimeReceiptV1>(
                       stream,
                       JsonOptions,
                       cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException($"Execution runtime receipt '{path}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Execution runtime receipt '{path}' is invalid.", exception);
        }
    }

    internal void ValidateReceipt(ExecutionRuntimeReceiptV1 receipt, string? receiptPath = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaVersion != ExecutionRuntimeReceiptV1.CurrentSchemaVersion
            || receipt.Identity is null
            || receipt.Identity.PackagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision
            || !string.Equals(
                receipt.WorkerProtocol,
                SunderExecutionRuntimeFormat.WorkerProtocol,
                StringComparison.Ordinal)
            || receipt.ArtifactSize is <= 0 or > SunderExecutionRuntimeFormat.MaxArchiveBytes
            || receipt.LauncherSize <= 0
            || receipt.LauncherArguments is null
            || receipt.Content is null
            || receipt.Content.Count == 0
            || receipt.Content.Count > SunderExecutionRuntimeFormat.MaxContentIndexEntries
            || receipt.TrustGeneration <= 0
            || !Enum.IsDefined(receipt.TrustEnvironment)
            || receipt.InstalledAtUtc == default)
        {
            throw InvalidReceipt(receiptPath);
        }
        ValidateReceiptSegment(receipt.Identity.RuntimeId, nameof(receipt.Identity.RuntimeId), receiptPath);
        ValidateReceiptSegment(receipt.Identity.Version, nameof(receipt.Identity.Version), receiptPath);
        ValidateReceiptSegment(receipt.Identity.Rid, nameof(receipt.Identity.Rid), receiptPath);
        ValidateReceiptSegment(receipt.Identity.ModuleAbi, nameof(receipt.Identity.ModuleAbi), receiptPath);
        ValidateReceiptSegment(receipt.SigningKeyId, nameof(receipt.SigningKeyId), receiptPath);
        ValidateReceiptSha256(receipt.ArtifactSha256, nameof(receipt.ArtifactSha256), receiptPath);
        ValidateReceiptSha256(receipt.ManifestSha256, nameof(receipt.ManifestSha256), receiptPath);
        ValidateReceiptSha256(receipt.ContentIdentity, nameof(receipt.ContentIdentity), receiptPath);
        ValidateReceiptSha256(receipt.LauncherSha256, nameof(receipt.LauncherSha256), receiptPath);
        ValidateReceiptSha256(
            receipt.NativeAttestationSha256,
            nameof(receipt.NativeAttestationSha256),
            receiptPath);
        ValidateReceiptSegment(
            receipt.NativeAttestationKeyId,
            nameof(receipt.NativeAttestationKeyId),
            receiptPath);
        ValidateLauncherArguments(receipt, receiptPath);

        if (receipt.AcquisitionSource is not (
                RuntimeExecutionRuntimeAcquisitionSources.Registry
                or RuntimeExecutionRuntimeAcquisitionSources.LocalImport
                or RuntimeExecutionRuntimeAcquisitionSources.Development)
            || (receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Development)
            != (receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development)
            || receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Registry
            && (!Uri.TryCreate(receipt.RegistryOrigin, UriKind.Absolute, out var registryOrigin)
                || !string.Equals(registryOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            || receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development
            && receipt.RegistryOrigin is not null
            && (!Uri.TryCreate(receipt.RegistryOrigin, UriKind.Absolute, out var developmentRegistryOrigin)
                || !string.Equals(developmentRegistryOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                   && (!_developmentAllowInsecureLoopbackRegistry
                       || !string.Equals(
                           developmentRegistryOrigin.Scheme,
                           Uri.UriSchemeHttp,
                           StringComparison.Ordinal)
                       || !developmentRegistryOrigin.IsLoopback
                       || !string.IsNullOrEmpty(developmentRegistryOrigin.UserInfo))))
        {
            throw InvalidReceipt(receiptPath);
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var content in receipt.Content)
        {
            if (content is null
                || string.IsNullOrEmpty(content.Path)
                || !SunderExecutionRuntimeFormat.IsPayloadPath(content.Path)
                || !paths.Add(content.Path)
                || previous is not null && string.Compare(previous, content.Path, StringComparison.Ordinal) >= 0
                || content.Size < 0)
            {
                throw InvalidReceipt(receiptPath);
            }
            ValidateReceiptSha256(content.Sha256, nameof(content.Sha256), receiptPath);
            previous = content.Path;
        }
        var launcher = receipt.Content.FirstOrDefault(content =>
            string.Equals(content.Path, receipt.LauncherPath, StringComparison.Ordinal));
        if (launcher is null
            || launcher.Size != receipt.LauncherSize
            || !string.Equals(launcher.Sha256, receipt.LauncherSha256, StringComparison.Ordinal))
        {
            throw InvalidReceipt(receiptPath);
        }
        if (receiptPath is not null
            && !PathsEqual(receiptPath, _layout.GetReceiptPath(receipt.Identity)))
        {
            throw InvalidReceipt(receiptPath);
        }
    }

    private static void ValidateLauncherArguments(
        ExecutionRuntimeReceiptV1 receipt,
        string? receiptPath)
    {
        var arguments = receipt.LauncherArguments;
        var validRecipe = receipt.Identity.RuntimeId switch
        {
            SunderExecutionRuntimeFormat.DotNetRuntimeId => arguments.Count == 2
                && string.Equals(arguments[0], SunderExecutionRuntimeFormat.DotNetExecArgument, StringComparison.Ordinal)
                && string.Equals(arguments[1], SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder, StringComparison.Ordinal),
            SunderExecutionRuntimeFormat.NodeRuntimeId => arguments.Count == 1
                && string.Equals(arguments[0], SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder, StringComparison.Ordinal),
            _ => false,
        };
        if (!validRecipe)
        {
            throw InvalidReceipt(receiptPath);
        }
    }

    private static void ValidateReceiptSegment(string value, string parameterName, string? receiptPath)
    {
        try
        {
            ExecutionRuntimeStoreLayout.ValidateSegment(value, parameterName);
        }
        catch (ArgumentException)
        {
            throw InvalidReceipt(receiptPath);
        }
    }

    private static void ValidateReceiptSha256(string value, string parameterName, string? receiptPath)
    {
        try
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(value, parameterName);
        }
        catch (ArgumentException)
        {
            throw InvalidReceipt(receiptPath);
        }
    }

    internal async Task<bool> TryCompleteRecoveryAsync(
        ExecutionRuntimeStoreTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        ValidateJournal(journal);
        var receipt = journal.Receipt;
        var archivePath = _layout.GetArchivePath(receipt.ArtifactSha256);
        var manifestPath = _layout.GetManifestPath(receipt.ManifestSha256);
        var rootPath = _layout.GetContentRootPath(receipt.ContentIdentity);
        if (!File.Exists(archivePath)
            || !File.Exists(manifestPath)
            || !Directory.Exists(rootPath))
        {
            return false;
        }

        using var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        if (journal.Phase == ExecutionRuntimeStoreTransactionPhase.RootSealed)
        {
            ExecutionRuntimeRootFileSystem.Seal(
                rootPath,
                GetArchiveRelativePath(rootPath, receipt.LauncherPath));
        }
        var verification = await VerifyReceiptAsync(receipt, deep: true, cancellationToken)
            .ConfigureAwait(false);
        if (!verification.Healthy)
        {
            var receiptPath = _layout.GetReceiptPath(receipt.Identity);
            if (File.Exists(receiptPath))
            {
                QuarantineUnderFence(receiptPath, "failed-transaction-receipt");
            }
            return false;
        }

        await PublishReceiptAsync(receipt, cancellationToken, storeFenceHeld: true).ConfigureAwait(false);
        return true;
    }

    internal void ValidateJournal(ExecutionRuntimeStoreTransactionJournal journal)
    {
        if (journal.SchemaVersion != ExecutionRuntimeStoreTransactionJournal.CurrentSchemaVersion
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !Enum.IsDefined(journal.Phase)
            || journal.Receipt is null)
        {
            throw new InvalidDataException("The execution runtime transaction journal is invalid.");
        }
        ValidateReceipt(journal.Receipt);
    }

    internal void Quarantine(string path, string label)
    {
        using var fence = _leases.EnterFence();
        QuarantineUnderFence(path, label);
    }

    internal async Task QuarantineAsync(
        string path,
        string label,
        CancellationToken cancellationToken)
    {
        using var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false);
        QuarantineUnderFence(path, label);
    }

    private void QuarantineUnderFence(string path, string label)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        _layout.EnsureContained(path, _layout.RootPath);
        var isContentRoot = TryGetContentIdentity(path, out var contentIdentity);
        if (isContentRoot
            && _leases.HasActiveLeaseUnderFence(contentIdentity))
        {
            throw new ExecutionRuntimeRootInUseException(
                $"Execution runtime root '{contentIdentity}' is actively leased and cannot be quarantined.");
        }
        Directory.CreateDirectory(_layout.QuarantineRootPath);
        var destination = _layout.CreateQuarantinePath(label, _timeProvider.GetUtcNow());
        try
        {
            if (Directory.Exists(path))
            {
                if (isContentRoot)
                {
                    ExecutionRuntimeRootFileSystem.MakeOwnedRootMovable(path);
                }
                Directory.Move(path, destination);
            }
            else
            {
                File.Move(path, destination);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (isContentRoot && Directory.Exists(path))
            {
                ExecutionRuntimeRootFileSystem.SealRootDirectory(path);
            }
            throw new IOException($"Failed to quarantine execution runtime store path '{path}'.", exception);
        }
    }

    private async Task<ExecutionRuntimeEnsureResult> WaitForFlightAsync(
        string key,
        ExecutionRuntimeSingleFlight flight,
        CancellationToken cancellationToken)
    {
        var released = false;
        try
        {
            var result = await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            released = true;
            if (flight.ReleaseWaiter())
            {
                flight.CancelOperation();
                try
                {
                    await flight.Task.ConfigureAwait(false);
                }
                catch
                {
                    // The final caller observes its own cancellation after producer teardown.
                }
            }
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (!released)
            {
                flight.ReleaseWaiter();
            }
            if (flight.Task.IsCompleted)
            {
                _singleFlights.TryRemove(new KeyValuePair<string, ExecutionRuntimeSingleFlight>(key, flight));
            }
        }
    }

    private async Task<ExecutionRuntimeEnsureResult> EnsureCoreAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        Func<CancellationToken, Task<ExecutionRuntimeOwnedArchive?>> sourceFactory,
        bool allowExactLocalImportReuse,
        CancellationToken cancellationToken)
    {
        using var recoveryFence = await EnterInitializedRecoveryReadAsync(cancellationToken).ConfigureAwait(false);
        using var identityLock = await RuntimeFileLock.AcquireExclusiveAsync(
                _layout.GetSingleFlightLockPath(plan.Identity),
                cancellationToken)
            .ConfigureAwait(false);
        await using var operationRetention = await RetainAsync(
                new ExecutionRuntimeRetention(
                    plan.Identity,
                    plan.ContentIdentity,
                    plan.ArtifactSha256,
                    plan.ArtifactSize,
                    plan.ManifestSha256),
                cancellationToken)
            .ConfigureAwait(false);
        var receiptPath = _layout.GetReceiptPath(plan.Identity);
        using (var fence = await _leases.EnterFenceAsync(cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(receiptPath))
            {
                ExecutionRuntimeReceiptV1 existing;
                try
                {
                    existing = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
                    ValidateReceipt(existing, receiptPath);
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException)
                {
                    QuarantineUnderFence(receiptPath, "invalid-receipt");
                    existing = null!;
                }

                if (existing is not null)
                {
                    var reusingLocalImport = allowExactLocalImportReuse
                                             && ReceiptMatchesRegistryPlanForLocalImport(existing, plan);
                    if (!reusingLocalImport)
                    {
                        EnsureReceiptMatchesPlan(existing, plan);
                    }
                    _ = _trust.Resolve(
                        existing.SigningKeyId,
                        existing.TrustEnvironment,
                        plan.MinimumHostTrustGeneration);
                    var verification = await VerifyReceiptAsync(existing, deep: false, cancellationToken)
                        .ConfigureAwait(false);
                    if (!verification.Healthy)
                    {
                        verification = await VerifyReceiptAsync(existing, deep: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    if (verification.Healthy)
                    {
                        return new ExecutionRuntimeEnsureResult(existing, AlreadyPresent: true);
                    }
                    if (reusingLocalImport)
                    {
                        throw new InvalidDataException(
                            "The exact local-import execution runtime is unhealthy and cannot be replaced by Registry acquisition.");
                    }

                    var rootPath = _layout.GetContentRootPath(existing.ContentIdentity);
                    if (Directory.Exists(rootPath))
                    {
                        QuarantineUnderFence(rootPath, "unhealthy-root");
                    }
                    TryDeleteFile(_layout.GetHealthPath(existing.ContentIdentity));
                }
            }
        }

        using var ownedArchive = await sourceFactory(cancellationToken).ConfigureAwait(false);
        var suppliedArchivePath = ownedArchive?.Path;

        var transactionId = Guid.NewGuid().ToString("N");
        var stagingPath = _layout.GetStagingPath(transactionId);
        var extractedPath = Path.Combine(stagingPath, "root");
        var validatedArchivePath = Path.Combine(stagingPath, "validated.sunderruntime");
        var transactionPath = _layout.GetTransactionPath(transactionId);
        var journalPath = _layout.GetTransactionJournalPath(transactionId);
        var journalPrepared = false;
        try
        {
            Directory.CreateDirectory(stagingPath);
            Directory.CreateDirectory(transactionPath);
            Hit(ExecutionRuntimeStoreFaultPoint.ArchiveSourceOpening);
            var sourceArchivePath = SelectArchiveSource(plan, suppliedArchivePath);
            var trustedKey = _trust.Resolve(
                plan.SigningKeyId,
                plan.TrustEnvironment,
                plan.MinimumHostTrustGeneration);
            SunderExecutionRuntimeArchiveValidationResult validation;
            using (var inspection = _telemetry.BeginPhase(
                       ExecutionRuntimePhaseNames.Inspect,
                       plan.Identity,
                       plan.ArtifactSize,
                       cacheHit: false))
            {
                try
                {
                    validation = await SunderExecutionRuntimeArchiveInspector.InspectAsync(
                            sourceArchivePath,
                            validatedArchivePath,
                            extractedPath,
                            _signatureVerifier,
                            trustedKey,
                            new ArchiveInspectionTelemetryObserver(
                                _telemetry,
                                plan.Identity,
                                plan.ArtifactSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    ValidateArchiveResult(plan, validation);
                    inspection.Succeeded();
                }
                catch (OperationCanceledException)
                {
                    inspection.Canceled();
                    throw;
                }
                catch (Exception exception)
                {
                    inspection.Failed(exception);
                    throw;
                }
            }
            Hit(ExecutionRuntimeStoreFaultPoint.Validated);
            cancellationToken.ThrowIfCancellationRequested();

            var receipt = CreateReceipt(plan, validation);
            var journal = new ExecutionRuntimeStoreTransactionJournal(
                ExecutionRuntimeStoreTransactionJournal.CurrentSchemaVersion,
                transactionId,
                ExecutionRuntimeStoreTransactionPhase.Prepared,
                receipt);
            ExecutionRuntimeReceiptV1 committedReceipt;
            using (var publicationFence = await _leases.EnterFenceAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                journalPrepared = true;
                Hit(ExecutionRuntimeStoreFaultPoint.JournalPrepared);

                await PublishFileUnderFenceAsync(
                        validatedArchivePath,
                        _layout.GetArchivePath(receipt.ArtifactSha256),
                        receipt.ArtifactSha256,
                        receipt.ArtifactSize,
                        "archive-conflict")
                    .ConfigureAwait(false);
                journal = await SetJournalPhaseAsync(
                        journalPath,
                        journal,
                        ExecutionRuntimeStoreTransactionPhase.ArchivePublished)
                    .ConfigureAwait(false);
                Hit(ExecutionRuntimeStoreFaultPoint.ArchivePublished);

                var stagedManifestPath = Path.Combine(stagingPath, "manifest-object.json");
                File.Copy(
                    GetArchiveRelativePath(extractedPath, SunderExecutionRuntimeFormat.ManifestPath),
                    stagedManifestPath,
                    overwrite: false);
                await PublishFileUnderFenceAsync(
                        stagedManifestPath,
                        _layout.GetManifestPath(receipt.ManifestSha256),
                        receipt.ManifestSha256,
                        expectedSize: null,
                        "manifest-conflict")
                    .ConfigureAwait(false);
                journal = await SetJournalPhaseAsync(
                        journalPath,
                        journal,
                        ExecutionRuntimeStoreTransactionPhase.ManifestPublished)
                    .ConfigureAwait(false);
                Hit(ExecutionRuntimeStoreFaultPoint.ManifestPublished);

                ExecutionRuntimeRootFileSystem.PrepareForPublication(
                    extractedPath,
                    GetArchiveRelativePath(extractedPath, receipt.LauncherPath));
                journal = await SetJournalPhaseAsync(
                        journalPath,
                        journal,
                        ExecutionRuntimeStoreTransactionPhase.RootSealed)
                    .ConfigureAwait(false);
                Hit(ExecutionRuntimeStoreFaultPoint.RootSealed);

                await PublishRootAsync(extractedPath, receipt, storeFenceHeld: true).ConfigureAwait(false);
                journal = await SetJournalPhaseAsync(
                        journalPath,
                        journal,
                        ExecutionRuntimeStoreTransactionPhase.RootPublished)
                    .ConfigureAwait(false);
                Hit(ExecutionRuntimeStoreFaultPoint.RootPublished);

                committedReceipt = await PublishReceiptAsync(
                        receipt,
                        CancellationToken.None,
                        storeFenceHeld: true)
                    .ConfigureAwait(false);
                journal = await SetJournalPhaseAsync(
                        journalPath,
                        journal,
                        ExecutionRuntimeStoreTransactionPhase.ReceiptPublished)
                    .ConfigureAwait(false);
                Hit(ExecutionRuntimeStoreFaultPoint.ReceiptPublished);
            }

            var health = await VerifyReceiptAsync(committedReceipt, deep: true, CancellationToken.None)
                .ConfigureAwait(false);
            if (!health.Healthy)
            {
                throw new InvalidDataException(
                    $"The newly published execution runtime failed deep verification: {health.Message}");
            }
            Hit(ExecutionRuntimeStoreFaultPoint.HealthPublished);
            journal = await SetJournalPhaseAsync(
                    journalPath,
                    journal,
                    ExecutionRuntimeStoreTransactionPhase.Completed)
                .ConfigureAwait(false);
            Hit(ExecutionRuntimeStoreFaultPoint.Cleanup);
            TryDeleteDirectory(stagingPath);
            TryDeleteDirectory(transactionPath);
            return new ExecutionRuntimeEnsureResult(committedReceipt, AlreadyPresent: false);
        }
        catch (ExecutionRuntimeStoreSimulatedCrashException)
        {
            throw;
        }
        catch (ExecutionRuntimeStoreConflictException)
        {
            if (Directory.Exists(stagingPath))
            {
                Quarantine(stagingPath, "incoming-conflict");
            }
            TryDeleteDirectory(transactionPath);
            throw;
        }
        catch
        {
            if (journalPrepared)
            {
                await _recovery.RecoverTransactionAsync(transactionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                TryDeleteDirectory(stagingPath);
                TryDeleteDirectory(transactionPath);
            }
            throw;
        }
    }

    private string SelectArchiveSource(
        ExecutionRuntimeAcquisitionPlan plan,
        string? suppliedArchivePath)
    {
        if (!string.IsNullOrWhiteSpace(suppliedArchivePath) && File.Exists(suppliedArchivePath))
        {
            return Path.GetFullPath(suppliedArchivePath);
        }
        var storedArchivePath = _layout.GetArchivePath(plan.ArtifactSha256);
        if (File.Exists(storedArchivePath))
        {
            return storedArchivePath;
        }
        throw new FileNotFoundException(
            "No source archive is available for the exact execution runtime acquisition plan.",
            suppliedArchivePath);
    }

    private async Task<ExecutionRuntimeOwnedArchive?> CaptureArchiveSourceAsync(
        ExecutionRuntimeAcquisitionPlan plan,
        string? suppliedArchivePath,
        FileStream? stableSource,
        CancellationToken cancellationToken)
    {
        if (stableSource is null
            && (string.IsNullOrWhiteSpace(suppliedArchivePath) || !File.Exists(suppliedArchivePath)))
        {
            return null;
        }

        var captureId = Guid.NewGuid().ToString("N");
        var capturePath = _layout.GetStagingPath(captureId);
        var archivePath = Path.Combine(capturePath, "owned-source.sunderruntime");
        try
        {
            Directory.CreateDirectory(capturePath);
            await using var openedSource = stableSource is null
                ? new FileStream(
                    Path.GetFullPath(suppliedArchivePath!),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan)
                : null;
            var source = stableSource ?? openedSource!;
            if (source.Length != plan.ArtifactSize)
            {
                throw new ExecutionRuntimePlanMismatchException(
                    "The execution runtime archive size does not match its acquisition plan.");
            }
            await using var destination = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            while (copied < plan.ArtifactSize)
            {
                var read = await source.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, plan.ArtifactSize - copied)),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new ExecutionRuntimePlanMismatchException(
                        "The execution runtime archive ended before its acquisition-plan size.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
            }
            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new ExecutionRuntimePlanMismatchException(
                    "The execution runtime archive exceeds its acquisition-plan size.");
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new ExecutionRuntimeOwnedArchive(
                archivePath,
                () => TryDeleteDirectory(capturePath));
        }
        catch
        {
            TryDeleteDirectory(capturePath);
            throw;
        }
    }

    private void ValidateArchiveResult(
        ExecutionRuntimeAcquisitionPlan plan,
        SunderExecutionRuntimeArchiveValidationResult result)
    {
        if (!result.Success)
        {
            throw new InvalidDataException(
                "Execution runtime archive validation failed: " + string.Join(" ", result.Errors));
        }
        var manifest = result.Manifest!;
        var mismatch = !string.Equals(manifest.Id, plan.Identity.RuntimeId, StringComparison.Ordinal)
                       || !string.Equals(manifest.Version, plan.Identity.Version, StringComparison.Ordinal)
                       || manifest.PackagingRevision != plan.Identity.PackagingRevision
                       || !string.Equals(manifest.Rid, plan.Identity.Rid, StringComparison.Ordinal)
                       || !string.Equals(manifest.ModuleAbi, plan.Identity.ModuleAbi, StringComparison.Ordinal)
                       || !string.Equals(result.ArchiveSha256, plan.ArtifactSha256, StringComparison.Ordinal)
                       || result.ArchiveSize != plan.ArtifactSize
                       || !string.Equals(result.ManifestSha256, plan.ManifestSha256, StringComparison.Ordinal)
                       || plan.ContentIdentity is not null
                       && !string.Equals(result.ContentIdentity, plan.ContentIdentity, StringComparison.Ordinal)
                       || !string.Equals(manifest.Signing?.KeyId, plan.SigningKeyId, StringComparison.Ordinal)
                       || plan.NativeAttestationSha256 is not null
                       && !string.Equals(
                           manifest.Provenance?.AttestationSha256,
                           plan.NativeAttestationSha256,
                           StringComparison.Ordinal)
                       || plan.NativeAttestationKeyId is not null
                       && !string.Equals(
                           manifest.Provenance?.AttestationKeyId,
                           plan.NativeAttestationKeyId,
                           StringComparison.Ordinal);
        if (mismatch)
        {
            throw new ExecutionRuntimePlanMismatchException(
                "The validated execution runtime archive does not exactly match its acquisition plan.");
        }
    }

    private ExecutionRuntimeReceiptV1 CreateReceipt(
        ExecutionRuntimeAcquisitionPlan plan,
        SunderExecutionRuntimeArchiveValidationResult validation)
    {
        var manifest = validation.Manifest!;
        var launcher = validation.LauncherContentRecord!;
        return new ExecutionRuntimeReceiptV1(
            ExecutionRuntimeReceiptV1.CurrentSchemaVersion,
            plan.Identity,
            manifest.WorkerProtocol!,
            validation.ArchiveSha256!,
            validation.ArchiveSize,
            validation.ManifestSha256!,
            validation.ContentIdentity!,
            launcher.Path!,
            launcher.Sha256!,
            launcher.Size,
            manifest.Launch!.Arguments!.Select(static argument => argument!).ToArray(),
            validation.ContentInventory.Select(static content =>
                    new ExecutionRuntimeReceiptContentV1(content.Path!, content.Sha256!, content.Size))
                .ToArray(),
            manifest.Provenance!.AttestationSha256!,
            manifest.Provenance.AttestationKeyId!,
            plan.SigningKeyId,
            _trust.Generation,
            plan.TrustEnvironment,
            plan.AcquisitionSource,
            plan.RegistryOrigin,
            _timeProvider.GetUtcNow());
    }

    private void EnsureReceiptMatchesPlan(
        ExecutionRuntimeReceiptV1 receipt,
        ExecutionRuntimeAcquisitionPlan plan)
    {
        var matches = receipt.Identity == plan.Identity
                      && string.Equals(receipt.ArtifactSha256, plan.ArtifactSha256, StringComparison.Ordinal)
                      && receipt.ArtifactSize == plan.ArtifactSize
                      && string.Equals(receipt.ManifestSha256, plan.ManifestSha256, StringComparison.Ordinal)
                      && (plan.ContentIdentity is null
                          || string.Equals(receipt.ContentIdentity, plan.ContentIdentity, StringComparison.Ordinal))
                      && string.Equals(receipt.SigningKeyId, plan.SigningKeyId, StringComparison.Ordinal)
                      && (plan.NativeAttestationSha256 is null
                          || string.Equals(
                              receipt.NativeAttestationSha256,
                              plan.NativeAttestationSha256,
                              StringComparison.Ordinal))
                      && (plan.NativeAttestationKeyId is null
                          || string.Equals(
                              receipt.NativeAttestationKeyId,
                              plan.NativeAttestationKeyId,
                              StringComparison.Ordinal))
                      && receipt.TrustEnvironment == plan.TrustEnvironment
                      && string.Equals(receipt.AcquisitionSource, plan.AcquisitionSource, StringComparison.Ordinal)
                      && string.Equals(receipt.RegistryOrigin, plan.RegistryOrigin, StringComparison.Ordinal);
        if (!matches)
        {
            throw new ExecutionRuntimeStoreConflictException(
                "The exact execution runtime identity already has a different immutable receipt.");
        }
    }

    private static bool ReceiptMatchesRegistryPlanForLocalImport(
        ExecutionRuntimeReceiptV1 receipt,
        ExecutionRuntimeAcquisitionPlan plan)
        => plan.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Registry
           && plan.RegistryOrigin is not null
           && receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.LocalImport
           && receipt.RegistryOrigin is null
           && receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Production
           && receipt.TrustGeneration >= plan.MinimumHostTrustGeneration
           && receipt.Identity == plan.Identity
           && string.Equals(receipt.ArtifactSha256, plan.ArtifactSha256, StringComparison.Ordinal)
           && receipt.ArtifactSize == plan.ArtifactSize
           && string.Equals(receipt.ManifestSha256, plan.ManifestSha256, StringComparison.Ordinal)
           && (plan.ContentIdentity is null
               || string.Equals(receipt.ContentIdentity, plan.ContentIdentity, StringComparison.Ordinal))
           && string.Equals(receipt.SigningKeyId, plan.SigningKeyId, StringComparison.Ordinal)
           && (plan.NativeAttestationSha256 is null
               || string.Equals(
                   receipt.NativeAttestationSha256,
                   plan.NativeAttestationSha256,
                   StringComparison.Ordinal))
           && (plan.NativeAttestationKeyId is null
               || string.Equals(
                   receipt.NativeAttestationKeyId,
                   plan.NativeAttestationKeyId,
                   StringComparison.Ordinal));

    private async Task<ExecutionRuntimeVerificationResult> VerifyReceiptAsync(
        ExecutionRuntimeReceiptV1 receipt,
        bool deep,
        CancellationToken cancellationToken,
        bool persistHealthMarker = true)
    {
        using var phase = _telemetry.BeginPhase(
            ExecutionRuntimePhaseNames.HealthCheck,
            receipt.Identity,
            deep ? GetDeepVerificationBytes(receipt) : 0,
            cacheHit: !deep);
        try
        {
            var result = await VerifyReceiptCoreAsync(
                    receipt,
                    deep,
                    cancellationToken,
                    persistHealthMarker)
                .ConfigureAwait(false);
            if (!deep)
            {
                phase.SetCacheHit(result.Healthy);
            }
            if (result.Healthy)
            {
                phase.Succeeded();
            }
            else
            {
                phase.Failed("IntegrityFailure");
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            phase.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            phase.Failed(exception);
            throw;
        }
    }

    private async Task<ExecutionRuntimeVerificationResult> VerifyReceiptCoreAsync(
        ExecutionRuntimeReceiptV1 receipt,
        bool deep,
        CancellationToken cancellationToken,
        bool persistHealthMarker)
    {
        var checkedAt = _timeProvider.GetUtcNow();
        string? signingKeySha256 = null;
        string? receiptSha256 = null;
        try
        {
            ValidateReceipt(receipt, _layout.GetReceiptPath(receipt.Identity));
            receiptSha256 = ComputeReceiptSha256(receipt);
            signingKeySha256 = ResolveCurrentSigningKeySha256(receipt);
            if (!deep)
            {
                var marker = await ReadHealthMarkerAsync(receipt, cancellationToken).ConfigureAwait(false);
                var refreshSignature = ValidateHealthMarker(
                    marker,
                    receipt,
                    receiptSha256);
                var observations = CaptureObservations(receipt);
                if (!marker.Files.SequenceEqual(observations))
                {
                    throw new InvalidDataException(
                        "The execution runtime health marker does not match current root/launcher metadata.");
                }
                if (!refreshSignature
                    && !string.Equals(marker.SigningKeySha256, signingKeySha256, StringComparison.Ordinal))
                {
                    await VerifySignedRootMetadataAsync(receipt, cancellationToken).ConfigureAwait(false);
                    throw new InvalidDataException(
                        "The execution runtime health marker does not match the current signing key.");
                }
                if (refreshSignature)
                {
                    await VerifySignedRootMetadataAsync(receipt, cancellationToken).ConfigureAwait(false);
                    if (persistHealthMarker)
                    {
                        await WriteHealthMarkerAsync(
                                receipt,
                                marker with
                                {
                                    CheckedAtUtc = checkedAt,
                                    TrustGeneration = _trust.Generation,
                                    SigningKeySha256 = signingKeySha256,
                                    Files = observations,
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                return new ExecutionRuntimeVerificationResult(true, false, checkedAt, null);
            }

            await DeepVerifyAsync(receipt, cancellationToken).ConfigureAwait(false);
            if (persistHealthMarker)
            {
                var observations = CaptureObservations(receipt);
                await WriteHealthMarkerAsync(
                        receipt,
                        new ExecutionRuntimeHealthMarkerV1(
                            ExecutionRuntimeHealthMarkerV1.CurrentSchemaVersion,
                            receipt.ContentIdentity,
                            RuntimeExecutionRuntimeStatuses.Ready,
                            DeepVerified: true,
                            checkedAt,
                            Message: null,
                            _trust.ProfileId,
                            _trust.Generation,
                            receipt.TrustEnvironment,
                            receipt.SigningKeyId,
                            signingKeySha256,
                            receiptSha256,
                            observations),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return new ExecutionRuntimeVerificationResult(true, true, checkedAt, null);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or CryptographicException
                                           or ExecutionRuntimeTrustException)
        {
            if (persistHealthMarker)
            {
                try
                {
                    receiptSha256 ??= ComputeReceiptSha256(receipt);
                    await WriteHealthMarkerAsync(
                            receipt,
                            new ExecutionRuntimeHealthMarkerV1(
                                ExecutionRuntimeHealthMarkerV1.CurrentSchemaVersion,
                                receipt.ContentIdentity,
                                RuntimeExecutionRuntimeStatuses.Unhealthy,
                                DeepVerified: deep,
                                checkedAt,
                                exception.Message,
                                _trust.ProfileId,
                                _trust.Generation,
                                receipt.TrustEnvironment,
                                receipt.SigningKeyId,
                                signingKeySha256,
                                receiptSha256,
                                []),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Verification failure remains authoritative even if its disposable marker cannot be written.
                }
            }
            return new ExecutionRuntimeVerificationResult(false, deep, checkedAt, exception.Message);
        }
    }

    private async Task<ExecutionRuntimeHealthMarkerV1> ReadHealthMarkerAsync(
        ExecutionRuntimeReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        var path = _layout.GetHealthPath(receipt.ContentIdentity);
        if (!File.Exists(path))
        {
            throw new InvalidDataException("The execution runtime health marker is missing.");
        }
        PackageStorePathGuard.EnsureSafe(path, _layout.HealthRootPath);

        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null
                || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The execution runtime health marker is malformed.");
            }
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxMetadataBytes)
            {
                throw new InvalidDataException("The execution runtime health marker is malformed.");
            }
            return await JsonSerializer.DeserializeAsync<ExecutionRuntimeHealthMarkerV1>(
                       stream,
                       JsonOptions,
                       cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException("The execution runtime health marker is malformed.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The execution runtime health marker is malformed.", exception);
        }
    }

    private bool ValidateHealthMarker(
        ExecutionRuntimeHealthMarkerV1 marker,
        ExecutionRuntimeReceiptV1 receipt,
        string receiptSha256)
    {
        if (marker.SchemaVersion != ExecutionRuntimeHealthMarkerV1.CurrentSchemaVersion
            || !marker.DeepVerified
            || marker.Status != RuntimeExecutionRuntimeStatuses.Ready
            || marker.CheckedAtUtc == default
            || marker.Message is not null
            || marker.Files is null
            || marker.Files.Count == 0
            || !string.Equals(marker.ContentIdentity, receipt.ContentIdentity, StringComparison.Ordinal)
            || !string.Equals(marker.TrustProfileId, _trust.ProfileId, StringComparison.Ordinal)
            || marker.TrustGeneration < receipt.TrustGeneration
            || marker.TrustGeneration > _trust.Generation
            || marker.TrustEnvironment != receipt.TrustEnvironment
            || !string.Equals(marker.SigningKeyId, receipt.SigningKeyId, StringComparison.Ordinal)
            || !IsSha256(marker.SigningKeySha256)
            || !string.Equals(marker.ReceiptSha256, receiptSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The execution runtime health marker is stale or does not match its immutable receipt.");
        }

        return marker.TrustGeneration != _trust.Generation;
    }

    private async Task DeepVerifyAsync(
        ExecutionRuntimeReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        var archivePath = _layout.GetArchivePath(receipt.ArtifactSha256);
        await VerifyFileAsync(
            archivePath,
            receipt.ArtifactSha256,
            receipt.ArtifactSize,
            "content-addressed archive",
            cancellationToken).ConfigureAwait(false);

        var manifestObjectPath = _layout.GetManifestPath(receipt.ManifestSha256);
        await VerifyFileAsync(
            manifestObjectPath,
            receipt.ManifestSha256,
            expectedSize: null,
            "content-addressed manifest",
            cancellationToken).ConfigureAwait(false);

        var rootPath = await VerifySignedRootMetadataAsync(receipt, cancellationToken).ConfigureAwait(false);

        foreach (var content in receipt.Content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentPath = GetArchiveRelativePath(rootPath, content.Path);
            PackageStorePathGuard.EnsureSafe(contentPath, rootPath);
            await VerifyFileAsync(
                contentPath,
                content.Sha256,
                content.Size,
                $"root content '{content.Path}'",
                cancellationToken,
                content.Path).ConfigureAwait(false);
        }
        EnsureExactRootInventory(rootPath, receipt);
    }

    private async Task<string> VerifySignedRootMetadataAsync(
        ExecutionRuntimeReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        var rootPath = _layout.GetContentRootPath(receipt.ContentIdentity);
        if (!Directory.Exists(rootPath))
        {
            throw new InvalidDataException("The content-addressed execution runtime root is missing.");
        }
        PackageStorePathGuard.EnsureSafe(rootPath, _layout.ContentRootPath);
        var rootManifestPath = GetArchiveRelativePath(rootPath, SunderExecutionRuntimeFormat.ManifestPath);
        var contentIndexPath = GetArchiveRelativePath(rootPath, SunderExecutionRuntimeFormat.ContentIndexPath);
        var signaturePath = GetArchiveRelativePath(rootPath, SunderExecutionRuntimeFormat.SignaturePath);
        PackageStorePathGuard.EnsureSafe(rootManifestPath, rootPath);
        PackageStorePathGuard.EnsureSafe(contentIndexPath, rootPath);
        PackageStorePathGuard.EnsureSafe(signaturePath, rootPath);

        var manifestBytes = await ReadBoundedFileAsync(
                rootManifestPath,
                SunderExecutionRuntimeFormat.MaxMetadataBytes,
                "root manifest",
                cancellationToken)
            .ConfigureAwait(false);
        var actualManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!string.Equals(actualManifestSha256, receipt.ManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The root manifest SHA-256 digest does not match its immutable receipt.");
        }
        var manifest = SunderExecutionRuntimeCanonicalJson.DeserializeManifest(manifestBytes)
                       ?? throw new InvalidDataException("The published execution runtime manifest is empty.");
        if (!manifestBytes.AsSpan().SequenceEqual(SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest))
            || !ManifestMatchesReceipt(manifest, receipt))
        {
            throw new InvalidDataException(
                "The published execution runtime manifest does not match its immutable receipt.");
        }

        var contentIndexBytes = await ReadBoundedFileAsync(
                contentIndexPath,
                SunderExecutionRuntimeFormat.MaxMetadataBytes,
                "root content index",
                cancellationToken)
            .ConfigureAwait(false);
        var signature = await ReadBoundedFileAsync(
                signaturePath,
                SunderExecutionRuntimeFormat.Ed25519SignatureBytes,
                "root signature",
                cancellationToken)
            .ConfigureAwait(false);
        EnsureContentIndexMatchesReceipt(contentIndexBytes, receipt);
        if (signature.Length != SunderExecutionRuntimeFormat.Ed25519SignatureBytes)
        {
            throw new InvalidDataException("The published execution runtime signature has an invalid length.");
        }
        var signatureInput = SunderExecutionRuntimeSignatureInput.Create(manifestBytes, contentIndexBytes);
        var contentIdentity = Convert.ToHexString(SHA256.HashData(signatureInput)).ToLowerInvariant();
        if (!string.Equals(contentIdentity, receipt.ContentIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The published execution runtime content identity is invalid.");
        }
        var trustedKey = _trust.Resolve(
            receipt.SigningKeyId,
            receipt.TrustEnvironment,
            receipt.TrustGeneration);
        if (!_signatureVerifier.Verify(signatureInput, signature, trustedKey.PublicKey.Span))
        {
            throw new CryptographicException(
                "The published execution runtime signature no longer verifies with Host trust.");
        }
        return rootPath;
    }

    private string ResolveCurrentSigningKeySha256(ExecutionRuntimeReceiptV1 receipt)
    {
        var trustedKey = _trust.Resolve(
            receipt.SigningKeyId,
            receipt.TrustEnvironment,
            receipt.TrustGeneration);
        return Convert.ToHexString(SHA256.HashData(trustedKey.PublicKey.Span)).ToLowerInvariant();
    }

    private static string ComputeReceiptSha256(ExecutionRuntimeReceiptV1 receipt)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions)))
            .ToLowerInvariant();

    private static void EnsureContentIndexMatchesReceipt(
        ReadOnlySpan<byte> contentIndexBytes,
        ExecutionRuntimeReceiptV1 receipt)
    {
        SunderExecutionRuntimeContentIndex? contentIndex;
        try
        {
            contentIndex = SunderExecutionRuntimeCanonicalJson.DeserializeContentIndex(contentIndexBytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The published execution runtime content index is invalid.",
                exception);
        }

        if (contentIndex is null
            || contentIndex.SchemaVersion != SunderExecutionRuntimeFormat.CurrentContentIndexVersion
            || contentIndex.Files is null
            || contentIndex.Files.Count != receipt.Content.Count
            || !contentIndexBytes.SequenceEqual(SunderExecutionRuntimeCanonicalJson.SerializeContentIndex(contentIndex)))
        {
            throw new InvalidDataException(
                "The published execution runtime content index does not match its immutable receipt.");
        }

        for (var index = 0; index < receipt.Content.Count; index++)
        {
            var signed = contentIndex.Files[index];
            var expected = receipt.Content[index];
            if (signed is null
                || !string.Equals(signed.Path, expected.Path, StringComparison.Ordinal)
                || !string.Equals(signed.Sha256, expected.Sha256, StringComparison.Ordinal)
                || signed.Size != expected.Size)
            {
                throw new InvalidDataException(
                    "The published execution runtime content index does not match its immutable receipt.");
            }
        }
    }

    private static bool ManifestMatchesReceipt(
        SunderExecutionRuntimeManifest manifest,
        ExecutionRuntimeReceiptV1 receipt)
        => string.Equals(manifest.Id, receipt.Identity.RuntimeId, StringComparison.Ordinal)
           && string.Equals(manifest.Version, receipt.Identity.Version, StringComparison.Ordinal)
           && manifest.PackagingRevision == receipt.Identity.PackagingRevision
           && string.Equals(manifest.Rid, receipt.Identity.Rid, StringComparison.Ordinal)
           && string.Equals(manifest.ModuleAbi, receipt.Identity.ModuleAbi, StringComparison.Ordinal)
           && string.Equals(manifest.WorkerProtocol, receipt.WorkerProtocol, StringComparison.Ordinal)
           && string.Equals(manifest.Launch?.Executable, receipt.LauncherPath, StringComparison.Ordinal)
           && manifest.Launch?.Arguments is { } launcherArguments
           && launcherArguments.SequenceEqual(receipt.LauncherArguments)
           && string.Equals(
               manifest.Provenance?.AttestationSha256,
               receipt.NativeAttestationSha256,
               StringComparison.Ordinal)
           && string.Equals(
               manifest.Provenance?.AttestationKeyId,
               receipt.NativeAttestationKeyId,
               StringComparison.Ordinal)
           && string.Equals(manifest.Signing?.KeyId, receipt.SigningKeyId, StringComparison.Ordinal);

    private static void EnsureExactRootInventory(
        string rootPath,
        ExecutionRuntimeReceiptV1 receipt)
    {
        var expected = receipt.Content.Select(static content => content.Path).ToHashSet(StringComparer.Ordinal);
        expected.Add(SunderExecutionRuntimeFormat.ManifestPath);
        expected.Add(SunderExecutionRuntimeFormat.ContentIndexPath);
        expected.Add(SunderExecutionRuntimeFormat.SignaturePath);
        var expectedDirectories = expected
            .SelectMany(static path => GetParentPaths(path))
            .ToHashSet(StringComparer.Ordinal);
        var actual = EnumerateFiles(rootPath, "*")
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)))
            .ToHashSet(StringComparer.Ordinal);
        var actualDirectories = EnumerateDirectories(rootPath)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)))
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected) || !actualDirectories.SetEquals(expectedDirectories))
        {
            throw new InvalidDataException(
                "The published execution runtime root inventory does not match its immutable receipt.");
        }
        ExecutionRuntimeRootFileSystem.VerifySealed(
            rootPath,
            GetArchiveRelativePath(rootPath, receipt.LauncherPath));
    }

    private IReadOnlyList<ExecutionRuntimeFileObservationV1> CaptureObservations(
        ExecutionRuntimeReceiptV1 receipt)
    {
        var paths = new List<(string Name, string Path)>
        {
            ("archive", _layout.GetArchivePath(receipt.ArtifactSha256)),
            ("manifest", _layout.GetManifestPath(receipt.ManifestSha256)),
        };
        var rootPath = _layout.GetContentRootPath(receipt.ContentIdentity);
        PackageStorePathGuard.EnsureSafe(rootPath, _layout.ContentRootPath);
        var launcherPath = GetArchiveRelativePath(rootPath, receipt.LauncherPath);
        PackageStorePathGuard.EnsureSafe(launcherPath, rootPath);
        paths.Add(("root/" + SunderExecutionRuntimeFormat.ManifestPath, GetArchiveRelativePath(rootPath, SunderExecutionRuntimeFormat.ManifestPath)));
        paths.Add(("root/" + SunderExecutionRuntimeFormat.ContentIndexPath, GetArchiveRelativePath(rootPath, SunderExecutionRuntimeFormat.ContentIndexPath)));
        paths.Add(("root/" + SunderExecutionRuntimeFormat.SignaturePath, GetArchiveRelativePath(rootPath, SunderExecutionRuntimeFormat.SignaturePath)));
        paths.AddRange(receipt.Content.Select(content =>
            ("root/" + content.Path, GetArchiveRelativePath(rootPath, content.Path))));

        var observations = new List<ExecutionRuntimeFileObservationV1>(paths.Count);
        foreach (var (name, path) in paths.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null
                || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Observed execution runtime file '{name}' is missing or is a link.");
            }
            observations.Add(new ExecutionRuntimeFileObservationV1(
                name,
                info.Length,
                info.LastWriteTimeUtc.Ticks));
        }
        EnsureExactRootInventory(rootPath, receipt);
        return Array.AsReadOnly(observations.ToArray());
    }

    private async Task WriteHealthMarkerAsync(
        ExecutionRuntimeReceiptV1 receipt,
        ExecutionRuntimeHealthMarkerV1 marker,
        CancellationToken cancellationToken)
    {
        var path = _layout.GetHealthPath(receipt.ContentIdentity);
        await DurableJsonDocument.WriteAsync(path, marker, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PublishFileUnderFenceAsync(
        string stagedPath,
        string destinationPath,
        string expectedSha256,
        long? expectedSize,
        string quarantineLabel)
    {
        while (true)
        {
            if (File.Exists(destinationPath))
            {
                try
                {
                    await VerifyFileAsync(
                        destinationPath,
                        expectedSha256,
                        expectedSize,
                        "existing content-addressed object",
                        CancellationToken.None).ConfigureAwait(false);
                    TryDeleteFile(stagedPath);
                    return;
                }
                catch (InvalidDataException)
                {
                    QuarantineUnderFence(destinationPath, quarantineLabel);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            try
            {
                File.Move(stagedPath, destinationPath);
                return;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another Host instance won the atomic publication; validate its object.
            }
        }
    }

    private async Task PublishRootAsync(
        string stagedRootPath,
        ExecutionRuntimeReceiptV1 receipt,
        bool storeFenceHeld = false)
    {
        using var phase = _telemetry.BeginPhase(
            ExecutionRuntimePhaseNames.PublishRoot,
            receipt.Identity,
            receipt.Content.Sum(static content => content.Size),
            cacheHit: false);
        try
        {
            var destinationPath = _layout.GetContentRootPath(receipt.ContentIdentity);
            while (true)
            {
                if (Directory.Exists(destinationPath))
                {
                    try
                    {
                        await DeepVerifyAsync(receipt, CancellationToken.None).ConfigureAwait(false);
                        TryDeleteDirectory(stagedRootPath);
                        phase.SetCacheHit(true);
                        phase.Succeeded();
                        return;
                    }
                    catch (Exception exception) when (exception is IOException
                                                       or UnauthorizedAccessException
                                                       or InvalidDataException
                                                       or CryptographicException
                                                       or ExecutionRuntimeTrustException)
                    {
                        if (storeFenceHeld)
                        {
                            QuarantineUnderFence(destinationPath, "root-conflict");
                        }
                        else
                        {
                            Quarantine(destinationPath, "root-conflict");
                        }
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                try
                {
                    Directory.Move(stagedRootPath, destinationPath);
                }
                catch (IOException) when (Directory.Exists(destinationPath))
                {
                    // Another Host instance won the atomic publication; validate its root.
                    continue;
                }
                Hit(ExecutionRuntimeStoreFaultPoint.RootMoved);
                ExecutionRuntimeRootFileSystem.Seal(
                    destinationPath,
                    GetArchiveRelativePath(destinationPath, receipt.LauncherPath));
                phase.SetCacheHit(false);
                phase.Succeeded();
                return;
            }
        }
        catch (Exception exception)
        {
            phase.Failed(exception);
            throw;
        }
    }

    private static long GetDeepVerificationBytes(ExecutionRuntimeReceiptV1 receipt)
    {
        var total = Math.Max(0, receipt.ArtifactSize);
        foreach (var content in receipt.Content)
        {
            var size = Math.Max(0, content?.Size ?? 0);
            if (size > long.MaxValue - total)
            {
                return long.MaxValue;
            }
            total += size;
        }
        return total;
    }

    private sealed class ArchiveInspectionTelemetryObserver(
        ExecutionRuntimePhaseTelemetry telemetry,
        ExecutionRuntimeIdentity identity,
        long archiveBytes) : ISunderExecutionRuntimeArchiveInspectionObserver
    {
        private ExecutionRuntimePhaseTelemetry.PhaseScope? _phase;

        public void ExtractionStarted()
            => _phase = telemetry.BeginPhase(
                ExecutionRuntimePhaseNames.Extract,
                identity,
                archiveBytes,
                cacheHit: false);

        public void ExtractionCompleted(bool succeeded, bool canceled)
        {
            var phase = Interlocked.Exchange(ref _phase, null);
            if (phase is null)
            {
                return;
            }
            if (succeeded)
            {
                phase.Succeeded();
            }
            else if (canceled)
            {
                phase.Canceled();
            }
            else
            {
                phase.Failed("ExtractionFailure");
            }
            phase.Dispose();
        }
    }

    private async Task<ExecutionRuntimeReceiptV1> PublishReceiptAsync(
        ExecutionRuntimeReceiptV1 receipt,
        CancellationToken cancellationToken,
        bool storeFenceHeld = false)
    {
        var destinationPath = _layout.GetReceiptPath(receipt.Identity);
        if (File.Exists(destinationPath))
        {
            ExecutionRuntimeReceiptV1 existing;
            try
            {
                existing = await ReadReceiptAsync(destinationPath, cancellationToken).ConfigureAwait(false);
                ValidateReceipt(existing, destinationPath);
            }
            catch (InvalidDataException)
            {
                if (storeFenceHeld)
                {
                    QuarantineUnderFence(destinationPath, "invalid-receipt");
                }
                else
                {
                    Quarantine(destinationPath, "invalid-receipt");
                }
                existing = null!;
            }
            if (existing is not null)
            {
                if (!ReceiptsDescribeSameContent(existing, receipt))
                {
                    throw new ExecutionRuntimeStoreConflictException(
                        "The exact execution runtime identity already has a conflicting immutable receipt.");
                }
                return existing;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var stagedReceiptPath = destinationPath + ".candidate-" + Guid.NewGuid().ToString("N");
        await DurableJsonDocument.WriteAsync(
            stagedReceiptPath,
            receipt,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(stagedReceiptPath, destinationPath);
            return receipt;
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            TryDeleteFile(stagedReceiptPath);
            var winner = await ReadReceiptAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            ValidateReceipt(winner, destinationPath);
            if (!ReceiptsDescribeSameContent(winner, receipt))
            {
                throw new ExecutionRuntimeStoreConflictException(
                    "Concurrent execution runtime publication produced conflicting immutable receipts.");
            }
            return winner;
        }
        finally
        {
            TryDeleteFile(stagedReceiptPath);
        }
    }

    private static bool ReceiptsDescribeSameContent(
        ExecutionRuntimeReceiptV1 first,
        ExecutionRuntimeReceiptV1 second)
        => first.Identity == second.Identity
           && first.WorkerProtocol == second.WorkerProtocol
           && first.ArtifactSha256 == second.ArtifactSha256
           && first.ArtifactSize == second.ArtifactSize
           && first.ManifestSha256 == second.ManifestSha256
           && first.ContentIdentity == second.ContentIdentity
           && first.LauncherPath == second.LauncherPath
            && first.LauncherSha256 == second.LauncherSha256
            && first.LauncherSize == second.LauncherSize
            && first.LauncherArguments.SequenceEqual(second.LauncherArguments)
            && first.Content.SequenceEqual(second.Content)
           && first.NativeAttestationSha256 == second.NativeAttestationSha256
           && first.NativeAttestationKeyId == second.NativeAttestationKeyId
           && first.SigningKeyId == second.SigningKeyId
           && first.TrustEnvironment == second.TrustEnvironment
           && first.AcquisitionSource == second.AcquisitionSource
           && first.RegistryOrigin == second.RegistryOrigin;

    private async Task VerifyFileAsync(
        string path,
        string expectedSha256,
        long? expectedSize,
        string label,
        CancellationToken cancellationToken,
        string? payloadRelativePath = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {label} is missing or is a link.");
        }
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (expectedSize is not null && stream.Length != expectedSize)
        {
            throw new InvalidDataException($"The {label} size does not match its immutable receipt.");
        }
        string actualHash;
        try
        {
            actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
        }
        finally
        {
            if (payloadRelativePath is not null)
            {
                TryRecordPayloadIo(payloadRelativePath, stream.Position, stream.Position);
            }
        }
        if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {label} SHA-256 digest does not match its immutable receipt.");
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        string label,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {label} is missing or is a link.");
        }
        if (info.Length > maximumBytes)
        {
            throw new InvalidDataException($"The {label} exceeds its maximum size.");
        }
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > maximumBytes)
        {
            throw new InvalidDataException($"The {label} exceeds its maximum size.");
        }
        return bytes;
    }

    private void TryRecordPayloadIo(string relativePath, long bytesRead, long bytesHashed)
    {
        try
        {
            _verificationIoObserver?.RecordPayloadContent(relativePath, bytesRead, bytesHashed);
        }
        catch
        {
            // Diagnostic observers must not affect runtime verification.
        }
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task WriteJournalAsync(
        string journalPath,
        ExecutionRuntimeStoreTransactionJournal journal,
        CancellationToken cancellationToken)
        => await DurableJsonDocument.WriteAsync(journalPath, journal, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<ExecutionRuntimeStoreTransactionJournal> SetJournalPhaseAsync(
        string journalPath,
        ExecutionRuntimeStoreTransactionJournal journal,
        ExecutionRuntimeStoreTransactionPhase phase)
    {
        var updated = journal with { Phase = phase };
        await WriteJournalAsync(journalPath, updated, CancellationToken.None).ConfigureAwait(false);
        return updated;
    }

    private void ValidateRetention(ExecutionRuntimeRetention retention)
    {
        if (retention.Identity is null
            && retention.ContentIdentity is null
            && retention.ArtifactSha256 is null
            && retention.ManifestSha256 is null)
        {
            throw new ArgumentException(
                "Execution-runtime retention must identify at least one immutable store object.",
                nameof(retention));
        }
        if (retention.Identity is not null)
        {
            _ = _layout.GetReceiptPath(retention.Identity);
        }
        if (retention.ContentIdentity is not null)
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(retention.ContentIdentity, nameof(retention));
        }
        if (retention.ArtifactSha256 is not null)
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(retention.ArtifactSha256, nameof(retention));
        }
        if (retention.ManifestSha256 is not null)
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(retention.ManifestSha256, nameof(retention));
        }
        if (retention.ArtifactSize is <= 0 or > SunderExecutionRuntimeFormat.MaxArchiveBytes
            || retention.ArtifactSize is not null && retention.ArtifactSha256 is null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                "Execution-runtime retention has an invalid artifact size or omits its artifact digest.");
        }
    }

    private static bool RetentionMatchesReceipt(
        ExecutionRuntimeRetention retention,
        ExecutionRuntimeReceiptV1 receipt)
        => (retention.Identity is null || receipt.Identity == retention.Identity)
           && (retention.ContentIdentity is null
               || string.Equals(receipt.ContentIdentity, retention.ContentIdentity, StringComparison.Ordinal))
           && (retention.ArtifactSha256 is null
               || string.Equals(receipt.ArtifactSha256, retention.ArtifactSha256, StringComparison.Ordinal))
           && (retention.ArtifactSize is null || receipt.ArtifactSize == retention.ArtifactSize)
           && (retention.ManifestSha256 is null
               || string.Equals(receipt.ManifestSha256, retention.ManifestSha256, StringComparison.Ordinal));

    private async Task<IReadOnlyList<ExecutionRuntimeReceiptV1>> ReadPendingTransactionReceiptsAsync(
        CancellationToken cancellationToken)
    {
        var receipts = new List<ExecutionRuntimeReceiptV1>();
        foreach (var transactionPath in Directory.EnumerateDirectories(_layout.TransactionRootPath).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transactionId = Path.GetFileName(transactionPath);
            if (!Guid.TryParseExact(transactionId, "N", out _))
            {
                throw new InvalidDataException(
                    $"Execution-runtime transaction directory '{transactionPath}' has an invalid identity.");
            }
            var journalPath = _layout.GetTransactionJournalPath(transactionId);
            if (!File.Exists(journalPath))
            {
                if (!Directory.Exists(transactionPath))
                {
                    continue;
                }
                throw new InvalidDataException(
                    $"Execution-runtime transaction '{transactionId}' has no durable journal.");
            }
            try
            {
                await using var stream = new FileStream(
                    journalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxMetadataBytes)
                {
                    throw new InvalidDataException(
                        $"Execution-runtime transaction journal '{journalPath}' has an invalid size.");
                }
                var journal = await JsonSerializer.DeserializeAsync<ExecutionRuntimeStoreTransactionJournal>(
                                  stream,
                                  JsonOptions,
                                  cancellationToken).ConfigureAwait(false)
                              ?? throw new InvalidDataException(
                                  $"Execution-runtime transaction journal '{journalPath}' is empty.");
                ValidateJournal(journal);
                if (!string.Equals(journal.TransactionId, transactionId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Execution-runtime transaction journal '{journalPath}' has the wrong identity.");
                }
                receipts.Add(journal.Receipt);
            }
            catch (FileNotFoundException) when (!Directory.Exists(transactionPath))
            {
                // A completed publisher removed its journal after publishing the retained receipt.
            }
            catch (DirectoryNotFoundException) when (!Directory.Exists(transactionPath))
            {
                // A completed publisher removed its journal after publishing the retained receipt.
            }
        }
        return receipts;
    }

    private HashSet<string> EnumerateContentRootIdentitiesUnderFence()
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shardPath in Directory.EnumerateDirectories(_layout.ContentRootPath).ToArray())
        {
            PackageStorePathGuard.EnsureSafe(shardPath, _layout.ContentRootPath);
            foreach (var rootPath in Directory.EnumerateDirectories(shardPath).ToArray())
            {
                PackageStorePathGuard.EnsureSafe(rootPath, _layout.ContentRootPath);
                var contentIdentity = Path.GetFileName(rootPath);
                try
                {
                    ExecutionRuntimeStoreLayout.ValidateSha256(contentIdentity, nameof(contentIdentity));
                }
                catch (ArgumentException)
                {
                    throw new InvalidDataException(
                        $"Execution-runtime content root '{rootPath}' has an invalid content identity.");
                }
                if (!PathsEqual(rootPath, _layout.GetContentRootPath(contentIdentity)))
                {
                    throw new InvalidDataException(
                        $"Execution-runtime content root '{rootPath}' is not in its canonical content-addressed location.");
                }
                _ = EnumerateFiles(rootPath, "*");
                identities.Add(contentIdentity);
            }
        }
        return identities;
    }

    private async Task<(ExecutionRuntimeGarbageCollectionMarkV1 Mark, bool Created)>
        GetOrCreateGarbageCollectionMarkUnderFenceAsync(
        string contentIdentity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var path = _layout.GetGarbageCollectionMarkPath(contentIdentity);
        if (File.Exists(path))
        {
            try
            {
                PackageStorePathGuard.EnsureSafe(path, _layout.GarbageCollectionMarkRootPath);
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is <= 0 or > 64 * 1024)
                {
                    throw new InvalidDataException(
                        $"Execution-runtime garbage-collection mark '{path}' has an invalid size.");
                }
                var existing = await JsonSerializer.DeserializeAsync<ExecutionRuntimeGarbageCollectionMarkV1>(
                                   stream,
                                   JsonOptions,
                                   cancellationToken).ConfigureAwait(false)
                               ?? throw new InvalidDataException(
                                   $"Execution-runtime garbage-collection mark '{path}' is empty.");
                if (existing.SchemaVersion != ExecutionRuntimeGarbageCollectionMarkV1.CurrentSchemaVersion
                    || existing.UnreferencedAtUtc == default
                    || !string.Equals(existing.ContentIdentity, contentIdentity, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Execution-runtime garbage-collection mark '{path}' is invalid.");
                }
                return (existing, false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                QuarantineUnderFence(path, "invalid-gc-mark");
            }
        }

        var created = new ExecutionRuntimeGarbageCollectionMarkV1(
            ExecutionRuntimeGarbageCollectionMarkV1.CurrentSchemaVersion,
            contentIdentity,
            now);
        await DurableJsonDocument.WriteAsync(path, created, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return (created, true);
    }

    private void DeleteGarbageCollectionMarkUnderFence(string contentIdentity)
    {
        var path = _layout.GetGarbageCollectionMarkPath(contentIdentity);
        if (File.Exists(path))
        {
            DurableJsonDocument.Delete(path);
        }
    }

    private void CleanupOrphanGarbageCollectionMarksUnderFence(IReadOnlySet<string> knownContent)
    {
        foreach (var path in Directory.EnumerateFiles(
                     _layout.GarbageCollectionMarkRootPath,
                     "*.json",
                     SearchOption.TopDirectoryOnly).ToArray())
        {
            var contentIdentity = Path.GetFileNameWithoutExtension(path);
            if (!knownContent.Contains(contentIdentity))
            {
                TryDeleteFile(path);
            }
        }
    }

    private async Task<bool> TombstoneContentUnderFenceAsync(
        string contentIdentity,
        IReadOnlyList<(string Path, ExecutionRuntimeReceiptV1 Receipt)> receipts,
        CancellationToken cancellationToken)
    {
        if (_leases.HasActiveLeaseUnderFence(contentIdentity))
        {
            return false;
        }
        return await TombstoneContentCoreUnderFenceAsync(
                contentIdentity,
                receipts,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> TombstoneContentCoreUnderFenceAsync(
        string contentIdentity,
        IReadOnlyList<(string Path, ExecutionRuntimeReceiptV1 Receipt)> receipts,
        CancellationToken cancellationToken)
    {
        var pendingTombstones = ReadTombstonesForContentUnderFence(contentIdentity);
        if (pendingTombstones.Count > 0)
        {
            var completed = true;
            foreach (var (pendingDocument, path) in pendingTombstones)
            {
                var receiptIdentities = pendingDocument.ReceiptIdentities
                    .Concat(receipts.Select(static item => item.Receipt.Identity))
                    .GroupBy(static identity => identity.SingleFlightKey, StringComparer.Ordinal)
                    .Select(static group => group.First())
                    .OrderBy(static identity => identity.SingleFlightKey, StringComparer.Ordinal)
                    .ToArray();
                var currentDocument = pendingDocument;
                if (receiptIdentities.Length != pendingDocument.ReceiptIdentities.Count)
                {
                    currentDocument = pendingDocument with { ReceiptIdentities = receiptIdentities };
                    ValidateTombstone(currentDocument, path);
                    await DurableJsonDocument.WriteAsync(
                            Path.Combine(path, "tombstone.json"),
                            currentDocument,
                            JsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                var tombstoneCompleted = await CompleteTombstoneUnderFenceAsync(
                        currentDocument,
                        path,
                        cancellationToken)
                    .ConfigureAwait(false);
                completed &= tombstoneCompleted;
            }
            return completed;
        }
        var tombstoneId = Guid.NewGuid().ToString("N");
        var tombstonePath = _layout.GetTombstonePath(tombstoneId);
        Directory.CreateDirectory(tombstonePath);
        PackageStorePathGuard.EnsureSafe(tombstonePath, _layout.TombstoneRootPath);
        var document = new ExecutionRuntimeRootTombstoneV1(
            ExecutionRuntimeRootTombstoneV1.CurrentSchemaVersion,
            tombstoneId,
            ExecutionRuntimeRootTombstonePhase.Prepared,
            contentIdentity,
            receipts
                .Select(static item => item.Receipt.Identity)
                .Distinct()
                .OrderBy(static identity => identity.SingleFlightKey, StringComparer.Ordinal)
                .ToArray(),
            _timeProvider.GetUtcNow());
        ValidateTombstone(document, tombstonePath);
        try
        {
            await DurableJsonDocument.WriteAsync(
                    Path.Combine(tombstonePath, "tombstone.json"),
                    document,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            DurableJsonDocument.SyncDirectory(_layout.TombstoneRootPath);
        }
        catch
        {
            TryDeleteDirectory(tombstonePath);
            throw;
        }
        return await CompleteTombstoneUnderFenceAsync(
                document,
                tombstonePath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RecoverTombstonesUnderFenceAsync(CancellationToken cancellationToken)
    {
        _ = EnumerateFiles(_layout.ReceiptRootPath, "*.json");
        _ = EnumerateFiles(_layout.ArchiveRootPath, "*");
        _ = EnumerateFiles(_layout.ManifestRootPath, "*");
        _ = EnumerateContentRootIdentitiesUnderFence();
        _ = EnumerateFiles(_layout.HealthRootPath, "*");
        _ = EnumerateFiles(_layout.PolicyRootPath, "*");
        _ = EnumerateFiles(_layout.GarbageCollectionMarkRootPath, "*");
        var tombstonePaths = Directory.EnumerateDirectories(_layout.TombstoneRootPath).ToArray();
        foreach (var tombstonePath in tombstonePaths)
        {
            PackageStorePathGuard.EnsureSafe(tombstonePath, _layout.TombstoneRootPath);
            if (!PathsEqual(tombstonePath, _layout.GarbageCollectionMarkRootPath))
            {
                _ = EnumerateFiles(tombstonePath, "*");
            }
        }

        foreach (var temporaryMark in Directory.EnumerateFiles(
                     _layout.GarbageCollectionMarkRootPath,
                     "*.tmp-*",
                     SearchOption.TopDirectoryOnly).ToArray())
        {
            TryDeleteFile(temporaryMark);
        }
        foreach (var tombstonePath in tombstonePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PathsEqual(tombstonePath, _layout.GarbageCollectionMarkRootPath))
            {
                continue;
            }
            var documentPath = Path.Combine(tombstonePath, "tombstone.json");
            if (!File.Exists(documentPath))
            {
                var entries = Directory.EnumerateFileSystemEntries(tombstonePath).ToArray();
                if (entries.All(path =>
                        File.Exists(path)
                        && Path.GetFileName(path).StartsWith("tombstone.json.tmp-", StringComparison.Ordinal)))
                {
                    foreach (var temporaryDocument in entries)
                    {
                        TryDeleteFile(temporaryDocument);
                    }
                    if (!Directory.EnumerateFileSystemEntries(tombstonePath).Any())
                    {
                        Directory.Delete(tombstonePath);
                        DurableJsonDocument.SyncDirectory(_layout.TombstoneRootPath);
                        continue;
                    }
                }
            }
            ExecutionRuntimeRootTombstoneV1 document;
            try
            {
                PackageStorePathGuard.EnsureSafe(documentPath, tombstonePath);
                await using var stream = new FileStream(
                    documentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxMetadataBytes)
                {
                    throw new InvalidDataException(
                        $"Execution-runtime tombstone '{documentPath}' has an invalid size.");
                }
                document = await JsonSerializer.DeserializeAsync<ExecutionRuntimeRootTombstoneV1>(
                               stream,
                               JsonOptions,
                               cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidDataException(
                               $"Execution-runtime tombstone '{documentPath}' is empty.");
                ValidateTombstone(document, tombstonePath);
            }
            catch (Exception exception) when (exception is IOException
                                                or UnauthorizedAccessException
                                                or JsonException
                                                or InvalidDataException
                                                or ArgumentException)
            {
                QuarantineUnderFence(tombstonePath, "invalid-tombstone");
                throw new InvalidDataException(
                    $"Execution-runtime tombstone '{documentPath}' is invalid; startup recovery cannot safely infer root ownership.",
                    exception);
            }
            if (document.Phase == ExecutionRuntimeRootTombstonePhase.DeletionCommitted)
            {
                _ = await DeleteCommittedTombstoneUnderFenceAsync(
                        document,
                        tombstonePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                RestoreTombstoneUnderFence(document, tombstonePath);
            }
        }
    }

    private void RestoreTombstoneUnderFence(
        ExecutionRuntimeRootTombstoneV1 document,
        string tombstonePath)
    {
        if (_leases.HasActiveLeaseUnderFence(document.ContentIdentity))
        {
            throw new ExecutionRuntimeRootInUseException(
                $"Execution runtime root '{document.ContentIdentity}' became leased while its interrupted tombstone was being recovered.");
        }

        var liveRootPath = _layout.GetContentRootPath(document.ContentIdentity);
        var tombstonedRootPath = Path.Combine(tombstonePath, "root");
        if (Directory.Exists(tombstonedRootPath))
        {
            if (Directory.Exists(liveRootPath))
            {
                throw new ExecutionRuntimeStoreConflictException(
                    $"Execution runtime root '{document.ContentIdentity}' exists both live and tombstoned.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(liveRootPath)!);
            PackageStorePathGuard.EnsureSafe(liveRootPath, _layout.ContentRootPath);
            Directory.Move(tombstonedRootPath, liveRootPath);
            ExecutionRuntimeRootFileSystem.SealRootDirectory(liveRootPath);
        }
        else if (Directory.Exists(liveRootPath))
        {
            // A crash after making the root movable but before its tombstone rename leaves only the root mode mutable.
            ExecutionRuntimeRootFileSystem.SealRootDirectory(liveRootPath);
        }

        RestoreTombstoneFile(
            Path.Combine(tombstonePath, "health.json"),
            _layout.GetHealthPath(document.ContentIdentity),
            disposable: true);
        for (var index = 0; index < document.ReceiptIdentities.Count; index++)
        {
            RestoreTombstoneFile(
                Path.Combine(tombstonePath, "receipts", $"{index:D6}.json"),
                _layout.GetReceiptPath(document.ReceiptIdentities[index]),
                disposable: false);
        }

        TryDeleteFile(Path.Combine(tombstonePath, "unreferenced-mark.json"));
        TryDeleteFile(_layout.GetGarbageCollectionMarkPath(document.ContentIdentity));
        SyncTombstoneMoveDirectories(document, tombstonePath);
        if (!TryDeleteTombstoneEnvelopeUnderFence(tombstonePath))
        {
            throw new IOException($"Execution-runtime tombstone '{tombstonePath}' could not be restored completely.");
        }
    }

    private static void RestoreTombstoneFile(
        string tombstonedPath,
        string livePath,
        bool disposable)
    {
        if (!File.Exists(tombstonedPath))
        {
            return;
        }
        if (File.Exists(livePath))
        {
            if (!disposable
                && !File.ReadAllBytes(tombstonedPath).AsSpan().SequenceEqual(File.ReadAllBytes(livePath)))
            {
                throw new ExecutionRuntimeStoreConflictException(
                    $"Execution runtime store object '{livePath}' conflicts with its interrupted tombstone.");
            }
            File.Delete(tombstonedPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        File.Move(tombstonedPath, livePath);
    }

    private async Task<bool> CompleteTombstoneUnderFenceAsync(
        ExecutionRuntimeRootTombstoneV1 document,
        string tombstonePath,
        CancellationToken cancellationToken)
    {
        if (document.Phase == ExecutionRuntimeRootTombstonePhase.DeletionCommitted)
        {
            return await DeleteCommittedTombstoneUnderFenceAsync(
                    document,
                    tombstonePath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (_leases.HasActiveLeaseUnderFence(document.ContentIdentity))
        {
            return false;
        }
        var liveRootPath = _layout.GetContentRootPath(document.ContentIdentity);
        var tombstonedRootPath = Path.Combine(tombstonePath, "root");
        if (Directory.Exists(liveRootPath))
        {
            if (Directory.Exists(tombstonedRootPath))
            {
                RestoreTombstoneUnderFence(document, tombstonePath);
                return false;
            }
            try
            {
                ExecutionRuntimeRootFileSystem.MakeOwnedRootMovable(liveRootPath);
                Hit(ExecutionRuntimeStoreFaultPoint.TombstoneRootMovable);
                Directory.Move(liveRootPath, tombstonedRootPath);
                Hit(ExecutionRuntimeStoreFaultPoint.TombstoneRootMoved);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(liveRootPath))
                {
                    ExecutionRuntimeRootFileSystem.SealRootDirectory(liveRootPath);
                }
                RestoreTombstoneUnderFence(document, tombstonePath);
                return false;
            }
        }

        if (!TryMoveTombstoneFile(
                _layout.GetHealthPath(document.ContentIdentity),
                Path.Combine(tombstonePath, "health.json")))
        {
            RestoreTombstoneUnderFence(document, tombstonePath);
            return false;
        }
        for (var index = 0; index < document.ReceiptIdentities.Count; index++)
        {
            if (!TryMoveTombstoneFile(
                    _layout.GetReceiptPath(document.ReceiptIdentities[index]),
                    Path.Combine(tombstonePath, "receipts", $"{index:D6}.json")))
            {
                RestoreTombstoneUnderFence(document, tombstonePath);
                return false;
            }
        }
        if (!TryMoveTombstoneFile(
                _layout.GetGarbageCollectionMarkPath(document.ContentIdentity),
                Path.Combine(tombstonePath, "unreferenced-mark.json")))
        {
            RestoreTombstoneUnderFence(document, tombstonePath);
            return false;
        }
        SyncTombstoneMoveDirectories(document, tombstonePath);
        var committed = document with { Phase = ExecutionRuntimeRootTombstonePhase.DeletionCommitted };
        ValidateTombstone(committed, tombstonePath);
        await DurableJsonDocument.WriteAsync(
                Path.Combine(tombstonePath, "tombstone.json"),
                committed,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
        Hit(ExecutionRuntimeStoreFaultPoint.TombstoneDeletionCommitted);
        return await DeleteCommittedTombstoneUnderFenceAsync(
                committed,
                tombstonePath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> DeleteCommittedTombstoneUnderFenceAsync(
        ExecutionRuntimeRootTombstoneV1 document,
        string tombstonePath,
        CancellationToken cancellationToken)
    {
        if (document.Phase != ExecutionRuntimeRootTombstonePhase.DeletionCommitted)
        {
            throw new InvalidOperationException("Only a deletion-committed execution-runtime tombstone may be destroyed.");
        }

        var fullTombstonePath = Path.GetFullPath(tombstonePath);
        for (var attempt = 1; attempt <= TombstoneDeletionAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (DeleteCommittedTombstoneOnceUnderFence(tombstonePath))
                {
                    _deferredCommittedTombstones.TryRemove(fullTombstonePath, out _);
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Sharing violations are transient; the committed tombstone remains the retry authority.
            }

            if (attempt < TombstoneDeletionAttemptCount)
            {
                await Task.Delay(TombstoneDeletionRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        _deferredCommittedTombstones[fullTombstonePath] = 0;
        return false;
    }

    private bool DeleteCommittedTombstoneOnceUnderFence(string tombstonePath)
    {
        if (!Directory.Exists(tombstonePath))
        {
            return true;
        }
        var tombstonedRootPath = Path.Combine(tombstonePath, "root");
        if (!TryDeleteTombstoneDirectory(tombstonedRootPath))
        {
            return false;
        }
        Hit(ExecutionRuntimeStoreFaultPoint.TombstoneCommittedRootDeleted);
        var receiptsPath = Path.Combine(tombstonePath, "receipts");
        if (!TryDeleteTombstoneDirectory(receiptsPath))
        {
            return false;
        }
        if (!TryDeleteTombstoneFile(Path.Combine(tombstonePath, "health.json"))
            || !TryDeleteTombstoneFile(Path.Combine(tombstonePath, "unreferenced-mark.json")))
        {
            return false;
        }
        return TryDeleteTombstoneEnvelopeUnderFence(tombstonePath);
    }

    private static bool TryDeleteTombstoneDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }
        return ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(path);
    }

    private static bool TryDeleteTombstoneFile(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }
        File.Delete(path);
        return !File.Exists(path);
    }

    private void SyncTombstoneMoveDirectories(
        ExecutionRuntimeRootTombstoneV1 document,
        string tombstonePath)
    {
        var directories = new HashSet<string>(PathComparer)
        {
            Path.GetDirectoryName(_layout.GetContentRootPath(document.ContentIdentity))!,
            Path.GetDirectoryName(_layout.GetHealthPath(document.ContentIdentity))!,
            Path.GetDirectoryName(_layout.GetGarbageCollectionMarkPath(document.ContentIdentity))!,
            tombstonePath,
        };
        foreach (var identity in document.ReceiptIdentities)
        {
            directories.Add(Path.GetDirectoryName(_layout.GetReceiptPath(identity))!);
        }
        var tombstoneReceiptsPath = Path.Combine(tombstonePath, "receipts");
        if (Directory.Exists(tombstoneReceiptsPath))
        {
            directories.Add(tombstoneReceiptsPath);
        }
        foreach (var directory in directories)
        {
            if (Directory.Exists(directory))
            {
                DurableJsonDocument.SyncDirectory(directory);
            }
        }
    }

    private bool TryDeleteTombstoneEnvelopeUnderFence(string tombstonePath)
    {
        var tombstonedRootPath = Path.Combine(tombstonePath, "root");
        if (Directory.Exists(tombstonedRootPath))
        {
            return false;
        }
        var receiptsPath = Path.Combine(tombstonePath, "receipts");
        if (Directory.Exists(receiptsPath))
        {
            if (Directory.EnumerateFileSystemEntries(receiptsPath).Any())
            {
                return false;
            }
            DurableJsonDocument.SyncDirectory(receiptsPath);
            Directory.Delete(receiptsPath);
        }
        foreach (var temporaryDocument in Directory.EnumerateFiles(
                     tombstonePath,
                     "tombstone.json.tmp-*",
                     SearchOption.TopDirectoryOnly).ToArray())
        {
            if (!TryDeleteTombstoneFile(temporaryDocument))
            {
                return false;
            }
        }
        var documentPath = Path.Combine(tombstonePath, "tombstone.json");
        if (Directory.EnumerateFileSystemEntries(tombstonePath)
            .Any(path => !PathsEqual(path, documentPath)))
        {
            return false;
        }
        DurableJsonDocument.SyncDirectory(tombstonePath);
        DurableJsonDocument.Delete(documentPath);
        Directory.Delete(tombstonePath);
        DurableJsonDocument.SyncDirectory(_layout.TombstoneRootPath);
        return true;
    }

    private IReadOnlyList<(ExecutionRuntimeRootTombstoneV1 Document, string Path)>
        ReadTombstonesForContentUnderFence(string contentIdentity)
    {
        var tombstones = new List<(ExecutionRuntimeRootTombstoneV1, string)>();
        foreach (var tombstonePath in Directory.EnumerateDirectories(_layout.TombstoneRootPath).ToArray())
        {
            if (PathsEqual(tombstonePath, _layout.GarbageCollectionMarkRootPath))
            {
                continue;
            }
            var documentPath = Path.Combine(tombstonePath, "tombstone.json");
            try
            {
                PackageStorePathGuard.EnsureSafe(documentPath, tombstonePath);
                using var stream = new FileStream(
                    documentPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);
                if (stream.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxMetadataBytes)
                {
                    throw new InvalidDataException(
                        $"Execution-runtime tombstone '{documentPath}' has an invalid size.");
                }
                var document = JsonSerializer.Deserialize<ExecutionRuntimeRootTombstoneV1>(stream, JsonOptions)
                               ?? throw new InvalidDataException(
                                   $"Execution-runtime tombstone '{documentPath}' is empty.");
                ValidateTombstone(document, tombstonePath);
                if (string.Equals(document.ContentIdentity, contentIdentity, StringComparison.Ordinal))
                {
                    tombstones.Add((document, tombstonePath));
                }
            }
            catch (Exception exception) when (exception is IOException
                                                or UnauthorizedAccessException
                                                or JsonException
                                                or InvalidDataException
                                                or ArgumentException)
            {
                QuarantineUnderFence(tombstonePath, "invalid-tombstone");
            }
        }
        return tombstones;
    }

    private void ValidateTombstone(
        ExecutionRuntimeRootTombstoneV1 document,
        string tombstonePath)
    {
        if (document.SchemaVersion != ExecutionRuntimeRootTombstoneV1.CurrentSchemaVersion
            || !Guid.TryParseExact(document.TombstoneId, "N", out _)
            || !Enum.IsDefined(document.Phase)
            || document.CreatedAtUtc == default
            || document.ReceiptIdentities is null
            || !PathsEqual(tombstonePath, _layout.GetTombstonePath(document.TombstoneId)))
        {
            throw new InvalidDataException($"Execution-runtime tombstone '{tombstonePath}' is invalid.");
        }
        ExecutionRuntimeStoreLayout.ValidateSha256(
            document.ContentIdentity,
            nameof(document.ContentIdentity));
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in document.ReceiptIdentities)
        {
            ArgumentNullException.ThrowIfNull(identity);
            _ = _layout.GetReceiptPath(identity);
            if (!identities.Add(identity.SingleFlightKey))
            {
                throw new InvalidDataException(
                    $"Execution-runtime tombstone '{tombstonePath}' repeats a receipt identity.");
            }
        }
    }

    private static bool TryMoveTombstoneFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return true;
        }
        if (File.Exists(destinationPath))
        {
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(sourcePath, destinationPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryGetContentIdentity(string path, out string contentIdentity)
    {
        contentIdentity = Path.GetFileName(Path.GetFullPath(path));
        try
        {
            ExecutionRuntimeStoreLayout.ValidateSha256(contentIdentity, nameof(path));
            return PathsEqual(path, _layout.GetContentRootPath(contentIdentity));
        }
        catch (ArgumentException)
        {
            contentIdentity = string.Empty;
            return false;
        }
    }

    private int CollectContentAddressedFiles(
        IReadOnlyList<string> paths,
        IReadOnlySet<string> reachable,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 64 || reachable.Contains(name) || File.GetLastWriteTimeUtc(path) > cutoff.UtcDateTime)
            {
                continue;
            }
            TryDeleteFile(path);
            if (!File.Exists(path))
            {
                removed++;
            }
        }
        return removed;
    }

    private static IReadOnlyList<string> EnumerateFiles(string path, string pattern)
    {
        var rootPath = Path.GetFullPath(path);
        PackageStorePathGuard.EnsureSafe(rootPath, rootPath);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.TryPop(out var currentPath))
        {
            foreach (var filePath in Directory.EnumerateFiles(
                         currentPath,
                         pattern,
                         SearchOption.TopDirectoryOnly).ToArray())
            {
                PackageStorePathGuard.EnsureSafe(filePath, rootPath);
                files.Add(filePath);
            }
            foreach (var directoryPath in Directory.EnumerateDirectories(currentPath).ToArray())
            {
                PackageStorePathGuard.EnsureSafe(directoryPath, rootPath);
                pending.Push(directoryPath);
            }
        }
        return Array.AsReadOnly(files.ToArray());
    }

    private static IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var rootPath = Path.GetFullPath(path);
        PackageStorePathGuard.EnsureSafe(rootPath, rootPath);
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.TryPop(out var currentPath))
        {
            foreach (var directoryPath in Directory.EnumerateDirectories(currentPath).ToArray())
            {
                PackageStorePathGuard.EnsureSafe(directoryPath, rootPath);
                directories.Add(directoryPath);
                pending.Push(directoryPath);
            }
        }
        return Array.AsReadOnly(directories.ToArray());
    }

    private static string GetArchiveRelativePath(string rootPath, string archivePath)
        => Path.Combine([rootPath, .. archivePath.Split('/')]);

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static IEnumerable<string> GetParentPaths(string path)
    {
        var segments = path.Split('/');
        for (var length = 1; length < segments.Length; length++)
        {
            yield return string.Join('/', segments.Take(length));
        }
    }

    private static InvalidDataException InvalidReceipt(string? path)
        => new(path is null
            ? "The execution runtime receipt is invalid."
            : $"Execution runtime receipt '{path}' is invalid.");

    private static bool PathsEqual(string first, string second)
        => string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void Hit(ExecutionRuntimeStoreFaultPoint point) => _faultInjector?.Hit(point);

    internal static void TryDeleteDirectory(string? path)
    {
        _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(path);
    }

    internal static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Content-addressed garbage collection retries disposable cleanup.
        }
    }

    private sealed class ExecutionRuntimeSingleFlight(
        ExecutionRuntimeAcquisitionPlan plan,
        Func<CancellationToken, Task<ExecutionRuntimeEnsureResult>> operation,
        Action? releaseClaimedSource)
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _operationCancellation = new();
        private readonly TaskCompletionSource<ExecutionRuntimeEnsureResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiters = 1;
        private bool _started;
        private bool _accepting = true;
        private int _sourceReleased;

        public Task<ExecutionRuntimeEnsureResult> Task => _completion.Task;

        public ExecutionRuntimeAcquisitionPlan Plan { get; } = plan;

        public void Start()
        {
            lock (_gate)
            {
                if (_started)
                {
                    throw new InvalidOperationException("The execution runtime single-flight already started.");
                }
                _started = true;
            }
            _ = RunAsync();
        }

        public void DisposeUnused()
        {
            lock (_gate)
            {
                if (_started)
                {
                    throw new InvalidOperationException("A started execution runtime single-flight cannot be discarded.");
                }
                _accepting = false;
            }
            ReleaseSource();
            _operationCancellation.Dispose();
        }

        public bool TryJoin(
            ExecutionRuntimeAcquisitionPlan joiningPlan,
            out bool planConflict)
        {
            lock (_gate)
            {
                planConflict = false;
                if (!_accepting)
                {
                    return false;
                }
                if (Plan != joiningPlan)
                {
                    planConflict = true;
                    return false;
                }
                _waiters++;
                return true;
            }
        }

        public bool ReleaseWaiter()
        {
            lock (_gate)
            {
                if (_waiters <= 0)
                {
                    throw new InvalidOperationException("The execution runtime single-flight has no waiter to release.");
                }
                _waiters--;
                if (_waiters == 0 && !_completion.Task.IsCompleted)
                {
                    _accepting = false;
                    return true;
                }
                return false;
            }
        }

        public void CancelOperation()
        {
            try
            {
                _operationCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Producer completion won the cancellation race.
            }
            catch (AggregateException)
            {
                // Callback failures cannot replace the final waiter's cancellation.
            }
        }

        private async Task RunAsync()
        {
            await System.Threading.Tasks.Task.Yield();
            ExecutionRuntimeEnsureResult? result = null;
            Exception? error = null;
            CancellationToken? canceledToken = null;
            try
            {
                result = await operation(_operationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                canceledToken = exception.CancellationToken;
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                lock (_gate)
                {
                    _accepting = false;
                }
                ReleaseSource();
                _operationCancellation.Dispose();
            }

            if (error is not null)
            {
                _completion.TrySetException(error);
            }
            else if (canceledToken is { } token)
            {
                _completion.TrySetCanceled(token);
            }
            else
            {
                _completion.TrySetResult(result!);
            }
        }

        private void ReleaseSource()
        {
            if (Interlocked.Exchange(ref _sourceReleased, 1) == 0)
            {
                releaseClaimedSource?.Invoke();
            }
        }
    }
}
