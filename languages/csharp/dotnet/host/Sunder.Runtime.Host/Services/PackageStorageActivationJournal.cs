using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Runtime.Host.Infrastructure.Storage;

namespace Sunder.Runtime.Host.Services;

// This journal records the outcome, not a crash-time rollback plan. State, settings,
// and secret snapshots are process-local: an interrupted activation must stay fenced.
internal sealed class PackageStorageActivationJournal
{
    internal const string FilePattern = ".sunder-storage-activation-*.json";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _packageRoot;
    private readonly string _path;
    private readonly PackageStorageActivationFileSystem _fileSystem;
    private PackageStorageActivationJournalDocument _document;

    private PackageStorageActivationJournal(
        string packageRoot,
        PackageStorageActivationJournalDocument document,
        PackageStorageActivationFileSystem fileSystem)
    {
        _packageRoot = packageRoot;
        _document = document;
        _fileSystem = fileSystem;
        _path = Path.Combine(packageRoot, $".sunder-storage-activation-{document.TransactionId}.json");
    }

    internal string GetBackupPath(string directoryName)
        => Path.Combine(_packageRoot, $".{directoryName}.sunder-activation-{_document.TransactionId}");

    internal static async Task<PackageStorageActivationJournal> CreateAsync(
        string packageId,
        string packageRoot,
        bool hasWorkspace,
        PackageStorageActivationFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        var journal = new PackageStorageActivationJournal(
            Path.GetFullPath(packageRoot),
            new(1, packageId, Guid.NewGuid().ToString("N"), PackageStorageActivationOutcome.Pending,
                hasWorkspace ? ["workspace", "files"] : ["files"]),
            fileSystem);
        PackageStorePathGuard.EnsureSafe(journal._path, journal._packageRoot);
        // Persist intent before the first workspace/files rename, including dev activations.
        await fileSystem.WriteJournalAsync(journal._path, journal._document, cancellationToken)
            .ConfigureAwait(false);
        return journal;
    }

    internal async Task RecordOutcomeAsync(PackageStorageActivationOutcome outcome)
    {
        var document = _document with { Outcome = outcome };
        await _fileSystem.WriteJournalAsync(_path, document, CancellationToken.None).ConfigureAwait(false);
        _document = document;
    }

    internal void TryCleanup(ILogger logger)
    {
        if (_document.Outcome == PackageStorageActivationOutcome.Pending)
        {
            throw new InvalidOperationException("Storage activation cleanup requires a durable terminal outcome.");
        }
        try
        {
            foreach (var directory in _document.Directories)
            {
                var backup = GetBackupPath(directory);
                PackageStorePathGuard.EnsureSafe(backup, _packageRoot);
                if (BackupExists(backup))
                {
                    _fileSystem.DeleteBackup(backup);
                }
            }
            // The terminal outcome must outlive every backup, even partially deleted ones.
            _fileSystem.DeleteJournal(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "Package {PackageId} storage activation cleanup is pending at {JournalPath}; startup will retry it",
                _document.PackageId, _path);
        }
    }

