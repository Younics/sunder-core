using System.Text.Json;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed record PackageStorageMigrationPlanIdentity(
    string Store,
    string MigrationId,
    string PlanSha256);

internal sealed class PackageStorageMigrationJournal
{
    private const string FormatName = "sunder.package-storage-migrations";
    private const int CurrentVersion = 1;
    private const int MaximumCompletions = 512;
    private readonly AtomicFileDocument _document;

    internal PackageStorageMigrationJournal(string filePath)
    {
        _document = new AtomicFileDocument(
            filePath,
            sensitive: false,
            StorageCommitPhase.MigrationJournal);
    }

    internal Task<bool> IsCompletedAsync(
        PackageStorageMigrationPlanIdentity plan,
        CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan);
        return _document.ExecuteAsync(transaction =>
        {
            var journal = Load(transaction);
            if (!journal.Completions.TryGetValue(plan.MigrationId, out var completion))
            {
                return false;
            }
            EnsureMatches(plan, completion);
            return true;
        }, cancellationToken);
    }

    internal Task ValidateAsync(
        IReadOnlyList<PackageStorageMigrationPlanIdentity> plans,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        foreach (var plan in plans) ValidatePlan(plan);
        return _document.ExecuteAsync(transaction =>
        {
            var journal = Load(transaction);
            foreach (var plan in plans)
            {
                if (journal.Completions.TryGetValue(plan.MigrationId, out var completion))
                {
                    EnsureMatches(plan, completion);
                }
            }
            return true;
        }, cancellationToken);
    }

    internal Task CommitAsync(
        IReadOnlyList<PackageStorageMigrationPlanIdentity> plans,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        foreach (var plan in plans) ValidatePlan(plan);
        if (plans.Count == 0)
        {
            return Task.CompletedTask;
        }
        return _document.ExecuteAsync(transaction =>
        {
            var journal = Load(transaction);
            var changed = false;
            foreach (var plan in plans)
            {
                if (journal.Completions.TryGetValue(plan.MigrationId, out var completion))
                {
                    EnsureMatches(plan, completion);
                    continue;
                }
                journal.Completions.Add(
                    plan.MigrationId,
                    new MigrationCompletion(plan.Store, plan.PlanSha256, DateTimeOffset.UtcNow));
                changed = true;
            }
            if (journal.Completions.Count > MaximumCompletions)
            {
                throw new InvalidOperationException(
                    $"A package cannot retain more than {MaximumCompletions} storage migration completions.");
            }
            if (changed)
            {
                Save(transaction, journal with { Revision = checked(journal.Revision + 1) });
            }
            return true;
        }, cancellationToken);
    }

    internal Task<AtomicFileSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
        => _document.CaptureSnapshotAsync(cancellationToken);

    internal Task RestoreSnapshotAsync(
        AtomicFileSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => _document.RestoreSnapshotAsync(snapshot, cancellationToken);

    private static MigrationJournalDocument Load(AtomicFileTransaction transaction)
    {
        var contents = transaction.Read();
        if (contents is null)
        {
            return new MigrationJournalDocument(0, new(StringComparer.Ordinal));
        }
        using var document = JsonDocument.Parse(contents);
        var root = PackageStorageJson.RequireObject(document, "package storage migration journal");
        var properties = root.EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (root.EnumerateObject().Count() != 4
            || !properties.SetEquals(["format", "version", "revision", "completions"]))
        {
            throw new InvalidDataException("The package storage migration journal has unsupported properties.");
        }
        if (!string.Equals(
                PackageStorageJson.ReadString(root, "format", "package storage migration journal"),
                FormatName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The package storage migration journal format is invalid.");
        }
        PackageStorageJson.RequireVersion(
            PackageStorageJson.ReadInt32(root, "version", "package storage migration journal"),
            CurrentVersion,
            "Package storage migration journal version");
        var revision = PackageStorageJson.ReadInt64(root, "revision", "package storage migration journal");
        if (revision < 0
            || !root.TryGetProperty("completions", out var values)
            || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() > MaximumCompletions)
        {
            throw new InvalidDataException("The package storage migration journal is invalid or oversized.");
        }
        var completions = new Dictionary<string, MigrationCompletion>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A package storage migration completion must be an object.");
            }
            var itemProperties = value.EnumerateObject()
                .Select(static property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (value.EnumerateObject().Count() != 4
                || !itemProperties.SetEquals(["migrationId", "store", "planSha256", "completedAtUtc"]))
            {
                throw new InvalidDataException("A package storage migration completion has unsupported properties.");
            }
            var migrationId = PackageStorageJson.ReadString(
                value,
                "migrationId",
                "package storage migration completion");
            var store = PackageStorageJson.ReadString(value, "store", "package storage migration completion");
            var planSha256 = PackageStorageJson.ReadString(
                value,
                "planSha256",
                "package storage migration completion");
            var completedText = PackageStorageJson.ReadString(
                value,
                "completedAtUtc",
                "package storage migration completion");
            if (!IsValidMigrationId(migrationId)
                || store is not ("state" or "secrets")
                || !IsSha256(planSha256)
                || !DateTimeOffset.TryParse(
                    completedText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var completedAtUtc)
                || completedAtUtc.Offset != TimeSpan.Zero
                || !completions.TryAdd(
                    migrationId,
                    new MigrationCompletion(store, planSha256, completedAtUtc)))
            {
                throw new InvalidDataException("A package storage migration completion is invalid.");
            }
        }
        return new MigrationJournalDocument(revision, completions);
    }

    private static void Save(AtomicFileTransaction transaction, MigrationJournalDocument journal)
    {
        var document = new
        {
            format = FormatName,
            version = CurrentVersion,
            revision = journal.Revision,
            completions = journal.Completions
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new
                {
                    migrationId = pair.Key,
                    store = pair.Value.Store,
                    planSha256 = pair.Value.PlanSha256,
                    completedAtUtc = pair.Value.CompletedAtUtc.UtcDateTime.ToString("O"),
                })
                .ToArray(),
        };
        transaction.Write(JsonSerializer.SerializeToUtf8Bytes(document, PackageStorageJson.Options));
    }

    private static void EnsureMatches(
        PackageStorageMigrationPlanIdentity plan,
        MigrationCompletion completion)
    {
        if (!string.Equals(plan.Store, completion.Store, StringComparison.Ordinal)
            || !string.Equals(plan.PlanSha256, completion.PlanSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Completed package storage migration '{plan.MigrationId}' was redeclared with a different canonical plan.");
        }
    }

    private static void ValidatePlan(PackageStorageMigrationPlanIdentity plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Store is not ("state" or "secrets")
            || !IsValidMigrationId(plan.MigrationId)
            || !IsSha256(plan.PlanSha256))
        {
            throw new ArgumentException("The package storage migration plan identity is invalid.", nameof(plan));
        }
    }

    private static bool IsValidMigrationId(string value)
        => value.Length is > 0 and <= 256
           && value.All(static character => char.IsAsciiLetterOrDigit(character)
                                                  || character is '.' or '-' or '_' or ':' or '+');

    private static bool IsSha256(string value)
        => value.Length == 64
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record MigrationJournalDocument(
        long Revision,
        Dictionary<string, MigrationCompletion> Completions);

    private sealed record MigrationCompletion(
        string Store,
        string PlanSha256,
        DateTimeOffset CompletedAtUtc);
}
