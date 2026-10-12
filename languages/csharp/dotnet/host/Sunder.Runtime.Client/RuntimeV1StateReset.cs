using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Sunder.Package.Projections;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Client;

public sealed record RuntimeV1ResetCategoryResult(
    string Category,
    string Status,
    IReadOnlyList<string>? RelativePaths = null);

public sealed record RuntimeV1ResetResult(
    IReadOnlyList<RuntimeV1ResetCategoryResult> Categories,
    string? ReceiptRelativePath = null)
{
    public bool Success => Categories.All(result => result.Status is "reset" or "already-empty");
}

public static class RuntimeV1StateReset
{
    private const string ReceiptFormat = "sunder.runtime-local-reset-receipt";
    private const int ReceiptSchemaVersion = 1;
    private const int MacOsFullFileSync = 51; // Darwin F_FULLFSYNC.
    private const int MaximumPackageIdLength = 128;
    private const int MaximumSemanticVersionLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static Task<RuntimeV1ResetResult> ResetAsync(
        TimeSpan leaseWait,
        CancellationToken cancellationToken = default)
        => ResetCoreAsync(
            leaseWait,
            cancellationToken,
            RuntimeLocalState.GetV1RootPath(),
            startupCacheOnly: false,
            afterStarted: null,
            expectedRootIdentity: null);

    public static Task<RuntimeV1ResetResult> ResetStartupCacheAsync(
        TimeSpan leaseWait,
        CancellationToken cancellationToken = default)
        => ResetCoreAsync(
            leaseWait,
            cancellationToken,
            RuntimeLocalState.GetV1RootPath(),
            startupCacheOnly: true,
            afterStarted: null,
            expectedRootIdentity: null);

    internal static Task<RuntimeV1ResetResult> ResetAsync(
        TimeSpan leaseWait,
        string rootPath,
        CancellationToken cancellationToken = default,
        Action? afterStarted = null,
        string? expectedRootIdentity = null)
        => ResetCoreAsync(
            leaseWait,
            cancellationToken,
            rootPath,
            startupCacheOnly: false,
            afterStarted,
            expectedRootIdentity);

    internal static Task<RuntimeV1ResetResult> ResetStartupCacheAsync(
        TimeSpan leaseWait,
        string rootPath,
        CancellationToken cancellationToken = default,
        Action? afterStarted = null,
        string? expectedRootIdentity = null)
        => ResetCoreAsync(
            leaseWait,
            cancellationToken,
            rootPath,
            startupCacheOnly: true,
            afterStarted,
            expectedRootIdentity);

    private static async Task<RuntimeV1ResetResult> ResetCoreAsync(
        TimeSpan leaseWait,
        CancellationToken cancellationToken,
        string rootPath,
        bool startupCacheOnly,
        Action? afterStarted,
        string? expectedRootIdentity)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = new RuntimeV1ResetPolicyOptions();
        var timeProvider = TimeProvider.System;
        var root = Path.GetFullPath(rootPath);
        ValidateRootAncestorChain(root);
        if (!Directory.Exists(root))
        {
            if (expectedRootIdentity is not null)
            {
                throw new InvalidDataException(
                    "The Host-bound Runtime V1 state root no longer exists.");
            }
            if (Path.Exists(root))
            {
                throw new InvalidDataException("The Runtime V1 state root must be a directory.");
            }

            CreateRootDirectory(root);
        }

