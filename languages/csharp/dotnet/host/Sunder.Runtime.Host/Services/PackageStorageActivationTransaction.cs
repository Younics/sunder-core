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
        CancellationToken cancellationToken)
    {
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
                    var workspaceSnapshot = CaptureWorkspaceSnapshot(package);
                    snapshots.Add(new PackageSnapshot(
                        package,
                        state,
                        stateSnapshot,
                        settings,
                        settingsSnapshot,
                        secretsSnapshot,
                        journalSnapshot,
                        workspaceSnapshot));
                    secretsSnapshot = null;
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
        catch
        {
            try
            {
                await new PackageStorageActivationTransaction(snapshots).RollbackAsync()
                    .ConfigureAwait(false);
            }
            catch
            {
                ZeroSnapshots(snapshots);
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

    public void Complete()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        foreach (var package in _packages)
        {
            TryDeleteWorkspaceBackup(package.WorkspaceSnapshot);
        }
        ZeroSnapshots(_packages);
    }

    public async Task RollbackAsync()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        List<Exception>? failures = null;
        foreach (var package in _packages.Reverse())
        {
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

    private static WorkspaceSnapshot? CaptureWorkspaceSnapshot(ActiveLoadedPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Worker.WorkspaceRootPath))
        {
            return null;
        }
        var workspacePath = Path.GetFullPath(package.Worker.WorkspaceRootPath);
        var workspace = new DirectoryInfo(workspacePath);
        if (workspace.LinkTarget is not null)
        {
            throw new IOException(
                $"Package '{package.Descriptor.PackageId}' workspace root must not be a symbolic link or reparse point.");
        }
        Directory.CreateDirectory(workspacePath);
        var parent = Path.GetDirectoryName(workspacePath)
                     ?? throw new IOException("The package workspace has no parent directory.");
        var backupPath = Path.Combine(
            parent,
            $".{Path.GetFileName(workspacePath)}.sunder-activation-{Guid.NewGuid():N}");
        Directory.Move(workspacePath, backupPath);
        try
        {
            CopyDirectory(backupPath, workspacePath);
            return new WorkspaceSnapshot(workspacePath, backupPath);
        }
        catch
        {
            if (Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: true);
            }
            Directory.Move(backupPath, workspacePath);
            throw;
        }
    }

    private static void RestoreWorkspaceSnapshot(WorkspaceSnapshot snapshot)
    {
        if (!Directory.Exists(snapshot.BackupPath))
        {
            throw new DirectoryNotFoundException(
                $"Package workspace activation backup '{snapshot.BackupPath}' is unavailable.");
        }
        if (Directory.Exists(snapshot.WorkspacePath))
        {
            Directory.Delete(snapshot.WorkspacePath, recursive: true);
        }
        Directory.Move(snapshot.BackupPath, snapshot.WorkspacePath);
    }

    private static void TryDeleteWorkspaceBackup(WorkspaceSnapshot? snapshot)
    {
        if (snapshot is null || !Directory.Exists(snapshot.BackupPath))
        {
            return;
        }
        try
        {
            Directory.Delete(snapshot.BackupPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
        WorkspaceSnapshot? WorkspaceSnapshot);

    private sealed record WorkspaceSnapshot(string WorkspacePath, string BackupPath);
}
