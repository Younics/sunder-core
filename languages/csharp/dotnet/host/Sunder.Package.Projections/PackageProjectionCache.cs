using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Runtime.LocalState;

namespace Sunder.Package.Projections;

public sealed class PackageProjectionCache
{
    public const int CurrentFormatRevision = 1;
    public const int CurrentReceiptSchemaVersion = 1;
    public const string DefaultRulesIdentity = "sunder.package.projection.rules.v1";
    private const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private const int MaximumProjectionFiles = 100_000;
    private const int MaximumProcessTreeProcesses = 8;
    private readonly PackageProjectionLayout _layout;
    private readonly PackageProjectionCacheOptions _options;
    private readonly IPackageProjectionProcessIdentityProvider _processes;
    private readonly TimeProvider _timeProvider;

    public PackageProjectionCache(
        string? runtimeStateRootPath = null,
        PackageProjectionCacheOptions? options = null,
        IPackageProjectionProcessIdentityProvider? processes = null,
        TimeProvider? timeProvider = null,
        string? executionRuntimeLeaseRootPath = null)
    {
        _layout = new PackageProjectionLayout(
            runtimeStateRootPath ?? RuntimeLocalState.GetV1RootPath(),
            executionRuntimeLeaseRootPath);
        _options = options ?? new PackageProjectionCacheOptions();
        _processes = processes ?? new SystemPackageProjectionProcessIdentityProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(_options);
    }

    public string RootPath => _layout.RootPath;

    public static string ComputeCacheKey(PackageProjectionRequest request)
        => Canonicalize(request).CacheKey;

    public async Task<PackageProjectionAcquisition> GetOrBuildAndLeaseAsync(
        PackageProjectionRequest request,
        PackageProjectionOwner owner,
        CancellationToken cancellationToken = default)
    {
        var canonical = Canonicalize(request);
        ValidateOwner(owner, canonical.PackageId);
        EnsureRuntimeStateInitialized();
        using var familyLock = await AcquireSharedFamilyLockAsync(cancellationToken).ConfigureAwait(false);
        EnsureInitializedUnderFamilyLock();
        using var keyLock = await AcquireKeyLockAsync(canonical.Role, canonical.CacheKey, cancellationToken)
            .ConfigureAwait(false);
        var expected = ExpectedProjection.From(canonical, canonical.PackageId);
        var validation = await ValidateEntryAsync(expected, canonical.WarmValidationPaths, deep: false, cancellationToken)
            .ConfigureAwait(false);
        if (validation.Receipt is not null)
        {
            var lease = await CreateLeaseUnderKeyLockAsync(validation.Receipt, owner, cancellationToken)
                .ConfigureAwait(false);
            RemoveTombstoneUnderKeyLock(canonical.Role, canonical.CacheKey);
            return CreateAcquisition(validation, PackageProjectionCacheOutcome.Hit, lease);
        }

        var outcome = PackageProjectionCacheOutcome.Miss;
        if (validation.Corrupt)
        {
            if (await HasValidLeaseUnderKeyLockAsync(canonical.Role, canonical.CacheKey, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    $"Projection '{canonical.CacheKey}' is corrupt but remains protected by a live process lease.",
                    validation.Exception);
            }
            QuarantineEntryUnderKeyLock(canonical.Role, canonical.CacheKey, "corrupt");
            outcome = PackageProjectionCacheOutcome.CorruptRebuild;
        }

        var built = await BuildUnderKeyLockAsync(canonical, cancellationToken).ConfigureAwait(false);
        var builtLease = await CreateLeaseUnderKeyLockAsync(built.Receipt!, owner, cancellationToken)
            .ConfigureAwait(false);
        RemoveTombstoneUnderKeyLock(canonical.Role, canonical.CacheKey);
        return CreateAcquisition(built, outcome, builtLease);
    }

    public async Task<PackageProjectionAcquisition> AcquireAndLeaseAsync(
        PackageProjectionReference reference,
        PackageProjectionOwner owner,
        IReadOnlyList<string>? warmValidationPaths = null,
        CancellationToken cancellationToken = default)
    {
        var expected = ValidateReference(reference, owner.PackageId);
        ValidateOwner(owner, owner.PackageId);
        var warmPaths = NormalizeWarmValidationPaths(warmValidationPaths);
        EnsureRuntimeStateInitialized();
        using var familyLock = await AcquireSharedFamilyLockAsync(cancellationToken).ConfigureAwait(false);
        EnsureInitializedUnderFamilyLock();
        using var keyLock = await AcquireKeyLockAsync(reference.ConsumerRole, reference.CacheKey, cancellationToken)
            .ConfigureAwait(false);
        var validation = await ValidateEntryAsync(expected, warmPaths, deep: false, cancellationToken)
            .ConfigureAwait(false);
        if (validation.Receipt is null)
        {
            throw new InvalidDataException(
                validation.Corrupt
                    ? $"Projection reference '{reference.CacheKey}' resolves to corrupt content."
                    : $"Projection reference '{reference.CacheKey}' is no longer available.",
                validation.Exception);
        }
        var lease = await CreateLeaseUnderKeyLockAsync(validation.Receipt, owner, cancellationToken)
            .ConfigureAwait(false);
        RemoveTombstoneUnderKeyLock(reference.ConsumerRole, reference.CacheKey);
        return CreateAcquisition(validation, PackageProjectionCacheOutcome.Hit, lease);
    }

    public async Task<PackageProjectionCacheMetrics> VerifyAsync(
        PackageProjectionReference reference,
        bool deep,
        CancellationToken cancellationToken = default)
    {
        var expected = ValidateReference(reference, packageId: null);
        EnsureRuntimeStateInitialized();
        using var familyLock = await AcquireSharedFamilyLockAsync(cancellationToken).ConfigureAwait(false);
        EnsureInitializedUnderFamilyLock();
        using var keyLock = await AcquireKeyLockAsync(reference.ConsumerRole, reference.CacheKey, cancellationToken)
            .ConfigureAwait(false);
        var validation = await ValidateEntryAsync(expected, [], deep, cancellationToken).ConfigureAwait(false);
        if (validation.Receipt is null)
        {
            throw new InvalidDataException(
                validation.Corrupt
                    ? $"Projection '{reference.CacheKey}' failed verification."
                    : $"Projection '{reference.CacheKey}' is missing.",
                validation.Exception);
        }
        return validation.Metrics;
    }

