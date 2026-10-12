using System.Text.Json;
using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeStoreRecovery(
    ExecutionRuntimeStoreLayout layout,
    ExecutionRuntimeStore store)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        CreateAndValidateRoots();
        using var recoveryFence = await RuntimeFileLock.AcquireExclusiveAsync(
                layout.RecoveryFencePath,
                cancellationToken)
            .ConfigureAwait(false);
        await store.RecoverProcessLeasesAsync(cancellationToken).ConfigureAwait(false);
        await store.RecoverTombstonesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var transactionPath in EnumerateDirectories(layout.TransactionRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transactionId = Path.GetFileName(transactionPath);
            if (!Guid.TryParseExact(transactionId, "N", out _))
            {
                await store.QuarantineAsync(transactionPath, "invalid-transaction", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            await RecoverTransactionCoreAsync(transactionId, cancellationToken).ConfigureAwait(false);
        }

        var activeTransactions = EnumerateDirectories(layout.TransactionRootPath)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stagingPath in EnumerateDirectories(layout.StagingRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!activeTransactions.Contains(Path.GetFileName(stagingPath)))
            {
                await store.QuarantineAsync(stagingPath, "unowned-staging", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task RecoverTransactionAsync(
        string transactionId,
        CancellationToken cancellationToken)
    {
        using var recoveryFence = await RuntimeFileLock.AcquireSharedAsync(
                layout.RecoveryFencePath,
                cancellationToken)
            .ConfigureAwait(false);
        await RecoverTransactionCoreAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task RecoverTransactionCoreAsync(
        string transactionId,
        CancellationToken cancellationToken)
    {
        var transactionPath = layout.GetTransactionPath(transactionId);
        var stagingPath = layout.GetStagingPath(transactionId);
        var journalPath = layout.GetTransactionJournalPath(transactionId);
        if (!File.Exists(journalPath))
        {
            await store.QuarantineAsync(stagingPath, "ambiguous-staging", cancellationToken)
                .ConfigureAwait(false);
            await store.QuarantineAsync(transactionPath, "ambiguous-transaction", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ExecutionRuntimeStoreTransactionJournal journal;
        try
        {
            await using var stream = new FileStream(
                journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > OfficialRuntimeSourcePolicy.MaxMetadataBytes)
            {
                throw new InvalidDataException(
                    $"Execution runtime transaction journal '{journalPath}' has an invalid size.");
            }
            journal = await JsonSerializer.DeserializeAsync<ExecutionRuntimeStoreTransactionJournal>(
                          stream,
                          ExecutionRuntimeStore.JsonOptions,
                          cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidDataException(
                          $"Execution runtime transaction journal '{journalPath}' is empty.");
            store.ValidateJournal(journal);
            if (!string.Equals(journal.TransactionId, transactionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Execution runtime transaction journal '{journalPath}' has the wrong identity.");
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or JsonException
                                           or InvalidDataException
                                           or ArgumentException)
        {
            await store.QuarantineAsync(transactionPath, "invalid-transaction", cancellationToken)
                .ConfigureAwait(false);
            await store.QuarantineAsync(stagingPath, "ambiguous-staging", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (await store.TryCompleteRecoveryAsync(journal, cancellationToken).ConfigureAwait(false))
        {
            ExecutionRuntimeStore.TryDeleteDirectory(stagingPath);
            ExecutionRuntimeStore.TryDeleteDirectory(transactionPath);
            return;
        }

        ExecutionRuntimeStore.TryDeleteDirectory(stagingPath);
        ExecutionRuntimeStore.TryDeleteDirectory(transactionPath);
    }

    private void CreateAndValidateRoots()
    {
        foreach (var path in layout.DurableRootPaths)
        {
            Directory.CreateDirectory(path);
            PackageStorePathGuard.EnsureNotReparsePoint(path);
            layout.EnsureContained(path, layout.RootPath);
        }
    }

    private static IReadOnlyList<string> EnumerateDirectories(string path)
        => Directory.EnumerateDirectories(path).ToArray();
}
