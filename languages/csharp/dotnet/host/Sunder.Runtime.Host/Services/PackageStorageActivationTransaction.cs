using System.Security.Cryptography;
using Sunder.Runtime.Host.Infrastructure.Storage;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStorageActivationTransaction
{
    private readonly IReadOnlyList<PackageSnapshot> _packages;
    private bool _completed;
    private bool _plansValidated;

    private PackageStorageActivationTransaction(IReadOnlyList<PackageSnapshot> packages)
    {
        _packages = packages;
    }

    public static async Task<PackageStorageActivationTransaction> BeginAsync(
        IReadOnlyList<ActiveLoadedPackage> packages,
        CancellationToken cancellationToken,
        PackageStorageActivationFileSystem? fileSystem = null)
    {
        fileSystem ??= new PackageStorageActivationFileSystem();
        var snapshots = new List<PackageSnapshot>(packages.Count);
        try
        {
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (package.StateStore is not JsonPackageKeyValueStore state)
                {
                    throw new InvalidOperationException(
                        $"Package '{package.Descriptor.PackageId}' does not use Host-authoritative state storage.");
                }
                if (package.Settings is not PackageSettings settings)
                {
                    throw new InvalidOperationException(
                        $"Package '{package.Descriptor.PackageId}' does not use Host-authoritative settings storage.");
                }
                if (package.FileStore is not LocalPackageFileStore files)
                {
                    throw new InvalidOperationException(
                        $"Package '{package.Descriptor.PackageId}' does not use Host-authoritative file storage.");
                }
                var packageRoot = Path.GetDirectoryName(files.RootPath)!;
                var workspacePath = package.Worker.WorkspaceRootPath;
                if (!PathEquals(files.RootPath, Path.Combine(packageRoot, "files"))
                    || !string.IsNullOrWhiteSpace(workspacePath)
                    && !PathEquals(workspacePath, Path.Combine(packageRoot, "workspace")))
                {
                    throw new InvalidOperationException(
                        $"Package '{package.Descriptor.PackageId}' storage does not use canonical workspace/files paths.");
                }
                AtomicFileSnapshot? secretsSnapshot = null;
                try
                {
                    var stateSnapshot = await state.CaptureMigrationSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var settingsSnapshot = await settings.CaptureMigrationSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    secretsSnapshot = await package.SecretsStore.CaptureMigrationSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var journal = GetJournal(package);
                    var journalSnapshot = journal is null
                        ? null
                        : await journal.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    var activationJournal = await PackageStorageActivationJournal.CreateAsync(
                        package.Descriptor.PackageId,
                        packageRoot,
                        !string.IsNullOrWhiteSpace(workspacePath),
                        fileSystem,
                        cancellationToken).ConfigureAwait(false);
                    var workspaceSnapshot = string.IsNullOrWhiteSpace(workspacePath)
                        ? null
                        : new WorkspaceSnapshot(workspacePath, activationJournal.GetBackupPath("workspace"));
                    var fileStoreSnapshot = new WorkspaceSnapshot(files.RootPath, activationJournal.GetBackupPath("files"));
                    var packageSnapshot = new PackageSnapshot(
                        package,
                        state,
                        stateSnapshot,
                        settings,
                        settingsSnapshot,
                        secretsSnapshot,
                        journalSnapshot,
                        workspaceSnapshot,
                        fileStoreSnapshot,
                        activationJournal);
                    snapshots.Add(packageSnapshot);
                    secretsSnapshot = null;
                    if (workspaceSnapshot is not null)
                    {
                        CaptureDirectorySnapshot(workspaceSnapshot);
                    }
                    CaptureDirectorySnapshot(fileStoreSnapshot);
                }
                finally
                {
                    if (secretsSnapshot?.Contents is { } secrets)
                    {
                        CryptographicOperations.ZeroMemory(secrets);
                    }
                }
            }
            return new PackageStorageActivationTransaction(snapshots);
        }
        catch (Exception snapshotFailure)
        {
            try
            {
                await new PackageStorageActivationTransaction(snapshots).RollbackAsync()
                    .ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                ZeroSnapshots(snapshots);
                throw new PackageStorageRollbackException(snapshotFailure, rollbackFailure);
            }
            throw;
        }
    }

    public async Task ValidateMigrationPlansAsync(CancellationToken cancellationToken)
    {
        if (_plansValidated)
        {
            return;
        }
        var plans = _packages
            .SelectMany(package => package.Package.Worker.StorageMigrationPlans.Select(
                plan => (Package: package.Package, Plan: plan)))
            .ToArray();
        foreach (var group in plans.GroupBy(
                     static item => item.Package.Descriptor.PackageId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var package = group.First().Package;
            var journal = GetJournal(package)
                          ?? throw new InvalidOperationException(
                              $"Package '{package.Descriptor.PackageId}' declared storage migrations without a Host journal.");
            var packagePlans = group.Select(static item => item.Plan).ToArray();
            if (packagePlans.Select(static plan => plan.MigrationId).Distinct(StringComparer.Ordinal).Count()
                    != packagePlans.Length
                || packagePlans.Select(static plan => plan.Store).Distinct(StringComparer.Ordinal).Count()
                    != packagePlans.Length)
            {
                throw new InvalidOperationException(
                    $"Package '{package.Descriptor.PackageId}' declared duplicate storage migration ids or stores.");
            }
            await journal.ValidateAsync(packagePlans, cancellationToken).ConfigureAwait(false);
        }
        _plansValidated = true;
    }

    public async Task WriteCompletionsAsync(CancellationToken cancellationToken)
    {
        foreach (var package in _packages)
        {
            var plans = package.Package.Worker.StorageMigrationPlans;
            if (plans.Count > 0)
            {
                await GetJournal(package.Package)!.CommitAsync(plans, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task CompleteAsync(ILogger logger)
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        try
        {
            // Publication already committed. Never roll it back on a marker/cleanup
            // failure, and never delete backups before their outcome is durable.
            foreach (var package in _packages)
            {
                await package.ActivationJournal.RecordOutcomeAsync(PackageStorageActivationOutcome.Committed)
                    .ConfigureAwait(false);
            }
            foreach (var package in _packages)
            {
                package.ActivationJournal.TryCleanup(logger);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception,
                "Committed package storage activation outcome could not be persisted; backups were preserved and explicit recovery may be required at startup");
        }
        finally
        {
            ZeroSnapshots(_packages);
        }
    }

    public async Task RollbackAsync(ILogger? logger = null)
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        List<Exception>? failures = null;
        foreach (var package in _packages.Reverse())
        {
            if (package.FileStoreSnapshot is not null)
            {
                try
                {
                    RestoreWorkspaceSnapshot(package.FileStoreSnapshot);
                }
                catch (Exception exception)
                {
                    AddFailure("files", exception);
                }
            }
            if (package.WorkspaceSnapshot is not null)
            {
                try
                {
                    RestoreWorkspaceSnapshot(package.WorkspaceSnapshot);
                }
                catch (Exception exception)
                {
                    AddFailure("workspace", exception);
                }
            }
            if (package.JournalSnapshot is not null)
            {
                try
                {
                    await GetJournal(package.Package)!.RestoreSnapshotAsync(
                        package.JournalSnapshot,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    AddFailure("migration journal", exception);
                }
            }
            try
            {
                await package.Package.SecretsStore.RestoreMigrationSnapshotAsync(
                    package.SecretsSnapshot,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddFailure("secrets", exception);
            }
            try
            {
                await package.StateStore.RestoreMigrationSnapshotAsync(
                    package.StateSnapshot,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddFailure("state", exception);
            }
            try
            {
                await package.Settings.RestoreMigrationSnapshotAsync(
                    package.SettingsSnapshot,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddFailure("settings", exception);
            }

            void AddFailure(string store, Exception exception)
                => (failures ??= []).Add(new IOException(
                    $"Failed to roll back package {store} for '{package.Package.Descriptor.PackageId}'.",
                    exception));
        }
        ZeroSnapshots(_packages);
        if (failures is not null)
        {
            throw new AggregateException("Package storage migration rollback failed.", failures);
        }
        foreach (var package in _packages)
        {
            await package.ActivationJournal.RecordOutcomeAsync(PackageStorageActivationOutcome.RolledBack)
                .ConfigureAwait(false);
            package.ActivationJournal.TryCleanup(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }
    }

    private static void ZeroSnapshots(IEnumerable<PackageSnapshot> packages)
    {
        foreach (var package in packages)
        {
            if (package.SecretsSnapshot.Contents is { } secrets)
            {
                CryptographicOperations.ZeroMemory(secrets);
            }
        }
    }

    private static PackageStorageMigrationJournal? GetJournal(ActiveLoadedPackage package)
        => package.MigrationJournal ?? package.Worker.StorageMigrationJournal;

    private static void CaptureDirectorySnapshot(WorkspaceSnapshot snapshot)
    {
        var workspacePath = Path.GetFullPath(snapshot.WorkspacePath);
        var workspace = new DirectoryInfo(workspacePath);
        if (workspace.LinkTarget is not null)
        {
            throw new IOException(
                $"Package storage root '{workspacePath}' must not be a symbolic link or reparse point.");
        }
        Directory.CreateDirectory(workspacePath);
        var parent = Path.GetDirectoryName(workspacePath)
                     ?? throw new IOException("The package workspace has no parent directory.");
        PackageStorePathGuard.EnsureSafe(snapshot.BackupPath, parent);
        DurableJsonDocument.MoveDirectory(workspacePath, snapshot.BackupPath);
        snapshot.BackupMoved = true;
        DurableJsonDocument.SyncDirectory(parent);
        CopyDirectory(snapshot.BackupPath, workspacePath);
    }

    private static void RestoreWorkspaceSnapshot(WorkspaceSnapshot snapshot)
    {
        if (!snapshot.BackupMoved)
        {
            return;
        }
        if (!Directory.Exists(snapshot.BackupPath))
        {
            throw new DirectoryNotFoundException(
                $"Package workspace activation backup '{snapshot.BackupPath}' is unavailable.");
        }
        if (Directory.Exists(snapshot.WorkspacePath))
        {
            Directory.Delete(snapshot.WorkspacePath, recursive: true);
        }
        DurableJsonDocument.MoveDirectory(snapshot.BackupPath, snapshot.WorkspacePath);
        DurableJsonDocument.SyncDirectory(Path.GetDirectoryName(snapshot.WorkspacePath)!);
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        var source = new DirectoryInfo(sourcePath);
        Directory.CreateDirectory(destinationPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
        }
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            var destination = Path.Combine(destinationPath, entry.Name);
            if (entry.LinkTarget is { } linkTarget)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(destination, linkTarget);
                }
                else
                {
                    File.CreateSymbolicLink(destination, linkTarget);
                }
                continue;
            }
            if (entry is DirectoryInfo directory)
            {
                CopyDirectory(directory.FullName, destination);
            }
            else
            {
                PackageStoreFileSystem.EnsureRegularFile(entry.FullName);
                File.Copy(entry.FullName, destination, overwrite: false);
            }
        }
    }

    private sealed record PackageSnapshot(
        ActiveLoadedPackage Package,
        JsonPackageKeyValueStore StateStore,
        AtomicFileSnapshot StateSnapshot,
        PackageSettings Settings,
        AtomicFileSnapshot SettingsSnapshot,
        AtomicFileSnapshot SecretsSnapshot,
        AtomicFileSnapshot? JournalSnapshot,
        WorkspaceSnapshot? WorkspaceSnapshot,
        WorkspaceSnapshot? FileStoreSnapshot,
        PackageStorageActivationJournal ActivationJournal);

    private sealed record WorkspaceSnapshot(string WorkspacePath, string BackupPath)
    {
        internal bool BackupMoved { get; set; }
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed class PackageStorageRollbackException(
    Exception snapshotFailure,
    Exception rollbackFailure)
    : AggregateException(
        "Package storage snapshot initialization failed and its partial filesystem changes could not be rolled back.",
        snapshotFailure,
        rollbackFailure);