    public async Task<int> RecoverLeasesAsync(CancellationToken cancellationToken = default)
    {
        EnsureRuntimeStateInitialized();
        using var familyLock = await AcquireSharedFamilyLockAsync(cancellationToken).ConfigureAwait(false);
        EnsureInitializedUnderFamilyLock();
        using var maintenanceLock = await PackageProjectionFileLock.AcquireAsync(
                _layout.MaintenanceLockPath,
                _options.LockTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var recovery = await RecoverLeasesUnderMaintenanceLockAsync(cancellationToken).ConfigureAwait(false);
        return recovery.Recovered;
    }

    public async Task<PackageProjectionOfflineResetScope> AcquireOfflineResetScopeAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureRuntimeStateInitialized();
        var familyLock = await PackageProjectionFamilyLock.AcquireExclusiveAsync(
                _layout.CoordinationLockPath,
                _layout.RuntimeStateRootPath,
                _options.LockTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return new PackageProjectionOfflineResetScope(this, familyLock);
    }

    public async Task<PackageProjectionGarbageCollectionResult> CollectGarbageAsync(
        IReadOnlySet<string> reachableSourceContentIdentities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reachableSourceContentIdentities);
        foreach (var identity in reachableSourceContentIdentities)
        {
            PackageProjectionLayout.ValidateCacheKey(identity, nameof(reachableSourceContentIdentities));
        }

        EnsureRuntimeStateInitialized();
        using var familyLock = await AcquireSharedFamilyLockAsync(cancellationToken).ConfigureAwait(false);
        EnsureInitializedUnderFamilyLock();
        using var maintenanceLock = await PackageProjectionFileLock.AcquireAsync(
                _layout.MaintenanceLockPath,
                _options.LockTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var recovery = await RecoverLeasesUnderMaintenanceLockAsync(cancellationToken).ConfigureAwait(false);
        CleanupExpiredDirectories(_layout.StagingRootPath, _options.StagingRetention, cancellationToken);
        CleanupExpiredDirectories(_layout.QuarantineRootPath, _options.QuarantineRetention, cancellationToken);
        CleanupExpiredDirectories(_layout.RetiredRootPath, TimeSpan.Zero, cancellationToken);

        var marked = 0;
        var tombstoned = 0;
        var preserved = 0;
        foreach (var (role, cacheKey, _) in _layout.EnumerateEntries().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var keyLock = await AcquireKeyLockAsync(role, cacheKey, cancellationToken).ConfigureAwait(false);
            var active = await HasValidLeaseUnderKeyLockAsync(role, cacheKey, cancellationToken).ConfigureAwait(false);
            PackageProjectionReceipt receipt;
            try
            {
                receipt = await ReadAndValidateReceiptAsync(role, cacheKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsProjectionDataException(exception))
            {
                if (active)
                {
                    preserved++;
                    continue;
                }
                QuarantineEntryUnderKeyLock(role, cacheKey, "invalid-receipt");
                RemoveTombstoneUnderKeyLock(role, cacheKey);
                tombstoned++;
                continue;
            }

            var reachable = active || reachableSourceContentIdentities.Contains(receipt.SourceContentIdentity);
            var tombstonePath = _layout.GetTombstonePath(role, cacheKey);
            if (reachable)
            {
                PackageProjectionDurableFile.TryDelete(tombstonePath);
                preserved++;
                continue;
            }

            if (!File.Exists(tombstonePath))
            {
                var markedAt = _timeProvider.GetUtcNow();
                await PackageProjectionDurableFile.WriteAsync(
                        tombstonePath,
                        new PackageProjectionTombstoneV1(
                            1,
                            cacheKey,
                            role,
                            receipt.SourceContentIdentity,
                            markedAt,
                            markedAt + _options.GarbageCollectionGracePeriod),
                        cancellationToken)
                    .ConfigureAwait(false);
                marked++;
                continue;
            }

            var (tombstone, _) = await PackageProjectionDurableFile.ReadAsync<PackageProjectionTombstoneV1>(
                    tombstonePath,
                    MaximumMetadataBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateTombstone(tombstone, role, cacheKey, receipt.SourceContentIdentity);
            if (_timeProvider.GetUtcNow() < tombstone.DeleteAfterUtc)
            {
                marked++;
                continue;
            }
            if (await HasValidLeaseUnderKeyLockAsync(role, cacheKey, cancellationToken).ConfigureAwait(false))
            {
                PackageProjectionDurableFile.TryDelete(tombstonePath);
                preserved++;
                continue;
            }

            var entryPath = _layout.GetEntryPath(role, cacheKey);
            if (Directory.Exists(entryPath))
            {
                var retiredPath = _layout.CreateRetiredPath(role, cacheKey, _timeProvider.GetUtcNow());
                SetDirectoryMovable(entryPath);
                PackageProjectionDurableFile.MoveDirectory(entryPath, retiredPath);
                TryDeleteDirectory(retiredPath);
            }
            PackageProjectionDurableFile.TryDelete(tombstonePath);
            tombstoned++;
        }

        return new PackageProjectionGarbageCollectionResult(
            recovery.Active,
            recovery.Recovered,
            marked,
            tombstoned,
            preserved);
    }

    private static PackageProjectionAcquisition CreateAcquisition(
        EntryValidation validation,
        PackageProjectionCacheOutcome outcome,
        PackageProjectionLease lease)
    {
        var receipt = validation.Receipt!;
        return new PackageProjectionAcquisition(
            validation.RootPath,
            new PackageProjectionReference(
                receipt.FormatRevision,
                receipt.CacheKey,
                receipt.ConsumerRole,
                receipt.SourceContentIdentity,
                receipt.ProjectionRulesIdentity,
                receipt.TargetKeys),
            receipt,
            outcome,
            validation.Metrics,
            lease);
    }

    private async Task<EntryValidation> BuildUnderKeyLockAsync(
        CanonicalProjectionRequest request,
        CancellationToken cancellationToken)
    {
        var stagingPath = _layout.GetStagingPath(request.Role);
        var stagingRoot = Path.Combine(stagingPath, "root");
        Directory.CreateDirectory(stagingRoot);
        long bytesRead = 0;
        long bytesHashed = 0;
        long bytesCopied = 0;
        var filesCopied = 0;
        try
        {
            foreach (var file in request.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ToPlatformPath(request.SourceRootPath, file.SourceRelativePath);
                var destinationPath = ToPlatformPath(stagingRoot, file.ProjectionRelativePath);
                EnsureSafeSourcePath(request.SourceRootPath, sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                var metrics = await CopyAndValidateAsync(sourcePath, destinationPath, file, cancellationToken)
                    .ConfigureAwait(false);
                bytesRead = checked(bytesRead + metrics.BytesRead);
                bytesHashed = checked(bytesHashed + metrics.BytesHashed);
                bytesCopied = checked(bytesCopied + metrics.BytesCopied);
                filesCopied++;
                SetFileImmutable(destinationPath, file.IsExecutable);
            }

            var now = _timeProvider.GetUtcNow();
            var receipt = new PackageProjectionReceipt(
                CurrentReceiptSchemaVersion,
                CurrentFormatRevision,
                request.CacheKey,
                request.Role,
                request.PackageId,
                request.SourceContentIdentity,
                request.RulesIdentity,
                request.TargetKeys,
                request.Inventory,
                now,
                now);
            var receiptPath = Path.Combine(stagingPath, "receipt.json");
            await PackageProjectionDurableFile.WriteAsync(receiptPath, receipt, cancellationToken)
                .ConfigureAwait(false);
            var receiptBytes = await File.ReadAllBytesAsync(receiptPath, cancellationToken).ConfigureAwait(false);
            var health = new PackageProjectionHealthV1(
                1,
                request.CacheKey,
                Convert.ToHexStringLower(SHA256.HashData(receiptBytes)),
                now);
            await PackageProjectionDurableFile.WriteAsync(
                    Path.Combine(stagingPath, "health.json"),
                    health,
                    cancellationToken)
                .ConfigureAwait(false);
            var entryPath = _layout.GetEntryPath(request.Role, request.CacheKey);
            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            PackageProjectionDurableFile.MoveDirectory(stagingPath, entryPath);
            try
            {
                SetTreeImmutable(entryPath, request.Inventory);
            }
            catch
            {
                QuarantineEntryUnderKeyLock(request.Role, request.CacheKey, "immutability-failure");
                throw;
            }
            return new EntryValidation(
                receipt,
                _layout.GetProjectionRootPath(request.Role, request.CacheKey),
                new PackageProjectionCacheMetrics(
                    bytesRead,
                    bytesHashed,
                    bytesCopied,
                    request.Files.Count,
                    filesCopied),
                Corrupt: false,
                Exception: null);
        }
        catch
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }
    }

    private async Task<EntryValidation> ValidateEntryAsync(
        ExpectedProjection expected,
        IReadOnlyList<string> warmValidationPaths,
        bool deep,
        CancellationToken cancellationToken)
    {
        var entryPath = _layout.GetEntryPath(expected.Role, expected.CacheKey);
        var rootPath = _layout.GetProjectionRootPath(expected.Role, expected.CacheKey);
        if (!Directory.Exists(entryPath))
        {
            return EntryValidation.Missing(rootPath);
        }
        try
        {
            var receiptPath = _layout.GetReceiptPath(expected.Role, expected.CacheKey);
            var healthPath = _layout.GetHealthPath(expected.Role, expected.CacheKey);
            var receiptBytes = await ReadBoundedBytesAsync(receiptPath, cancellationToken).ConfigureAwait(false);
            var healthBytes = await ReadBoundedBytesAsync(healthPath, cancellationToken).ConfigureAwait(false);
            var receipt = JsonSerializer.Deserialize<PackageProjectionReceipt>(receiptBytes, PackageProjectionJson.Options)
                          ?? throw new InvalidDataException("Projection receipt is empty.");
            var health = JsonSerializer.Deserialize<PackageProjectionHealthV1>(healthBytes, PackageProjectionJson.Options)
                         ?? throw new InvalidDataException("Projection health marker is empty.");
            ValidateReceipt(receipt, expected);
            ValidateHealth(health, receipt, receiptBytes);
            ValidateProjectionTree(rootPath, receipt.Inventory);

            var pathsToHash = deep
                ? receipt.Inventory.Select(static item => item.Path).ToArray()
                : warmValidationPaths;
            long contentBytes = 0;
            var filesHashed = 0;
            var inventory = receipt.Inventory.ToDictionary(static item => item.Path, StringComparer.Ordinal);
            foreach (var relativePath in pathsToHash.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!inventory.TryGetValue(relativePath, out var expectedFile))
                {
                    throw new InvalidDataException(
                        $"Warm validation path '{relativePath}' is absent from projection '{receipt.CacheKey}'.");
                }
                var path = ToPlatformPath(rootPath, relativePath);
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false));
                contentBytes = checked(contentBytes + stream.Length);
                filesHashed++;
                if (!string.Equals(actual, expectedFile.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Projection file '{relativePath}' failed warm hash verification.");
                }
            }

            var metadataBytes = checked(receiptBytes.LongLength + healthBytes.LongLength);
            return new EntryValidation(
                receipt,
                rootPath,
                new PackageProjectionCacheMetrics(
                    checked(metadataBytes + contentBytes),
                    checked(receiptBytes.LongLength + contentBytes),
                    0,
                    filesHashed,
                    0),
                Corrupt: false,
                Exception: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsProjectionDataException(exception))
        {
            return new EntryValidation(
                null,
                rootPath,
                PackageProjectionCacheMetrics.Empty,
                Corrupt: true,
                exception);
        }
    }

    private async Task<PackageProjectionReceipt> ReadAndValidateReceiptAsync(
        PackageProjectionRole role,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var (receipt, _) = await PackageProjectionDurableFile.ReadAsync<PackageProjectionReceipt>(
                _layout.GetReceiptPath(role, cacheKey),
                MaximumMetadataBytes,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateReceipt(receipt, new ExpectedProjection(
            cacheKey,
            role,
            receipt.SourceContentIdentity,
            receipt.ProjectionRulesIdentity,
            receipt.TargetKeys,
            receipt.PackageId));
        return receipt;
    }

    private async Task<PackageProjectionLease> CreateLeaseUnderKeyLockAsync(
        PackageProjectionReceipt receipt,
        PackageProjectionOwner owner,
        CancellationToken cancellationToken)
    {
        var rootPath = _layout.GetProjectionRootPath(receipt.ConsumerRole, receipt.CacheKey);
        if (!Directory.Exists(rootPath))
        {
            throw new InvalidDataException("Projection root disappeared before lease publication.");
        }
        var process = _processes.ObserveCurrent();
        ValidateProcessIdentity(process);
        var leaseId = Guid.NewGuid().ToString("N");
        var lease = new PackageProjectionLeaseV1(
            1,
            leaseId,
            receipt.CacheKey,
            receipt.ConsumerRole,
            rootPath,
            owner.OwnerKind,
            owner.HostInstanceId,
            owner.PackageId,
            owner.GenerationId,
            process.ProcessId,
            process.BirthIdentity,
            process.StartedAtUtc.ToUniversalTime(),
            _timeProvider.GetUtcNow());
        ValidateLease(lease, _layout.GetLeasePath(receipt.ConsumerRole, receipt.CacheKey, leaseId));
        var path = _layout.GetLeasePath(receipt.ConsumerRole, receipt.CacheKey, leaseId);
        try
        {
            await PackageProjectionDurableFile.WriteAsync(path, lease, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            PackageProjectionDurableFile.TryDelete(path);
            throw;
        }
        return new PackageProjectionLease(
            this,
            receipt.ConsumerRole,
            receipt.CacheKey,
            leaseId);
    }

    internal async Task AttachLeaseToProcessTreeAsync(
        PackageProjectionRole role,
        string cacheKey,
        string leaseId,
        IReadOnlyList<PackageProjectionProcessIdentity> processes,
        string? processTreeLeaseId,
        CancellationToken cancellationToken)
    {
        if (role != PackageProjectionRole.Runtime)
        {
            throw new InvalidOperationException("Only Runtime projection leases can be attached to a process tree.");
        }
        PackageProjectionLayout.ValidateCacheKey(cacheKey, nameof(cacheKey));
        var attachedProcesses = ValidateProcessTreeProcesses(processes);
        var processTreeLeasePath = processTreeLeaseId is null
            ? null
            : _layout.GetProcessTreeLeasePath(processTreeLeaseId);

        EnsureRuntimeStateInitialized();
        using var familyLock = await AcquireSharedFamilyLockAsync(cancellationToken).ConfigureAwait(false);
        EnsureInitializedUnderFamilyLock();
        using var keyLock = await AcquireKeyLockAsync(role, cacheKey, cancellationToken).ConfigureAwait(false);
        var path = _layout.GetLeasePath(role, cacheKey, leaseId);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The Runtime projection lease disappeared before process-tree attachment.");
        }
        if (processTreeLeasePath is not null && !File.Exists(processTreeLeasePath))
        {
            throw new InvalidOperationException(
                "The durable execution-runtime process-tree lease disappeared before projection attachment.");
        }

        var lease = await ReadAndValidateLeaseAsync(path, cancellationToken).ConfigureAwait(false);
        var existingProcesses = lease.ProcessTreeProcesses ?? [];
        if (lease.ProcessTreeLeaseId is not null
            && !string.Equals(lease.ProcessTreeLeaseId, processTreeLeaseId, StringComparison.Ordinal)
            || existingProcesses.Count > attachedProcesses.Count
            || !existingProcesses.SequenceEqual(
                attachedProcesses.Take(existingProcesses.Count)))
        {
            throw new InvalidOperationException(
                "The Runtime projection lease is already attached to a different process tree.");
        }

        foreach (var process in attachedProcesses.Skip(existingProcesses.Count))
        {
            if (_processes.Probe(process.ProcessId, process.BirthIdentity) != PackageProjectionProcessProbe.Matching)
            {
                throw new InvalidOperationException(
                    $"Projection process-tree owner {process.ProcessId} no longer matches its captured birth identity.");
            }
        }

        if (existingProcesses.Count == attachedProcesses.Count
            && string.Equals(lease.ProcessTreeLeaseId, processTreeLeaseId, StringComparison.Ordinal))
        {
            return;
        }

        var attached = lease with
        {
            ProcessTreeLeaseId = processTreeLeaseId,
            ProcessTreeProcesses = attachedProcesses,
            ProcessTreeAttachedAtUtc = lease.ProcessTreeAttachedAtUtc ?? _timeProvider.GetUtcNow(),
        };
        ValidateLease(attached, path);
        await PackageProjectionDurableFile.WriteAsync(path, attached, cancellationToken).ConfigureAwait(false);
    }

    internal async Task BindLeaseToProcessTreeAsync(
        PackageProjectionRole role,
        string cacheKey,
        string leaseId,
        string processTreeLeaseId,
        CancellationToken cancellationToken)
    {
        if (role != PackageProjectionRole.Runtime)
        {
            throw new InvalidOperationException("Only Runtime projection leases can be bound to a process tree.");
        }
        PackageProjectionLayout.ValidateCacheKey(cacheKey, nameof(cacheKey));
        var processTreeLeasePath = _layout.GetProcessTreeLeasePath(processTreeLeaseId);

        EnsureRuntimeStateInitialized();
        using var familyLock = await AcquireSharedFamilyLockAsync(cancellationToken).ConfigureAwait(false);
        EnsureInitializedUnderFamilyLock();
        using var keyLock = await AcquireKeyLockAsync(role, cacheKey, cancellationToken).ConfigureAwait(false);
        var path = _layout.GetLeasePath(role, cacheKey, leaseId);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The Runtime projection lease disappeared before process-tree binding.");
        }
        if (!File.Exists(processTreeLeasePath))
        {
            throw new InvalidOperationException(
                "The durable execution-runtime process-tree lease disappeared before projection binding.");
        }

        var lease = await ReadAndValidateLeaseAsync(path, cancellationToken).ConfigureAwait(false);
        if (lease.ProcessTreeLeaseId is not null
            && !string.Equals(lease.ProcessTreeLeaseId, processTreeLeaseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Runtime projection lease is already bound to a different process tree.");
        }
        if (string.Equals(lease.ProcessTreeLeaseId, processTreeLeaseId, StringComparison.Ordinal))
        {
            return;
        }

        var bound = lease with
        {
            ProcessTreeLeaseId = processTreeLeaseId,
            ProcessTreeAttachedAtUtc = lease.ProcessTreeAttachedAtUtc ?? _timeProvider.GetUtcNow(),
        };
        ValidateLease(bound, path);
        await PackageProjectionDurableFile.WriteAsync(path, bound, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ReleaseLeaseAsync(
        PackageProjectionRole role,
        string cacheKey,
        string leaseId,
        CancellationToken cancellationToken)
    {
        EnsureRuntimeStateInitialized();
        using var familyLock = await AcquireSharedFamilyLockAsync(cancellationToken).ConfigureAwait(false);
        EnsureInitializedUnderFamilyLock();
        using var keyLock = await AcquireKeyLockAsync(role, cacheKey, cancellationToken).ConfigureAwait(false);
        var path = _layout.GetLeasePath(role, cacheKey, leaseId);
        if (!File.Exists(path))
        {
            return;
        }
        var lease = await ReadAndValidateLeaseAsync(path, cancellationToken).ConfigureAwait(false);
        if ((lease.ProcessTreeLeaseId is not null || lease.ProcessTreeProcesses is not null)
            && IsLeaseProcessTreeLive(lease, preserveBoundReservationOwner: false))
        {
            throw new InvalidOperationException(
                "A Runtime projection lease cannot be released before its Worker process tree exits.");
        }
        PackageProjectionDurableFile.Delete(path);
    }

    private async Task<bool> HasValidLeaseUnderKeyLockAsync(
        PackageProjectionRole role,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var directory = _layout.GetLeaseDirectory(role, cacheKey);
        if (!Directory.Exists(directory))
        {
            return false;
        }
        var active = false;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = await ReadAndValidateLeaseAsync(path, cancellationToken).ConfigureAwait(false);
            if (!IsLeaseLive(lease))
            {
                PackageProjectionDurableFile.Delete(path);
                continue;
            }
            active = true;
        }
        return active;
    }

    private async Task<(int Active, int Recovered)> RecoverLeasesUnderMaintenanceLockAsync(
        CancellationToken cancellationToken)
    {
        var active = 0;
        var recovered = 0;
        foreach (var path in _layout.EnumerateLeaseFiles().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageProjectionLeaseV1 lease;
            try
            {
                lease = await ReadAndValidateLeaseAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (IsProjectionDataException(exception))
            {
                throw new InvalidDataException(
                    $"Projection lease '{path}' is invalid; recovery cannot safely infer ownership.",
                    exception);
            }
            using var keyLock = await AcquireKeyLockAsync(
                    lease.ConsumerRole,
                    lease.CacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!File.Exists(path))
            {
                continue;
            }
            lease = await ReadAndValidateLeaseAsync(path, cancellationToken).ConfigureAwait(false);
            if (!IsLeaseLive(lease))
            {
                PackageProjectionDurableFile.Delete(path);
                recovered++;
            }
            else
            {
                active++;
            }
        }
        return (active, recovered);
    }

    internal async Task<PackageProjectionOfflineResetInspection> InspectOfflineResetLeasesAsync(
        CancellationToken cancellationToken)
    {
        var active = 0;
        var stalePaths = new List<string>();
        foreach (var path in _layout.EnumerateLeaseFiles().Order(StringComparer.Ordinal).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageProjectionLeaseV1 lease;
            try
            {
                lease = await ReadAndValidateLeaseAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (IsProjectionDataException(exception))
            {
                throw new InvalidDataException(
                    $"Projection lease '{path}' is invalid; reset cannot safely infer ownership.",
                    exception);
            }

            if (IsLeaseLive(lease))
            {
                active++;
            }
            else
            {
                stalePaths.Add(path);
            }
        }

        return new PackageProjectionOfflineResetInspection(active, stalePaths);
    }

    internal Task<PackageProjectionLeaseRecoveryResult> PrepareOfflineResetForDeletionAsync(
        PackageProjectionOfflineResetInspection inspection,
        CancellationToken cancellationToken)
    {
        var recovered = 0;
        foreach (var path in inspection.StaleLeasePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                continue;
            }
            PackageProjectionDurableFile.Delete(path);
            recovered++;
        }
        if (Directory.Exists(_layout.RootPath))
        {
            MakeTreeWritable(_layout.RootPath);
        }
        return Task.FromResult(new PackageProjectionLeaseRecoveryResult(inspection.ActiveLeaseCount, recovered));
    }

    private async Task<PackageProjectionLeaseV1> ReadAndValidateLeaseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var (lease, _) = await PackageProjectionDurableFile.ReadAsync<PackageProjectionLeaseV1>(
                path,
                MaximumMetadataBytes,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateLease(lease, path);
        return lease;
    }

    private void ValidateLease(PackageProjectionLeaseV1 lease, string path)
    {
        PackageProjectionLayout.ValidateCacheKey(lease.CacheKey, nameof(lease.CacheKey));
        if (lease.SchemaVersion != 1
            || !Enum.IsDefined(lease.ConsumerRole)
            || !Guid.TryParseExact(lease.LeaseId, "N", out _)
            || lease.HostInstanceId == Guid.Empty
            || string.IsNullOrWhiteSpace(lease.OwnerKind)
            || string.IsNullOrWhiteSpace(lease.PackageId)
            || string.IsNullOrWhiteSpace(lease.GenerationId)
            || lease.CreatedAtUtc == default)
        {
            throw new InvalidDataException("Projection lease metadata is invalid.");
        }
        ValidateProcessIdentity(new PackageProjectionProcessIdentity(
            lease.OwnerProcessId,
            lease.OwnerProcessBirthIdentity,
            lease.OwnerProcessStartedAtUtc));
        var hasProcessTreeAttachment = lease.ProcessTreeLeaseId is not null
                                       || lease.ProcessTreeProcesses is not null;
        if (hasProcessTreeAttachment != (lease.ProcessTreeAttachedAtUtc is not null)
            || hasProcessTreeAttachment && lease.ConsumerRole != PackageProjectionRole.Runtime
            || lease.ProcessTreeAttachedAtUtc is { } attachedAtUtc
                && attachedAtUtc < lease.CreatedAtUtc)
        {
            throw new InvalidDataException("Projection process-tree attachment metadata is invalid.");
        }
        if (lease.ProcessTreeProcesses is not null)
        {
            _ = ValidateProcessTreeProcesses(lease.ProcessTreeProcesses);
        }
        if (lease.ProcessTreeLeaseId is not null)
        {
            _ = _layout.GetProcessTreeLeasePath(lease.ProcessTreeLeaseId);
        }
        var expectedPath = _layout.GetLeasePath(lease.ConsumerRole, lease.CacheKey, lease.LeaseId);
        if (!PathsEqual(path, expectedPath)
            || !PathsEqual(
                lease.ProjectionRootPath,
                _layout.GetProjectionRootPath(lease.ConsumerRole, lease.CacheKey)))
        {
            throw new InvalidDataException("Projection lease path identity is invalid.");
        }
    }

    private static void ValidateProcessIdentity(PackageProjectionProcessIdentity process)
    {
        if (process.ProcessId <= 0
            || string.IsNullOrWhiteSpace(process.BirthIdentity)
            || process.BirthIdentity.Any(char.IsControl)
            || process.StartedAtUtc == default)
        {
            throw new InvalidDataException("Projection owner process identity is invalid.");
        }
    }

    private static IReadOnlyList<PackageProjectionProcessIdentity> ValidateProcessTreeProcesses(
        IReadOnlyList<PackageProjectionProcessIdentity> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (processes.Count is <= 0 or > MaximumProcessTreeProcesses)
        {
            throw new ArgumentException("A bounded non-empty process tree identity is required.", nameof(processes));
        }
        var snapshot = processes.ToArray();
        foreach (var process in snapshot)
        {
            ArgumentNullException.ThrowIfNull(process);
            ValidateProcessIdentity(process);
        }
        if (snapshot.Select(static process => process.ProcessId).Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("Process tree identities must use distinct process ids.", nameof(processes));
        }
        return snapshot;
    }

    private bool IsLeaseLive(PackageProjectionLeaseV1 lease)
        => lease.ProcessTreeLeaseId is null && lease.ProcessTreeProcesses is null
            ? IsProcessLive(lease.OwnerProcessId, lease.OwnerProcessBirthIdentity)
            : IsLeaseProcessTreeLive(lease, preserveBoundReservationOwner: true);

    private bool IsLeaseProcessTreeLive(
        PackageProjectionLeaseV1 lease,
        bool preserveBoundReservationOwner)
    {
        if (lease.ProcessTreeLeaseId is not null)
        {
            var durableTree = ExecutionRuntimeProcessTreeLease.Probe(
                _layout.GetProcessTreeLeasePath(lease.ProcessTreeLeaseId),
                lease.ProcessTreeLeaseId,
                (processId, birthIdentity) => _processes.Probe(processId, birthIdentity));
            if (durableTree is ExecutionRuntimeProcessTreeLeaseProbe.Active
                or ExecutionRuntimeProcessTreeLeaseProbe.IdentityMismatch
                or ExecutionRuntimeProcessTreeLeaseProbe.Unavailable)
            {
                return true;
            }
        }
        if (lease.ProcessTreeProcesses is not null)
        {
            return lease.ProcessTreeProcesses.Any(process =>
                IsProcessLive(process.ProcessId, process.BirthIdentity));
        }
        return preserveBoundReservationOwner
               && IsProcessLive(lease.OwnerProcessId, lease.OwnerProcessBirthIdentity);
    }

    private bool IsProcessLive(int processId, string birthIdentity)
        => _processes.Probe(processId, birthIdentity) is
            PackageProjectionProcessProbe.Matching or PackageProjectionProcessProbe.Unavailable;

    private static void ValidateReceipt(PackageProjectionReceipt receipt, ExpectedProjection expected)
    {
        if (receipt.SchemaVersion != CurrentReceiptSchemaVersion
            || receipt.FormatRevision != CurrentFormatRevision
            || receipt.ConsumerRole != expected.Role
            || !string.Equals(receipt.CacheKey, expected.CacheKey, StringComparison.Ordinal)
            || !string.Equals(receipt.SourceContentIdentity, expected.SourceContentIdentity, StringComparison.Ordinal)
            || !string.Equals(receipt.ProjectionRulesIdentity, expected.RulesIdentity, StringComparison.Ordinal)
            || !receipt.TargetKeys.SequenceEqual(expected.TargetKeys, StringComparer.Ordinal)
            || (expected.PackageId is not null
                && !string.Equals(receipt.PackageId, expected.PackageId, StringComparison.OrdinalIgnoreCase))
            || receipt.CreatedAtUtc == default
            || receipt.VerifiedAtUtc == default
            || string.IsNullOrWhiteSpace(receipt.PackageId))
        {
            throw new InvalidDataException("Projection receipt identity is invalid.");
        }
        PackageProjectionLayout.ValidateCacheKey(receipt.SourceContentIdentity, nameof(receipt.SourceContentIdentity));
        if (receipt.Inventory.Count is <= 0 or > MaximumProjectionFiles)
        {
            throw new InvalidDataException("Projection receipt inventory count is invalid.");
        }
        var ordered = receipt.Inventory.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray();
        if (!receipt.Inventory.SequenceEqual(ordered)
            || ordered.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new InvalidDataException("Projection receipt inventory is not canonical.");
        }
        foreach (var item in ordered)
        {
            PackageProjectionLayout.NormalizeRelativePath(item.Path, nameof(item.Path));
            PackageProjectionLayout.ValidateCacheKey(item.Sha256, nameof(item.Sha256));
            if (item.Size < 0)
            {
                throw new InvalidDataException("Projection receipt contains a negative file size.");
            }
        }
        var computed = PackageProjectionLayout.ComputeKey(
            receipt.ConsumerRole,
            receipt.SourceContentIdentity,
            receipt.ProjectionRulesIdentity,
            receipt.TargetKeys,
            receipt.Inventory);
        if (!string.Equals(computed, receipt.CacheKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Projection receipt key is not canonical.");
        }
    }

    private static void ValidateHealth(
        PackageProjectionHealthV1 health,
        PackageProjectionReceipt receipt,
        byte[] receiptBytes)
    {
        if (health.SchemaVersion != 1
            || health.CompletedAtUtc == default
            || !string.Equals(health.CacheKey, receipt.CacheKey, StringComparison.Ordinal)
            || !string.Equals(
                health.ReceiptSha256,
                Convert.ToHexStringLower(SHA256.HashData(receiptBytes)),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Projection health marker is invalid.");
        }
    }

    private static void ValidateProjectionTree(
        string rootPath,
        IReadOnlyList<PackageProjectionInventoryEntry> inventory)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new InvalidDataException("Projection root is missing.");
        }
        EnsureNotReparsePoint(rootPath);
        foreach (var directory in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
        {
            EnsureNotReparsePoint(directory);
        }
        var actual = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/'),
                PhysicalPath = path,
            })
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ToArray();
        if (!actual.Select(static item => item.Path)
                .SequenceEqual(inventory.Select(static item => item.Path), StringComparer.Ordinal))
        {
            throw new InvalidDataException("Projection root file inventory does not match its receipt.");
        }
        for (var index = 0; index < actual.Length; index++)
        {
            EnsureNotReparsePoint(actual[index].PhysicalPath);
            if (new FileInfo(actual[index].PhysicalPath).Length != inventory[index].Size)
            {
                throw new InvalidDataException(
                    $"Projection file '{actual[index].Path}' length does not match its receipt.");
            }
        }
    }

    private static CanonicalProjectionRequest Canonicalize(PackageProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceRoot = Path.GetFullPath(request.SourceRootPath);
        if (string.IsNullOrWhiteSpace(request.PackageId)
            || request.PackageId.Any(char.IsControl))
        {
            throw new ArgumentException("A package id is required.", nameof(request));
        }
        PackageProjectionLayout.ValidateCacheKey(request.SourceContentIdentity, nameof(request.SourceContentIdentity));
        if (!Enum.IsDefined(request.ConsumerRole)
            || string.IsNullOrWhiteSpace(request.ProjectionRulesIdentity)
            || request.ProjectionRulesIdentity.Any(char.IsControl))
        {
            throw new ArgumentException("Projection role or rules identity is invalid.", nameof(request));
        }
        var targetKeys = request.TargetKeys
            .Select(static value => ValidateTargetKey(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (targetKeys.Length == 0 || targetKeys.Length != request.TargetKeys.Count)
        {
            throw new ArgumentException("One or more unique exact target keys are required.", nameof(request));
        }
        if (request.Files.Count is <= 0 or > MaximumProjectionFiles)
        {
            throw new ArgumentException("Projection file count is invalid.", nameof(request));
        }
        var files = request.Files.Select(static file =>
        {
            ArgumentNullException.ThrowIfNull(file);
            var source = PackageProjectionLayout.NormalizeRelativePath(
                file.SourceRelativePath,
                nameof(file.SourceRelativePath));
            var projection = PackageProjectionLayout.NormalizeRelativePath(
                file.ProjectionRelativePath,
                nameof(file.ProjectionRelativePath));
            PackageProjectionLayout.ValidateCacheKey(file.Sha256, nameof(file.Sha256));
            if (file.Size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(file.Size));
            }
            return file with { SourceRelativePath = source, ProjectionRelativePath = projection };
        }).OrderBy(static file => file.ProjectionRelativePath, StringComparer.Ordinal).ToArray();
        if (files.Select(static file => file.ProjectionRelativePath)
                .Distinct(StringComparer.Ordinal).Count() != files.Length)
        {
            throw new ArgumentException("Projection destination paths must be unique.", nameof(request));
        }
        var inventory = files.Select(static file => new PackageProjectionInventoryEntry(
            file.ProjectionRelativePath,
            file.Sha256,
            file.Size,
            file.IsExecutable)).ToArray();
        var cacheKey = PackageProjectionLayout.ComputeKey(
            request.ConsumerRole,
            request.SourceContentIdentity,
            request.ProjectionRulesIdentity,
            targetKeys,
            inventory);
        return new CanonicalProjectionRequest(
            sourceRoot,
            request.PackageId,
            request.SourceContentIdentity,
            request.ConsumerRole,
            request.ProjectionRulesIdentity,
            targetKeys,
            files,
            inventory,
            NormalizeWarmValidationPaths(request.WarmValidationPaths),
            cacheKey);
    }

    private static ExpectedProjection ValidateReference(
        PackageProjectionReference reference,
        string? packageId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        PackageProjectionLayout.ValidateCacheKey(reference.CacheKey, nameof(reference.CacheKey));
        PackageProjectionLayout.ValidateCacheKey(reference.SourceContentIdentity, nameof(reference.SourceContentIdentity));
        if (reference.FormatRevision != CurrentFormatRevision
            || !Enum.IsDefined(reference.ConsumerRole)
            || string.IsNullOrWhiteSpace(reference.ProjectionRulesIdentity))
        {
            throw new InvalidDataException("Projection reference metadata is invalid.");
        }
        var targets = reference.TargetKeys
            .Select(static value => ValidateTargetKey(value))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (targets.Length == 0
            || !targets.SequenceEqual(reference.TargetKeys, StringComparer.Ordinal)
            || targets.Distinct(StringComparer.Ordinal).Count() != targets.Length)
        {
            throw new InvalidDataException("Projection reference target keys are not canonical.");
        }
        return new ExpectedProjection(
            reference.CacheKey,
            reference.ConsumerRole,
            reference.SourceContentIdentity,
            reference.ProjectionRulesIdentity,
            targets,
            packageId);
    }

    private static IReadOnlyList<string> NormalizeWarmValidationPaths(IReadOnlyList<string>? paths)
        => (paths ?? [])
            .Select(static path => PackageProjectionLayout.NormalizeRelativePath(path, nameof(paths)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ValidateTargetKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Projection target key is invalid.", nameof(value));
        }
        return value;
    }

    private static void ValidateOwner(PackageProjectionOwner owner, string packageId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(owner.OwnerKind)
            || owner.OwnerKind.Any(char.IsControl)
            || owner.HostInstanceId == Guid.Empty
            || string.IsNullOrWhiteSpace(owner.PackageId)
            || string.IsNullOrWhiteSpace(owner.GenerationId)
            || owner.GenerationId.Any(char.IsControl)
            || !string.Equals(owner.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Projection lease owner metadata is invalid.", nameof(owner));
        }
    }

    private static void ValidateOptions(PackageProjectionCacheOptions options)
    {
        if (options.LockTimeout <= TimeSpan.Zero
            || options.GarbageCollectionGracePeriod < TimeSpan.Zero
            || options.StagingRetention < TimeSpan.Zero
            || options.QuarantineRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static async Task<PackageProjectionCacheMetrics> CopyAndValidateAsync(
        string sourcePath,
        string destinationPath,
        PackageProjectionSourceFile file,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != file.Size)
        {
            throw new InvalidDataException(
                $"Projection source file '{file.SourceRelativePath}' length changed before publication.");
        }
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long length = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                length = checked(length + read);
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (length != file.Size || !string.Equals(actual, file.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Projection source file '{file.SourceRelativePath}' failed hash validation.");
        }
        return new PackageProjectionCacheMetrics(length, length, length, 1, 1);
    }

    private void EnsureRuntimeStateInitialized()
        => RuntimeLocalState.EnsureInitialized(
            _layout.RuntimeStateRootPath,
            deleteStaleSchemaTemporaryFiles: false);

    private void EnsureInitializedUnderFamilyLock()
    {
        foreach (var root in _layout.DurableRootPaths)
        {
            Directory.CreateDirectory(root);
            PackageProjectionLayout.EnsureContained(root, _layout.RuntimeStateRootPath);
            EnsureNotReparsePoint(root);
        }
    }

    private Task<PackageProjectionFamilyLock> AcquireSharedFamilyLockAsync(
        CancellationToken cancellationToken)
        => PackageProjectionFamilyLock.AcquireSharedAsync(
            _layout.CoordinationLockPath,
            _layout.RuntimeStateRootPath,
            _options.LockTimeout,
            cancellationToken);

    private Task<PackageProjectionFileLock> AcquireKeyLockAsync(
        PackageProjectionRole role,
        string cacheKey,
        CancellationToken cancellationToken)
        => PackageProjectionFileLock.AcquireAsync(
            _layout.GetKeyLockPath(role, cacheKey),
            _options.LockTimeout,
            cancellationToken);

    private void QuarantineEntryUnderKeyLock(
        PackageProjectionRole role,
        string cacheKey,
        string reason)
    {
        var entryPath = _layout.GetEntryPath(role, cacheKey);
        if (!Directory.Exists(entryPath))
        {
            return;
        }
        SetDirectoryMovable(entryPath);
        PackageProjectionDurableFile.MoveDirectory(
            entryPath,
            _layout.CreateQuarantinePath(role, cacheKey, reason, _timeProvider.GetUtcNow()));
    }

    private void RemoveTombstoneUnderKeyLock(PackageProjectionRole role, string cacheKey)
        => PackageProjectionDurableFile.TryDelete(_layout.GetTombstonePath(role, cacheKey));

    private static void ValidateTombstone(
        PackageProjectionTombstoneV1 tombstone,
        PackageProjectionRole role,
        string cacheKey,
        string contentIdentity)
    {
        if (tombstone.SchemaVersion != 1
            || tombstone.ConsumerRole != role
            || !string.Equals(tombstone.CacheKey, cacheKey, StringComparison.Ordinal)
            || !string.Equals(tombstone.SourceContentIdentity, contentIdentity, StringComparison.Ordinal)
            || tombstone.MarkedAtUtc == default
            || tombstone.DeleteAfterUtc < tombstone.MarkedAtUtc)
        {
            throw new InvalidDataException("Projection tombstone metadata is invalid.");
        }
    }

    private static void CleanupExpiredDirectories(
        string root,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        var cutoff = DateTime.UtcNow - retention;
        foreach (var roleRoot in Directory.EnumerateDirectories(root))
        {
            foreach (var path in Directory.EnumerateDirectories(roleRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(path) && Directory.GetLastWriteTimeUtc(path) <= cutoff)
                {
                    TryDeleteDirectory(path);
                }
            }
        }
    }

    private static void SetTreeImmutable(
        string entryPath,
        IReadOnlyList<PackageProjectionInventoryEntry> inventory)
    {
        foreach (var item in inventory)
        {
            SetFileImmutable(ToPlatformPath(Path.Combine(entryPath, "root"), item.Path), item.IsExecutable);
        }
        SetFileImmutable(Path.Combine(entryPath, "receipt.json"), executable: false);
        SetFileImmutable(Path.Combine(entryPath, "health.json"), executable: false);
        if (!OperatingSystem.IsWindows())
        {
            foreach (var directory in Directory.EnumerateDirectories(entryPath, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            File.SetUnixFileMode(
                entryPath,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void SetFileImmutable(string path, bool executable)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            return;
        }
        var mode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if (executable)
        {
            mode |= UnixFileMode.UserExecute;
        }
        File.SetUnixFileMode(path, mode);
    }

    private static void SetDirectoryMovable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.Directory);
            return;
        }
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void EnsureSafeSourcePath(string sourceRoot, string sourcePath)
    {
        PackageProjectionLayout.EnsureContained(sourcePath, sourceRoot);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Projection source file is missing.", sourcePath);
        }
        var current = sourcePath;
        while (!PathsEqual(current, sourceRoot))
        {
            EnsureNotReparsePoint(current);
            current = Path.GetDirectoryName(current)
                      ?? throw new InvalidDataException("Projection source path has no trusted parent.");
        }
        EnsureNotReparsePoint(sourceRoot);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Projection path '{path}' must not be a reparse point.");
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumMetadataBytes)
        {
            throw new InvalidDataException($"Projection metadata '{path}' has an invalid size.");
        }
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static string ToPlatformPath(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        PackageProjectionLayout.EnsureContained(path, root);
        return path;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsProjectionDataException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or CryptographicException
            or ArgumentException;

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        try
        {
            MakeTreeWritable(path);
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void MakeTreeWritable(string path)
    {
        RuntimeOwnedFileSystem.ValidateMutableDirectory(Path.GetDirectoryName(path) ?? path, path);
        var directories = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).ToArray();
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();
        foreach (var directory in directories)
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectory(path, directory);
        }
        foreach (var file in files)
        {
            RuntimeOwnedFileSystem.ValidateMutableRegularFile(path, file);
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in files)
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            return;
        }
        foreach (var directory in directories)
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed record CanonicalProjectionRequest(
        string SourceRootPath,
        string PackageId,
        string SourceContentIdentity,
        PackageProjectionRole Role,
        string RulesIdentity,
        IReadOnlyList<string> TargetKeys,
        IReadOnlyList<PackageProjectionSourceFile> Files,
        IReadOnlyList<PackageProjectionInventoryEntry> Inventory,
        IReadOnlyList<string> WarmValidationPaths,
        string CacheKey);

    private sealed record ExpectedProjection(
        string CacheKey,
        PackageProjectionRole Role,
        string SourceContentIdentity,
        string RulesIdentity,
        IReadOnlyList<string> TargetKeys,
        string? PackageId)
    {
        public static ExpectedProjection From(CanonicalProjectionRequest request, string? packageId)
            => new(
                request.CacheKey,
                request.Role,
                request.SourceContentIdentity,
                request.RulesIdentity,
                request.TargetKeys,
                packageId);
    }

    private sealed record EntryValidation(
        PackageProjectionReceipt? Receipt,
        string RootPath,
        PackageProjectionCacheMetrics Metrics,
        bool Corrupt,
        Exception? Exception)
    {
        public static EntryValidation Missing(string rootPath)
            => new(null, rootPath, PackageProjectionCacheMetrics.Empty, Corrupt: false, Exception: null);
    }
}