    internal static async Task<IReadOnlySet<string>> RecoverAsync(
        string packageRoot,
        ILogger logger,
        CancellationToken cancellationToken,
        PackageStorageActivationFileSystem? fileSystem = null)
    {
        fileSystem ??= new PackageStorageActivationFileSystem();
        var journals = new List<PackageStorageActivationJournal>();
        foreach (var path in Directory.EnumerateFiles(packageRoot, FilePattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageStorePathGuard.EnsureSafe(path, packageRoot);
            PackageStorageActivationJournalDocument document;
            try
            {
                if (new FileInfo(path).Length > 16 * 1024)
                {
                    throw new InvalidDataException("The activation journal is oversized.");
                }
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false));
                var names = json.RootElement.ValueKind == JsonValueKind.Object
                    ? json.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
                    : [];
                if (names.Length != 5 || !names.ToHashSet(StringComparer.Ordinal).SetEquals(
                        ["version", "packageId", "transactionId", "outcome", "directories"]))
                {
                    throw new InvalidDataException("The activation journal has invalid properties.");
                }
                document = json.Deserialize<PackageStorageActivationJournalDocument>(JsonOptions)
                           ?? throw new InvalidDataException("The activation journal is empty.");
                if (document.Version != 1
                    || string.IsNullOrWhiteSpace(document.PackageId)
                    || !Guid.TryParseExact(document.TransactionId, "N", out _)
                    || !Enum.IsDefined(document.Outcome)
                    || document.Directories is not { Length: > 0 and <= 2 }
                    || document.Directories.Any(name => name is not ("workspace" or "files"))
                    || document.Directories.Distinct(StringComparer.Ordinal).Count() != document.Directories.Length
                    || !PathEquals(path, Path.Combine(packageRoot,
                        $".sunder-storage-activation-{document.TransactionId}.json"))
                    || !PathEquals(packageRoot, LocalPackageStorageContext.GetPackageRootPath(
                        document.PackageId, Path.GetDirectoryName(packageRoot)!)))
                {
                    throw new InvalidDataException("The activation journal identity is invalid.");
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                throw new PackageStorageActivationRecoveryException(
                    Path.GetFileName(packageRoot),
                    $"Storage activation journal '{path}' is invalid. Its evidence was preserved; explicit storage recovery is required.",
                    exception);
            }

            var journal = new PackageStorageActivationJournal(packageRoot, document, fileSystem);
            foreach (var directory in document.Directories)
            {
                var target = Path.Combine(packageRoot, directory);
                var backup = journal.GetBackupPath(directory);
                PackageStorePathGuard.EnsureSafe(target, packageRoot);
                PackageStorePathGuard.EnsureSafe(backup, packageRoot);
                if (File.Exists(backup)
                    || document.Outcome == PackageStorageActivationOutcome.Committed && !Directory.Exists(target)
                    || document.Outcome == PackageStorageActivationOutcome.RolledBack && Directory.Exists(backup))
                {
                    throw new PackageStorageActivationRecoveryException(document.PackageId,
                        $"Storage activation journal '{path}' disagrees with package storage. All evidence was preserved; explicit storage recovery is required.");
                }
            }
            if (document.Outcome == PackageStorageActivationOutcome.Pending)
            {
                throw new PackageStorageActivationRecoveryException(document.PackageId,
                    $"Storage activation '{document.TransactionId}' was interrupted without a durable outcome. " +
                    "Storage and backups were preserved; explicit storage recovery is required before retrying startup.");
            }
            journals.Add(journal);
        }

        // Validate all evidence before cleanup. A committed backup is disposable even
        // when deletion fails again; it must not be reclassified as an unknown activation.
        var ownedBackups = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var journal in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ownedBackups.UnionWith(journal._document.Directories.Select(journal.GetBackupPath));
            journal.TryCleanup(logger);
        }
        return ownedBackups;
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool BackupExists(string path)
    {
        try
        {
            // Directory.Exists also returns false on access/I/O failures. Only a
            // positively absent backup permits removal of its durable outcome.
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
                throw new IOException($"Storage activation backup '{path}' is not a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

internal enum PackageStorageActivationOutcome { Pending, Committed, RolledBack }

internal sealed record PackageStorageActivationJournalDocument(
    int Version,
    string PackageId,
    string TransactionId,
    PackageStorageActivationOutcome Outcome,
    string[] Directories);

internal class PackageStorageActivationFileSystem
{
    internal virtual Task WriteJournalAsync(string path, PackageStorageActivationJournalDocument document,
        CancellationToken cancellationToken)
        => DurableJsonDocument.WriteAsync(path, document, PackageStorageActivationJournal.JsonOptions, cancellationToken);

    internal virtual void DeleteBackup(string path)
    {
        Directory.Delete(path, recursive: true);
        DurableJsonDocument.SyncDirectory(Path.GetDirectoryName(path)!);
    }

    internal virtual void DeleteJournal(string path) => DurableJsonDocument.Delete(path);
}

internal sealed class PackageStorageActivationRecoveryException(
    string packageId, string detail, Exception? innerException = null)
    : IOException($"Package '{packageId}' requires storage activation recovery: {detail}", innerException)
{
    internal const string Code = "runtime.storage-activation-recovery-required";
}