        var rootIdentity = expectedRootIdentity
            ?? RuntimeOwnedFileSystem.GetMutableDirectoryIdentity(root);
        RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);

        _ = Preflight(root, startupCacheOnly);

        var leasePath = ContainedPath(root, RuntimeLocalState.LeaseFileName);
        await using var lease = await AcquireLeaseAsync(
            leasePath,
            leaseWait,
            policy,
            timeProvider,
            cancellationToken).ConfigureAwait(false);
        RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
        _ = Preflight(root, startupCacheOnly);
        RuntimeLocalState.EnsureInitialized(root, deleteStaleSchemaTemporaryFiles: false);
        var projectionCache = new PackageProjectionCache(runtimeStateRootPath: root);
        await using var projectionReset = await projectionCache
            .AcquireOfflineResetScopeAsync(cancellationToken)
            .ConfigureAwait(false);
        var planned = Preflight(root, startupCacheOnly);
        await projectionReset.ValidateAsync(cancellationToken).ConfigureAwait(false);
        var receiptId = Guid.NewGuid().ToString("N");
        var startedAtUtc = timeProvider.GetUtcNow();
        var receiptRelativePath = Path.Combine(
            RuntimeV1StateDescriptor.ResetReceiptDirectory,
            $"{startedAtUtc:yyyyMMddTHHmmssfffffffZ}-{receiptId}.json");
        var receiptPath = ContainedPath(root, receiptRelativePath);
        var results = new List<RuntimeV1ResetCategoryResult>();
        var startedReceiptPublished = false;
        try
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
            WriteReceipt(
                receiptPath,
                new RuntimeV1ResetReceipt(
                    ReceiptFormat,
                    ReceiptSchemaVersion,
                    receiptId,
                    "started",
                    startedAtUtc,
                    null,
                    planned.Select(static category => new RuntimeV1ResetReceiptCategory(
                        category.Descriptor.Id,
                        "planned",
                        category.RelativePaths)).ToArray(),
                    GetPreservedPaths(startupCacheOnly)));
            startedReceiptPublished = true;
            await projectionReset.PrepareForDeletionAsync(CancellationToken.None).ConfigureAwait(false);
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
            planned = Preflight(root, startupCacheOnly);
            afterStarted?.Invoke();
            foreach (var category in planned)
            {
                results.Add(ResetCategory(root, rootIdentity, category));
            }
            var status = results.All(result => result.Status is "reset" or "already-empty")
                ? "completed"
                : "partial";
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
            WriteReceipt(
                receiptPath,
                CreateTerminalReceipt(
                    receiptId,
                    status,
                    startedAtUtc,
                    timeProvider.GetUtcNow(),
                    results,
                    GetPreservedPaths(startupCacheOnly)));
            return new RuntimeV1ResetResult(results, NormalizeRelativePath(receiptRelativePath));
        }
        catch (Exception resetException)
        {
            if (!startedReceiptPublished && !File.Exists(receiptPath))
            {
                throw;
            }
            var attempted = results.ToDictionary(static result => result.Category, StringComparer.Ordinal);
            var partialResults = planned.Select(category =>
                attempted.TryGetValue(category.Descriptor.Id, out var result)
                    ? result
                    : new RuntimeV1ResetCategoryResult(
                        category.Descriptor.Id,
                        "partial",
                        category.RelativePaths)).ToArray();
            try
            {
                RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
                WriteReceipt(
                    receiptPath,
                    CreateTerminalReceipt(
                        receiptId,
                        "partial",
                        startedAtUtc,
                        timeProvider.GetUtcNow(),
                        partialResults,
                        GetPreservedPaths(startupCacheOnly)));
            }
            catch (Exception receiptException)
            {
                throw new AggregateException(
                    "Runtime V1 reset failed and its terminal partial receipt could not be published.",
                    resetException,
                    receiptException);
            }
            throw;
        }
    }

    private static RuntimeV1ResetReceipt CreateTerminalReceipt(
        string receiptId,
        string status,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        IReadOnlyList<RuntimeV1ResetCategoryResult> results,
        IReadOnlyList<string> preservedPaths)
        => new(
            ReceiptFormat,
            ReceiptSchemaVersion,
            receiptId,
            status,
            startedAtUtc,
            completedAtUtc,
            results.Select(static category => new RuntimeV1ResetReceiptCategory(
                category.Category,
                category.Status,
                category.RelativePaths ?? [])).ToArray(),
            preservedPaths);

    private static IReadOnlyList<string> PreservedPaths { get; } =
    [
        RuntimeV1StateDescriptor.PackageDataDirectory,
        RuntimeV1StateDescriptor.CredentialsDirectory,
        RuntimeV1StateDescriptor.RpcPermissionFile,
        RuntimeLocalState.SchemaFileName,
        RuntimeLocalState.LeaseFileName,
        RuntimeV1StateDescriptor.PackageProjectionCacheCoordinationFile,
        RuntimeV1StateDescriptor.ResetReceiptDirectory,
    ];

    private static IReadOnlyList<string> StartupCachePreservedPaths { get; } =
    [
        RuntimeV1StateDescriptor.CatalogDirectory,
        RuntimeV1StateDescriptor.InstalledDirectory,
        RuntimeV1StateDescriptor.StagingDirectory,
        RuntimeV1StateDescriptor.TransactionsDirectory,
        RuntimeV1StateDescriptor.TombstonesDirectory,
        RuntimeV1StateDescriptor.PackageDataDirectory,
        RuntimeV1StateDescriptor.TransfersDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeArtifactsDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeRootsDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeReceiptsDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeStagingDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeTransactionsDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeQuarantineDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeLeasesDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeTombstonesDirectory,
        RuntimeV1StateDescriptor.ExecutionRuntimeManagementDirectory,
        RuntimeV1StateDescriptor.CredentialsDirectory,
        RuntimeV1StateDescriptor.ConnectionFile,
        RuntimeV1StateDescriptor.ConnectionLockFile,
        RuntimeV1StateDescriptor.RpcPermissionFile,
        RuntimeLocalState.SchemaFileName,
        RuntimeLocalState.LeaseFileName,
        RuntimeV1StateDescriptor.PackageProjectionCacheCoordinationFile,
        RuntimeV1StateDescriptor.ResetReceiptDirectory,
    ];

    private static IReadOnlyList<string> GetPreservedPaths(bool startupCacheOnly)
        => startupCacheOnly ? StartupCachePreservedPaths : PreservedPaths;

    private static async Task<FileStream> AcquireLeaseAsync(
        string leasePath,
        TimeSpan wait,
        RuntimeV1ResetPolicyOptions policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var expiresAt = timeProvider.GetUtcNow() + wait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (timeProvider.GetUtcNow() < expiresAt)
            {
                await Task.Delay(policy.LeaseRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (timeProvider.GetUtcNow() < expiresAt)
            {
                await Task.Delay(policy.LeaseRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("The Runtime V1 state is locked. Stop the Runtime and retry the reset.", exception);
            }
        }
    }

    private static PlannedResetCategory[] Preflight(string root, bool startupCacheOnly)
    {
        if (IsReparsePoint(root))
        {
            throw new InvalidDataException("The Runtime V1 state root cannot be reset through a symbolic link or reparse point.");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);
            if (IsReparsePoint(entry))
            {
                throw UnsafePath(root, entry, "is a symbolic link or reparse point");
            }
            if (name is null || !ContainsKnownRootEntry(name) && !IsSchemaTemporaryFile(name))
            {
                throw new InvalidDataException(
                    $"The Runtime V1 state root contains an unclassified entry and was not changed: {name ?? entry}.");
            }

            ValidateRootEntryType(root, entry, name);
        }

        RuntimeLocalState.Validate(root);
        IReadOnlyList<RuntimeV1StateCategoryDescriptor> categories = startupCacheOnly
            ? [RuntimeV1StateDescriptor.StartupCacheResetCategory]
            : RuntimeV1StateDescriptor.ResetCategories;
        return categories
            .Select(category => PlanCategory(root, category))
            .ToArray();
    }

    private static void ValidateRootEntryType(string root, string path, string name)
    {
        var shouldBeDirectory = name is RuntimeV1StateDescriptor.CatalogDirectory
            or RuntimeV1StateDescriptor.InstalledDirectory
            or RuntimeV1StateDescriptor.StagingDirectory
            or RuntimeV1StateDescriptor.TransactionsDirectory
            or RuntimeV1StateDescriptor.TombstonesDirectory
            or RuntimeV1StateDescriptor.PackageDataDirectory
            or RuntimeV1StateDescriptor.TransfersDirectory
            or RuntimeV1StateDescriptor.CacheDirectory
            or RuntimeV1StateDescriptor.ExecutionRuntimesDirectory
            or RuntimeV1StateDescriptor.CredentialsDirectory
            or RuntimeV1StateDescriptor.ResetReceiptDirectory;
        if (shouldBeDirectory)
        {
            ValidateDirectory(root, path);
            if (name == RuntimeV1StateDescriptor.ResetReceiptDirectory)
            {
                RuntimeOwnedFileSystem.ValidateMutableDirectory(root, path);
            }
        }
        else
        {
            ValidateRegularFile(root, path);
            if (name == RuntimeLocalState.LeaseFileName)
            {
                RuntimeOwnedFileSystem.ValidateMutableRegularFile(root, path);
            }
        }
    }

    private static PlannedResetCategory PlanCategory(
        string root,
        RuntimeV1StateCategoryDescriptor descriptor)
    {
        var relativePaths = new List<string>();
        switch (descriptor.Id)
        {
            case "installed-catalog-and-payloads":
                PlanCatalog(root, relativePaths);
                PlanInstalled(root, relativePaths);
                break;
            case "package-transactions-and-derived-state":
                PlanSchemaTemporaryFiles(root, relativePaths);
                PlanGuidDirectories(root, RuntimeV1StateDescriptor.StagingDirectory, relativePaths);
                PlanTransactions(root, relativePaths);
                PlanTombstones(root, relativePaths);
                break;
            case "execution-runtime-derived-state":
                PlanExecutionRuntimes(root, descriptor, relativePaths);
                break;
            case "uploads-and-snapshots":
                PlanTransfers(root, relativePaths);
                PlanCache(root, relativePaths);
                break;
            case "startup-cache":
                PlanCache(root, relativePaths);
                PlanStartupRuntimeHealth(root, relativePaths);
                break;
            case "runtime-connection":
                PlanFixedFiles(root, descriptor.RelativePaths, relativePaths);
                break;
            default:
                throw new InvalidOperationException($"Runtime reset category '{descriptor.Id}' has no reviewed deletion grammar.");
        }

        return new PlannedResetCategory(
            descriptor,
            relativePaths.Select(relativePath => CapturePlannedPath(root, relativePath)).ToArray());
    }

    private static PlannedResetPath CapturePlannedPath(string root, string relativePath)
    {
        var path = ContainedPath(root, relativePath);
        var identities = new Dictionary<string, string>(PathComparer);
        if (Directory.Exists(path))
        {
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var directory))
            {
                identities[RelativePath(root, directory)] =
                    RuntimeOwnedFileSystem.GetMutableDirectoryIdentity(root, directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    ValidateNotReparsePoint(root, entry);
                    if (Directory.Exists(entry))
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        identities[RelativePath(root, entry)] =
                            RuntimeOwnedFileSystem.GetMutableRegularFileIdentity(root, entry);
                    }
                }
            }
        }
        else
        {
            identities[NormalizeRelativePath(relativePath)] =
                RuntimeOwnedFileSystem.GetMutableRegularFileIdentity(root, path);
        }
        return new PlannedResetPath(NormalizeRelativePath(relativePath), identities);
    }

    private static void PlanExecutionRuntimes(
        string root,
        RuntimeV1StateCategoryDescriptor descriptor,
        ICollection<string> relativePaths)
    {
        var storeRoot = ContainedPath(root, RuntimeV1StateDescriptor.ExecutionRuntimesDirectory);
        if (!Directory.Exists(storeRoot))
        {
            return;
        }

        var classifiedPaths = descriptor.RelativePaths
            .Select(relativePath => ContainedPath(root, relativePath))
            .ToArray();
        foreach (var entry in Directory.EnumerateFileSystemEntries(storeRoot))
        {
            ValidateDirectory(root, entry);
            if (!classifiedPaths.Any(path => string.Equals(path, entry, PathComparison)))
            {
                throw UnsafePath(root, entry, "is not classified by the execution-runtime reset grammar");
            }
        }

        foreach (var relativePath in descriptor.RelativePaths)
        {
            var path = ContainedPath(root, relativePath);
            if (!Path.Exists(path))
            {
                continue;
            }

            ValidateDeletionTree(root, path);
            relativePaths.Add(NormalizeRelativePath(relativePath));
        }
    }

    private static void PlanCatalog(string root, ICollection<string> relativePaths)
    {
        var catalog = ContainedPath(root, RuntimeV1StateDescriptor.CatalogDirectory);
        if (!Directory.Exists(catalog))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(catalog))
        {
            ValidateNotReparsePoint(root, entry);
            var name = Path.GetFileName(entry);
            if (name == Path.GetFileName(RuntimeV1StateDescriptor.AppPolicySessionsDirectory))
            {
                // Preserve loaded-process evidence across a data-safe reset.
                ValidateDirectory(root, entry);
                continue;
            }
            if (name == Path.GetFileName(RuntimeV1StateDescriptor.PackagePolicyDirectory))
            {
                ValidateDirectory(root, entry);
                foreach (var policyFile in Directory.EnumerateFileSystemEntries(entry))
                {
                    ValidateDeletionRegularFile(root, policyFile);
                    var policyName = Path.GetFileName(policyFile);
                    if (policyName.Length < 64 || !policyName[..64].All(static character => character is >= 'a' and <= 'f' or >= '0' and <= '9')
                        || policyName[64..] is not (".json" or ".json.lock" or ".intent.json")
                        && !IsDurableJsonTemporaryFile(policyName, policyName[..64] + ".json")
                        && !IsDurableJsonTemporaryFile(policyName, policyName[..64] + ".intent.json"))
                        throw UnsafePath(root, policyFile, "is not classified by the package policy reset grammar");
                }
                relativePaths.Add(RelativePath(root, entry));
                continue;
            }
            ValidateRegularFile(root, entry);
            if (name == "rpc-permissions.json")
            {
                continue;
            }
            if (name == "installed-packages.json"
                || IsDurableJsonTemporaryFile(name, "installed-packages.json"))
            {
                ValidateDeletionRegularFile(root, entry);
                relativePaths.Add(RelativePath(root, entry));
                continue;
            }

            throw UnsafePath(root, entry, "is not classified by the catalog reset grammar");
        }
    }

    private static void PlanInstalled(string root, ICollection<string> relativePaths)
    {
        var installedRoot = ContainedPath(root, RuntimeV1StateDescriptor.InstalledDirectory);
        if (!Directory.Exists(installedRoot))
        {
            return;
        }

        foreach (var packageRoot in Directory.EnumerateFileSystemEntries(installedRoot))
        {
            ValidateDirectory(root, packageRoot);
            var packageId = Path.GetFileName(packageRoot);
            if (!IsPackageId(packageId))
            {
                throw UnsafePath(root, packageRoot, "is not a canonical package id");
            }

            foreach (var versionRoot in Directory.EnumerateFileSystemEntries(packageRoot))
            {
                ValidateDirectory(root, versionRoot);
                if (!IsSemanticVersion(Path.GetFileName(versionRoot)))
                {
                    throw UnsafePath(root, versionRoot, "is not a canonical package version");
                }

                ValidateDeletionTree(root, versionRoot);
                relativePaths.Add(RelativePath(root, versionRoot));
            }
        }
    }

    private static void PlanGuidDirectories(string root, string relativeRoot, ICollection<string> relativePaths)
    {
        var directory = ContainedPath(root, relativeRoot);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            ValidateDirectory(root, entry);
            if (!IsGuid(Path.GetFileName(entry)))
            {
                throw UnsafePath(root, entry, "is not a canonical Runtime stage id");
            }

            ValidateDeletionTree(root, entry);
            relativePaths.Add(RelativePath(root, entry));
        }
    }

    private static void PlanTransactions(string root, ICollection<string> relativePaths)
    {
        var transactionsRoot = ContainedPath(root, RuntimeV1StateDescriptor.TransactionsDirectory);
        if (!Directory.Exists(transactionsRoot))
        {
            return;
        }

        foreach (var transactionRoot in Directory.EnumerateFileSystemEntries(transactionsRoot))
        {
            ValidateDirectory(root, transactionRoot);
            if (!IsGuid(Path.GetFileName(transactionRoot)))
            {
                throw UnsafePath(root, transactionRoot, "is not a canonical transaction id");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(transactionRoot))
            {
                ValidateNotReparsePoint(root, entry);
                var name = Path.GetFileName(entry);
                if (IsDurableJsonTemporaryFile(name, "journal.json"))
                {
                    ValidateRegularFile(root, entry);
                    continue;
                }

                switch (name)
                {
                    case "journal.json":
                        ValidateRegularFile(root, entry);
                        break;
                    case "backups":
                        ValidateDirectory(root, entry);
                        ValidateDeletionTree(root, entry);
                        break;
                    default:
                        throw UnsafePath(root, entry, "is not classified by the transaction reset grammar");
                }
            }
            ValidateDeletionTree(root, transactionRoot);
            relativePaths.Add(RelativePath(root, transactionRoot));
        }
    }

    private static void PlanSchemaTemporaryFiles(string root, ICollection<string> relativePaths)
    {
        foreach (var path in Directory.EnumerateFiles(root))
        {
            if (!IsSchemaTemporaryFile(Path.GetFileName(path))) continue;
            ValidateDeletionRegularFile(root, path);
            relativePaths.Add(RelativePath(root, path));
        }
    }

    private static void PlanTombstones(string root, ICollection<string> relativePaths)
    {
        var tombstonesRoot = ContainedPath(root, RuntimeV1StateDescriptor.TombstonesDirectory);
        if (!Directory.Exists(tombstonesRoot))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(tombstonesRoot))
        {
            ValidateDirectory(root, entry);
            if (!IsTombstoneName(Path.GetFileName(entry)))
            {
                throw UnsafePath(root, entry, "is not a canonical tombstone id");
            }

            ValidateDeletionTree(root, entry);
            relativePaths.Add(RelativePath(root, entry));
        }
    }

    private static void PlanTransfers(string root, ICollection<string> relativePaths)
    {
        var transfersRoot = ContainedPath(root, RuntimeV1StateDescriptor.TransfersDirectory);
        if (!Directory.Exists(transfersRoot))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(transfersRoot))
        {
            ValidateDeletionRegularFile(root, entry);
            if (!IsTransferFile(Path.GetFileName(entry)))
            {
                throw UnsafePath(root, entry, "is not a recognized Runtime transfer file");
            }

            relativePaths.Add(RelativePath(root, entry));
        }
    }

    private static void PlanCache(string root, ICollection<string> relativePaths)
    {
        var cacheRoot = ContainedPath(root, RuntimeV1StateDescriptor.CacheDirectory);
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }
        RuntimeOwnedFileSystem.ValidateMutableDirectory(root, cacheRoot);

        var snapshotsRoot = Path.Combine(cacheRoot, "package-ui-snapshots");
        var projectionsRoot = Path.Combine(cacheRoot, "package-projections");
        foreach (var entry in Directory.EnumerateFileSystemEntries(cacheRoot))
        {
            ValidateDirectory(root, entry);
            if (!string.Equals(entry, snapshotsRoot, PathComparison)
                && !string.Equals(entry, projectionsRoot, PathComparison))
            {
                throw UnsafePath(root, entry, "is not classified by the cache reset grammar");
            }
        }

        PlanCacheRevision(root, snapshotsRoot, "r2", "package UI snapshot", relativePaths);
        PlanCacheRevision(
            root,
            projectionsRoot,
            "r1",
            "package projection",
            relativePaths,
            Path.GetFileName(RuntimeV1StateDescriptor.PackageProjectionCacheCoordinationFile));
    }

    private static void PlanStartupRuntimeHealth(string root, ICollection<string> relativePaths)
    {
        var path = ContainedPath(root, RuntimeV1StateDescriptor.ExecutionRuntimeHealthDirectory);
        if (!Directory.Exists(path))
        {
            return;
        }
        ValidateDeletionTree(root, path);
        relativePaths.Add(NormalizeRelativePath(RuntimeV1StateDescriptor.ExecutionRuntimeHealthDirectory));
    }

    private static void PlanCacheRevision(
        string root,
        string revisionRoot,
        string expectedRevision,
        string description,
        ICollection<string> relativePaths,
        string? preservedFileName = null)
    {
        if (!Directory.Exists(revisionRoot))
        {
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(revisionRoot))
        {
            if (preservedFileName is not null
                && string.Equals(Path.GetFileName(entry), preservedFileName, StringComparison.Ordinal))
            {
                ValidateRegularFile(root, entry);
                RuntimeOwnedFileSystem.ValidateMutableRegularFile(root, entry);
                continue;
            }
            ValidateDirectory(root, entry);
            if (!string.Equals(Path.GetFileName(entry), expectedRevision, StringComparison.Ordinal))
            {
                throw UnsafePath(root, entry, $"is not a known {description} revision");
            }

            ValidateDeletionTree(root, entry);
            relativePaths.Add(RelativePath(root, entry));
        }
    }

    private static void PlanFixedFiles(
        string root,
        IEnumerable<string> candidates,
        ICollection<string> relativePaths)
    {
        foreach (var relativePath in candidates)
        {
            AddExistingPath(root, relativePath, relativePaths);
        }
    }

    private static void AddExistingPath(string root, string relativePath, ICollection<string> relativePaths)
    {
        var path = ContainedPath(root, relativePath);
        if (!Path.Exists(path))
        {
            return;
        }

        ValidateDeletionRegularFile(root, path);
        relativePaths.Add(NormalizeRelativePath(relativePath));
    }

    private static void ValidateDeletionTree(string root, string path)
    {
        ValidateDirectory(root, path);
        RuntimeOwnedFileSystem.ValidateMutableDirectory(root, path);
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ValidateNotReparsePoint(root, entry);
                if (Directory.Exists(entry))
                {
                    RuntimeOwnedFileSystem.ValidateMutableDirectory(root, entry);
                    pending.Push(entry);
                }
                else
                {
                    ValidateDeletionRegularFile(root, entry);
                }
            }
        }
    }

    private static void ValidateDirectory(string root, string path)
    {
        ValidateNotReparsePoint(root, path);
        if (!Directory.Exists(path))
        {
            throw UnsafePath(root, path, "must be a directory");
        }
    }

    private static void ValidateRegularFile(string root, string path)
    {
        ValidateNotReparsePoint(root, path);
        if (Directory.Exists(path) || !File.Exists(path))
        {
            throw UnsafePath(root, path, "must be a regular file");
        }
    }

    private static void ValidateDeletionRegularFile(string root, string path)
    {
        ValidateRegularFile(root, path);
        RuntimeOwnedFileSystem.ValidateMutableRegularFile(root, path);
    }

    private static void ValidateNotReparsePoint(string root, string path)
    {
        if (IsReparsePoint(path))
        {
            throw UnsafePath(root, path, "is a symbolic link or reparse point");
        }
    }

    private static InvalidDataException UnsafePath(string root, string path, string reason)
        => new($"The Runtime V1 reset path '{RelativePath(root, path)}' {reason}; no state was changed.");

    private static RuntimeV1ResetCategoryResult ResetCategory(
        string root,
        string rootIdentity,
        PlannedResetCategory category)
    {
        var failed = false;
        foreach (var plannedPath in category.Paths)
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
            var path = ContainedPath(root, plannedPath.RelativePath);
            try
            {
                if (Directory.Exists(path))
                {
                    DeleteDeletionTree(root, rootIdentity, path, plannedPath.EntryIdentities);
                }
                else if (File.Exists(path))
                {
                    var expectedIdentity = GetExpectedEntryIdentity(
                        root,
                        path,
                        plannedPath.EntryIdentities);
                    RuntimeOwnedFileSystem.ValidateMutableRegularFileIdentity(
                        root,
                        path,
                        expectedIdentity);
                    MakeDeletionFileWritable(root, path);
                    RuntimeOwnedFileSystem.ValidateMutableRegularFileIdentity(
                        root,
                        path,
                        expectedIdentity);
                    DeleteFileDurably(path);
                }
                else if (IsReparsePoint(path))
                {
                    throw UnsafePath(root, path, "was replaced by a symbolic link or reparse point");
                }
                else
                {
                    throw UnsafePath(root, path, "changed after reset planning");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed = true;
            }
        }

        return new RuntimeV1ResetCategoryResult(
            category.Descriptor.Id,
            failed ? "partial" : category.RelativePaths.Count == 0 ? "already-empty" : "reset",
            category.RelativePaths);
    }

    private static void DeleteDeletionTree(
        string root,
        string rootIdentity,
        string path,
        IReadOnlyDictionary<string, string> expectedIdentities)
    {
        var directories = new List<(string Path, string Identity)>();
        var files = new List<(string Path, string Identity)>();
        var observed = new HashSet<string>(PathComparer);
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
            ValidateDirectory(root, directory);
            var directoryKey = RelativePath(root, directory);
            var directoryIdentity = GetExpectedEntryIdentity(root, directory, expectedIdentities);
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(
                root,
                directory,
                directoryIdentity);
            observed.Add(directoryKey);
            MakeDeletionDirectoryWritable(directory);
            directories.Add((directory, directoryIdentity));
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).ToArray())
            {
                ValidateNotReparsePoint(root, entry);
                var entryKey = RelativePath(root, entry);
                var entryIdentity = GetExpectedEntryIdentity(root, entry, expectedIdentities);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    RuntimeOwnedFileSystem.ValidateMutableRegularFileIdentity(
                        root,
                        entry,
                        entryIdentity);
                    observed.Add(entryKey);
                    files.Add((entry, entryIdentity));
                }
            }
        }
        if (observed.Count != expectedIdentities.Count)
        {
            throw UnsafePath(root, path, "changed after reset planning");
        }

        foreach (var file in files)
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
            RuntimeOwnedFileSystem.ValidateMutableRegularFileIdentity(
                root,
                file.Path,
                file.Identity);
            MakeDeletionFileWritable(root, file.Path);
            RuntimeOwnedFileSystem.ValidateMutableRegularFileIdentity(
                root,
                file.Path,
                file.Identity);
            DeleteFileDurably(file.Path);
        }
        foreach (var directory in directories.OrderByDescending(static value => value.Path.Length))
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(root, rootIdentity);
            ValidateDirectory(root, directory.Path);
            RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(
                root,
                directory.Path,
                directory.Identity);
            DeleteDirectoryDurably(directory.Path);
        }
    }

    private static string GetExpectedEntryIdentity(
        string root,
        string path,
        IReadOnlyDictionary<string, string> expectedIdentities)
    {
        var relativePath = RelativePath(root, path);
        if (expectedIdentities.TryGetValue(relativePath, out var identity))
        {
            return identity;
        }
        throw UnsafePath(root, path, "was added or moved after reset planning");
    }

    private static void DeleteFileDurably(string path)
    {
        File.Delete(path);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    private static void DeleteDirectoryDurably(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        Directory.Delete(path, recursive: false);
        SyncDirectory(parent);
    }

    private static void MakeDeletionFileWritable(string root, string path)
    {
        ValidateDeletionRegularFile(root, path);
        if (OperatingSystem.IsWindows())
        {
            SetWindowsDeletionAccess(path, directory: false);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            return;
        }
    }

    private static void MakeDeletionDirectoryWritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsDeletionAccess(path, directory: true);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsDeletionAccess(string path, bool directory)
    {
        var identity = WindowsIdentity.GetCurrent().User
                       ?? throw new IOException("The current Windows user has no security identifier.");
        FileSystemSecurity current = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        if (!identity.Equals(current.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new InvalidDataException(
                $"Runtime-owned path '{path}' is not owned by the current Windows user.");
        }
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (directory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }

    private static void WriteReceipt(string path, RuntimeV1ResetReceipt receipt)
    {
        var directory = Path.GetDirectoryName(path)!;
        var root = Path.GetDirectoryName(directory)
                   ?? throw new InvalidDataException("The Runtime V1 reset receipt path has no state root.");
        var directoryExisted = Directory.Exists(directory);
        RuntimeOwnedFileSystem.ValidateMutableDirectory(root, root);
        if (directoryExisted)
        {
            RuntimeOwnedFileSystem.ValidateMutableDirectory(root, directory);
        }
        Directory.CreateDirectory(directory);
        RuntimeOwnedFileSystem.ValidateMutableDirectory(root, directory);
        if (IsReparsePoint(directory))
        {
            throw new InvalidDataException("The Runtime V1 reset receipt directory cannot be a symbolic link or reparse point.");
        }
        if (!directoryExisted)
        {
            SyncDirectory(Path.GetDirectoryName(directory)!);
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                SyncReceiptFile(stream, temporaryPath);
            }
            PublishReceipt(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PublishReceipt(string temporaryPath, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows has no fsync-style directory operation. MOVEFILE_WRITE_THROUGH makes the atomic
            // replacement wait for the filesystem to publish the move to disk before returning.
            if (!NativeMethods.MoveFileEx(
                    temporaryPath,
                    path,
                    MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough))
            {
                throw new IOException(
                    $"Could not durably publish Runtime reset receipt '{path}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            return;
        }

        File.Move(temporaryPath, path, overwrite: true);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    private static void SyncReceiptFile(FileStream stream, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
        var result = OperatingSystem.IsMacOS()
            ? NativeMethods.Fcntl(descriptor, MacOsFullFileSync)
            : NativeMethods.Fsync(descriptor);
        if (result != 0)
        {
            throw new IOException(
                $"Could not synchronize Runtime reset receipt temporary file '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static void SyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var descriptor = NativeMethods.Open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open Runtime reset directory '{path}' for durability synchronization.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            if (NativeMethods.Fsync(descriptor) != 0)
            {
                throw new IOException(
                    $"Could not synchronize Runtime reset directory '{path}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            NativeMethods.Close(descriptor);
        }
    }

    private static void ValidateRootAncestorChain(string root)
        => RuntimeOwnedFileSystem.ValidateNoReparsePointAncestors(root);

    private static void CreateRootDirectory(string root)
    {
        var missingDirectories = new List<string>();
        for (var current = root; !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
        {
            if (Path.Exists(current) || IsReparsePoint(current))
            {
                throw new InvalidDataException($"Runtime V1 state path '{current}' must be a directory and not a reparse point.");
            }

            missingDirectories.Add(current);
            if (Path.GetDirectoryName(current) is null)
            {
                throw new DirectoryNotFoundException($"Runtime V1 state root '{root}' has no existing ancestor.");
            }
        }

        Directory.CreateDirectory(root);
        foreach (var directory in missingDirectories)
        {
            SyncDirectory(Path.GetDirectoryName(directory)!);
        }
    }

    private static string ContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Reset paths must be fixed relative V1 paths.");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison))
        {
            throw new InvalidOperationException("A Runtime reset path escaped the V1 root.");
        }

        return path;
    }

    private static bool ContainsKnownRootEntry(string entry)
        => RuntimeV1StateDescriptor.KnownRootEntries.Contains(entry);

    private static bool IsSchemaTemporaryFile(string name)
        => name.StartsWith($".{RuntimeLocalState.SchemaFileName}.", StringComparison.Ordinal)
           && name.EndsWith(".tmp", StringComparison.Ordinal);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null
                   || new DirectoryInfo(path).LinkTarget is not null
                   || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsGuid(string value)
        => value.Length == 32
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
           && Guid.TryParseExact(value, "N", out _);

    private static bool IsDurableJsonTemporaryFile(string value, string documentFileName)
    {
        var prefix = documentFileName + ".tmp-";
        return value.StartsWith(prefix, StringComparison.Ordinal) && IsGuid(value[prefix.Length..]);
    }

    private static bool IsPackageId(string value)
        => value.Length is > 0 and <= MaximumPackageIdLength
           && value.Split('.').All(static segment =>
               segment.Length > 0
               && segment.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9'));

    private static bool IsSemanticVersion(string value)
    {
        if (value.Length is 0 or > MaximumSemanticVersionLength || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var buildSeparator = value.IndexOf('+');
        if (buildSeparator >= 0 && value.IndexOf('+', buildSeparator + 1) >= 0)
        {
            return false;
        }

        var build = buildSeparator < 0 ? null : value[(buildSeparator + 1)..];
        var precedence = buildSeparator < 0 ? value : value[..buildSeparator];
        if (build is not null && !AreSemanticIdentifiersValid(build, numericLeadingZerosAllowed: true))
        {
            return false;
        }

        var prereleaseSeparator = precedence.IndexOf('-');
        var prerelease = prereleaseSeparator < 0 ? null : precedence[(prereleaseSeparator + 1)..];
        var core = prereleaseSeparator < 0 ? precedence : precedence[..prereleaseSeparator];
        return (prerelease is null || AreSemanticIdentifiersValid(prerelease, numericLeadingZerosAllowed: false))
               && core.Split('.') is { Length: 3 } components
               && components.All(static component =>
                   component.Length > 0
                   && (component.Length == 1 || component[0] != '0')
                   && component.All(char.IsAsciiDigit));
    }

    private static bool AreSemanticIdentifiersValid(string value, bool numericLeadingZerosAllowed)
        => value.Split('.').All(identifier =>
            identifier.Length > 0
            && identifier.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (numericLeadingZerosAllowed
                || identifier.Length == 1
                || identifier[0] != '0'
                || identifier.Any(static character => !char.IsAsciiDigit(character))));

    private static bool IsTombstoneName(string value)
    {
        const string orphanPrefix = "orphan-";
        if (value.StartsWith(orphanPrefix, StringComparison.Ordinal))
        {
            return IsGuid(value[orphanPrefix.Length..]);
        }

        return value.Length >= 37
               && value[32] == '-'
               && IsGuid(value[..32])
               && value[33..].Length >= 4
               && value[33..].All(char.IsAsciiDigit);
    }

    private static bool IsTransferFile(string value)
    {
        var extensions = new[] { ".partial", ".sunderpkg", ".sunderstack", ".media", ".download" };
        if (extensions.Any(extension =>
                value.EndsWith(extension, StringComparison.Ordinal)
                && IsGuid(value[..^extension.Length])))
        {
            return true;
        }

        const string rpcPrefix = "rpc-content-";
        return value.StartsWith(rpcPrefix, StringComparison.Ordinal)
               && new[] { ".partial", ".rpccontent" }.Any(extension =>
                   value.EndsWith(extension, StringComparison.Ordinal)
                   && IsGuid(value[rpcPrefix.Length..^extension.Length]));
    }

    private static string RelativePath(string root, string path)
        => NormalizeRelativePath(Path.GetRelativePath(root, path));

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record PlannedResetCategory(
        RuntimeV1StateCategoryDescriptor Descriptor,
        IReadOnlyList<PlannedResetPath> Paths)
    {
        public IReadOnlyList<string> RelativePaths { get; } =
            Array.AsReadOnly(Paths.Select(static path => path.RelativePath).ToArray());
    }

    private sealed record PlannedResetPath(
        string RelativePath,
        IReadOnlyDictionary<string, string> EntryIdentities);

    private sealed record RuntimeV1ResetReceipt(
        string Format,
        int SchemaVersion,
        string ReceiptId,
        string Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        IReadOnlyList<RuntimeV1ResetReceiptCategory> Categories,
        IReadOnlyList<string> PreservedPaths);

    private sealed record RuntimeV1ResetReceiptCategory(
        string Category,
        string Status,
        IReadOnlyList<string> RelativePaths);

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x1,
        WriteThrough = 0x8,
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileEx(string existingPath, string? newPath, MoveFileFlags flags);

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int Fsync(int descriptor);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        internal static extern int Fcntl(int descriptor, int command);

        [DllImport("libc", EntryPoint = "close")]
        internal static extern int Close(int descriptor);
    }
}
