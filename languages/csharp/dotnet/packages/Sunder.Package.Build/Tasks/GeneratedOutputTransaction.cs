using System.Text.Json;

namespace Sunder.Package.Build.Tasks;

internal static class GeneratedOutputTransaction
{
    internal const string TransactionSuffix = ".sunder-output-transaction.json";
    private const string InstalledSuffix = ".installed";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed record StagedOutput(string FinalPath, string StagedPath);

    public static void RecoverAndCleanup(params string[] finalPaths)
    {
        var normalized = finalPaths.Select(Path.GetFullPath).ToHashSet(PathComparer);
        foreach (var coordinatorPath in normalized)
        {
            var journalPath = TransactionPath(coordinatorPath);
            if (!File.Exists(journalPath)) continue;
            var transaction = ReadTransaction(journalPath, normalized);
            var installedPath = journalPath + InstalledSuffix;
            if (File.Exists(installedPath)
                && string.Equals(File.ReadAllText(installedPath).Trim(), transaction.Token, StringComparison.Ordinal))
            {
                Finalize(transaction, journalPath, installedPath);
            }
            else
            {
                Rollback(transaction, journalPath, installedPath);
            }
        }
        CleanupOrphans(normalized);
    }

    public static void Commit(params StagedOutput[] outputs)
    {
        if (outputs.Length == 0) return;
        var token = Guid.NewGuid().ToString("D");
        var coordinatorPath = Path.GetFullPath(outputs[0].FinalPath);
        var journalPath = TransactionPath(coordinatorPath);
        var installedPath = journalPath + InstalledSuffix;
        var entries = outputs.Select(output =>
        {
            var finalPath = Path.GetFullPath(output.FinalPath);
            var stagedPath = Path.GetFullPath(output.StagedPath);
            if (!PathExists(stagedPath)) throw new IOException($"Staged generated output '{stagedPath}' is missing.");
            if (IsMarkerSidecar(finalPath))
            {
                ValidateOptionalRegularFile(stagedPath, "staged generated output marker");
                ValidateOptionalRegularFile(finalPath, "generated output marker");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            return new TransactionEntry(
                finalPath,
                stagedPath,
                finalPath + ".backup-" + token,
                PathExists(finalPath));
        }).ToArray();
        var transaction = new Transaction(1, token, coordinatorPath, entries);
        var journalStagingPath = journalPath + ".write-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteDurableJson(journalStagingPath, transaction);
            File.Move(journalStagingPath, journalPath);
            foreach (var entry in entries)
            {
                if (entry.Existed) Move(entry.FinalPath, entry.BackupPath);
            }
            foreach (var entry in entries) Move(entry.StagedPath, entry.FinalPath);
            WriteDurableText(installedPath, token + Environment.NewLine);
            Finalize(transaction, journalPath, installedPath);
        }
        catch (Exception exception)
        {
            TryDeletePath(journalStagingPath);
            if (!File.Exists(journalPath)) throw;
            if (File.Exists(installedPath)
                && string.Equals(File.ReadAllText(installedPath).Trim(), token, StringComparison.Ordinal))
            {
                throw;
            }
            try
            {
                Rollback(transaction, journalPath, installedPath);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Generated output replacement failed and could not be fully rolled back.",
                    exception,
                    rollbackException);
            }
            throw;
        }
    }

    private static Transaction ReadTransaction(string journalPath, IReadOnlySet<string> allowedPaths)
    {
        Transaction transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<Transaction>(File.ReadAllText(journalPath), JsonOptions)
                          ?? throw new InvalidDataException("Transaction journal was empty.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Generated output transaction journal '{journalPath}' is unreadable; refusing unsafe recovery.",
                exception);
        }
        if (transaction.SchemaVersion != 1
            || !Guid.TryParseExact(transaction.Token, "D", out _)
            || !PathsEqual(TransactionPath(transaction.CoordinatorPath), journalPath)
            || transaction.Outputs.Length == 0)
        {
            throw new InvalidDataException($"Generated output transaction journal '{journalPath}' is invalid; refusing unsafe recovery.");
        }
        foreach (var output in transaction.Outputs)
        {
            var finalPath = Path.GetFullPath(output.FinalPath);
            var validStage = output.StagedPath.StartsWith(finalPath + ".stage-", PathComparison);
            if (!validStage && finalPath.EndsWith(ValidateSunderDevOutputPathTask.MarkerFileName, PathComparison))
            {
                var outputRoot = finalPath[..^ValidateSunderDevOutputPathTask.MarkerFileName.Length];
                validStage = output.StagedPath.StartsWith(outputRoot + ".stage-", PathComparison)
                             && output.StagedPath.EndsWith(ValidateSunderDevOutputPathTask.MarkerFileName, PathComparison);
            }
            if (!allowedPaths.Contains(finalPath)
                || !PathsEqual(output.StagedPath, Path.GetFullPath(output.StagedPath))
                || !validStage
                || !PathsEqual(output.BackupPath, finalPath + ".backup-" + transaction.Token))
            {
                throw new InvalidDataException(
                    $"Generated output transaction journal '{journalPath}' contains unsafe paths; refusing recovery.");
            }
            if (IsMarkerSidecar(finalPath))
            {
                ValidateOptionalRegularFile(finalPath, "generated output marker");
                ValidateOptionalRegularFile(output.StagedPath, "staged generated output marker");
                ValidateOptionalRegularFile(output.BackupPath, "backed-up generated output marker");
            }
        }
        return transaction;
    }

    private static void Rollback(Transaction transaction, string journalPath, string installedPath)
    {
        foreach (var output in transaction.Outputs.Reverse())
        {
            if (output.Existed && PathExists(output.BackupPath))
            {
                DeleteTransactionPath(output, output.FinalPath);
                Move(output.BackupPath, output.FinalPath);
            }
            else if (!output.Existed)
            {
                DeleteTransactionPath(output, output.FinalPath);
            }
            DeleteTransactionPath(output, output.StagedPath);
        }
        File.Delete(journalPath);
        TryDeletePath(installedPath);
    }

    private static void Finalize(Transaction transaction, string journalPath, string installedPath)
    {
        foreach (var output in transaction.Outputs)
        {
            if (!PathExists(output.FinalPath))
            {
                throw new IOException($"Committed generated output '{output.FinalPath}' is missing during crash recovery.");
            }
            if (IsMarkerSidecar(output.FinalPath))
            {
                ValidateOptionalRegularFile(output.FinalPath, "committed generated output marker");
            }
        }
        foreach (var output in transaction.Outputs)
        {
            DeleteTransactionPath(output, output.BackupPath);
            DeleteTransactionPath(output, output.StagedPath);
        }
        File.Delete(journalPath);
        TryDeletePath(installedPath);
    }

    private static void CleanupOrphans(IEnumerable<string> finalPaths)
    {
        foreach (var finalPath in finalPaths)
        {
            var markerSidecar = IsMarkerSidecar(finalPath);
            if (markerSidecar) ValidateOptionalRegularFile(finalPath, "generated output marker");
            var parent = Path.GetDirectoryName(finalPath);
            if (parent is null || !Directory.Exists(parent)) continue;
            var name = Path.GetFileName(finalPath);
            foreach (var stage in Directory.EnumerateFileSystemEntries(parent, name + ".stage-*"))
            {
                if (markerSidecar || IsMarkerSidecar(stage))
                {
                    ValidateOptionalRegularFile(stage, "orphaned staged generated output marker");
                    File.Delete(stage);
                }
                else
                {
                    DeletePath(stage);
                }
            }
            foreach (var write in Directory.EnumerateFileSystemEntries(parent, name + TransactionSuffix + ".write-*"))
            {
                DeletePath(write);
            }
            TryDeletePath(finalPath + TransactionSuffix + InstalledSuffix);
            var backups = Directory.EnumerateFileSystemEntries(parent, name + ".backup-*")
                .Select(path =>
                {
                    if (markerSidecar) ValidateOptionalRegularFile(path, "orphaned backed-up generated output marker");
                    return new { Path = path, LastWrite = File.GetLastWriteTimeUtc(path) };
                })
                .OrderByDescending(static item => item.LastWrite)
                .ThenBy(static item => item.Path, PathComparer)
                .ToArray();
            if (backups.Length == 0) continue;
            if (!PathExists(finalPath)) Move(backups[0].Path, finalPath);
            foreach (var backup in backups)
            {
                if (markerSidecar) File.Delete(backup.Path);
                else DeletePath(backup.Path);
            }
        }
    }

    private static bool IsMarkerSidecar(string path)
        => path.EndsWith(ValidateSunderDevOutputPathTask.MarkerFileName, PathComparison)
           || path.EndsWith(AggregateSunderPackageTask.MarkerFileSuffix, PathComparison);

    private static void ValidateOptionalRegularFile(string path, string label)
    {
        if (!PathExists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {label} '{path}' must be a regular file.");
        }
    }

    private static void DeleteTransactionPath(TransactionEntry output, string path)
    {
        if (IsMarkerSidecar(output.FinalPath))
        {
            ValidateOptionalRegularFile(path, "generated output marker transaction artifact");
            File.Delete(path);
        }
        else
        {
            DeletePath(path);
        }
    }

    private static void WriteDurableJson(string path, Transaction transaction)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, transaction, JsonOptions);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static void WriteDurableText(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(value);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void Move(string source, string destination)
    {
        if (Directory.Exists(source) && !IsReparsePoint(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void DeletePath(string path)
    {
        if (!PathExists(path)) return;
        if (Directory.Exists(path) && !IsReparsePoint(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            DeletePath(path);
        }
        catch
        {
            // Best-effort cleanup leaves the journal or lock to drive later recovery.
        }
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string TransactionPath(string coordinatorPath)
        => Path.GetFullPath(coordinatorPath) + TransactionSuffix;

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record Transaction(int SchemaVersion, string Token, string CoordinatorPath, TransactionEntry[] Outputs);

    private sealed record TransactionEntry(string FinalPath, string StagedPath, string BackupPath, bool Existed);
}
