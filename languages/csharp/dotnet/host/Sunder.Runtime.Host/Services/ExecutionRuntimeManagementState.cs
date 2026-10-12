using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ExecutionRuntimeManualInstall(RuntimeExecutionRuntimeIdentity Runtime,
    RuntimeExecutionRuntimeRequirement Requirement);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ExecutionRuntimeManagementDocument(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] RuntimeExecutionRuntimeSettings Settings,
    [property: JsonRequired] IReadOnlyList<ExecutionRuntimeManualInstall> ManualInstalls,
    [property: JsonRequired] IReadOnlyList<RuntimeExecutionRuntimePreparedUpdate> Updates,
    DateTimeOffset? LastCheckedAtUtc = null, DateTimeOffset? NextCheckAtUtc = null, string? LastErrorCode = null);

/// <summary>User intent and pending transitions only. InstalledPackageStore remains binding authority.</summary>
internal sealed class ExecutionRuntimeManagementState(RuntimePackagePaths paths, RuntimeEventStreamService? events = null)
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _writeFailed;
    internal event Action<bool>? AutomaticUpdatesChanged;
    internal string FilePath => Path.Combine(paths.RootPath, RuntimeV1StateDescriptor.ExecutionRuntimeManagementFile);

    public Task<ExecutionRuntimeManagementDocument> ReadAsync(CancellationToken token)
        => ChangeAsync(static state => state, token);

    public async Task<ExecutionRuntimeManagementDocument> ChangeAsync(
        Func<ExecutionRuntimeManagementDocument, ExecutionRuntimeManagementDocument> change, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var changed = false;
        try
        {
            if (_writeFailed) throw new InvalidDataException("Runtime management state requires recovery after a failed durable write.");
            PackageStorePathGuard.EnsureSafe(FilePath, paths.RootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var fileLock = await RuntimeFileLock.AcquireExclusiveAsync(FilePath + ".lock", token);
            ExecutionRuntimeManagementDocument current;
            var exists = File.Exists(FilePath);
            if (exists)
            {
                await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Runtime management state exceeds its bound.");
                current = await JsonSerializer.DeserializeAsync<ExecutionRuntimeManagementDocument>(stream, Json, token)
                    ?? throw new InvalidDataException("Runtime management state is empty.");
                Validate(current);
            }
            else current = new(1, new(1, true), [], []);
            var desired = change(current);
            Validate(desired);
            if (!exists || !ReferenceEquals(current, desired))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(desired, Json);
                if (bytes.Length > MaximumBytes)
                    throw new RuntimeConflictException("Runtime management state exceeds its durable byte bound; existing intent was preserved.");
                if (exists && bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(current, Json))) return current;
                try { await DurableJsonDocument.WriteAsync(FilePath, desired, Json, token); }
                catch { _writeFailed = true; throw; }
                changed = true;
            }
            return desired;
        }
        finally
        {
            _gate.Release();
            if (changed) events?.PublishExecutionRuntimesChanged();
        }
    }

    public async Task<RuntimeExecutionRuntimeSettings> UpdateSettingsAsync(RuntimeExecutionRuntimeSettingsUpdateRequest request, CancellationToken token)
    {
        var settings = (await ChangeAsync(state => state.Settings.Revision != request.ExpectedRevision
            ? throw new RuntimeConflictException("Runtime update settings changed; reload before saving.")
            : state with { Settings = new(checked(state.Settings.Revision + 1), request.AutomaticUpdatesEnabled),
                NextCheckAtUtc = request.AutomaticUpdatesEnabled ? state.NextCheckAtUtc : null }, token)).Settings;
        AutomaticUpdatesChanged?.Invoke(settings.AutomaticUpdatesEnabled);
        return settings;
    }

    public Task<ExecutionRuntimeManagementDocument> RetainAsync(ExecutionRuntimeManualInstall install, CancellationToken token)
        => ChangeAsync(state => state with { ManualInstalls = state.ManualInstalls
            .Where(value => value.Runtime != install.Runtime).Append(install).ToArray() }, token);

    public Task<ExecutionRuntimeManagementDocument> SaveUpdateAsync(RuntimeExecutionRuntimePreparedUpdate update, CancellationToken token)
        => ChangeAsync(state =>
        {
            var existing = state.Updates.Where(value => value.UpdateId != update.UpdateId).ToList();
            while (existing.Count >= 32)
            {
                var terminal = existing.FindIndex(value => !Pending(value));
                if (terminal < 0) throw new RuntimeConflictException("Pending runtime update limit reached.");
                existing.RemoveAt(terminal);
            }
            return state with { Updates = existing.Append(update).ToArray() };
        }, token);

    internal static bool Pending(RuntimeExecutionRuntimePreparedUpdate update)
        => update.State is RuntimeExecutionRuntimeUpdateState.Prepared or RuntimeExecutionRuntimeUpdateState.Deferred
            or RuntimeExecutionRuntimeUpdateState.ReconciliationPending;

    internal static IReadOnlyList<ExecutionRuntimeRetention> Retentions(ExecutionRuntimeManagementDocument state)
        => state.ManualInstalls.Select(value => Retention(value.Runtime))
            .Concat(state.Updates.Where(Pending).SelectMany(update => update.Runtimes.SelectMany(value => new[]
            { Retention(value.CurrentRuntime), Retention(value.Resolution.Artifact.Identity) })))
            .Distinct().ToArray();

    private static ExecutionRuntimeRetention Retention(RuntimeExecutionRuntimeIdentity value)
        => new(ExecutionRuntimeManagementSupport.Identity(value));

    private static void Validate(ExecutionRuntimeManagementDocument state)
    {
        if (state.SchemaVersion != 1 || state.Settings is null || state.Settings.Revision <= 0
            || state.ManualInstalls is null || state.ManualInstalls.Count > 1024 || state.Updates is null || state.Updates.Count > 32
            || state.Updates.Select(value => value.UpdateId).Distinct(StringComparer.Ordinal).Count() != state.Updates.Count)
            throw new InvalidDataException("Runtime management state is invalid; automatic operations remain disabled.");
        foreach (var install in state.ManualInstalls)
        {
            ExecutionRuntimeManagementSupport.ValidateIdentity(install.Runtime);
            ExecutionRuntimeManagementSupport.ValidateRequirement(install.Requirement);
        }
        foreach (var update in state.Updates)
        {
            if (!Guid.TryParseExact(update.UpdateId, "N", out _) || !ExecutionRuntimeManagementSupport.IsHash(update.Fingerprint)
                || !Enum.IsDefined(update.State) || update.PreparedAtUtc == default
                || update.Fingerprint != ExecutionRuntimeManagementSupport.Hash(new { update.Runtimes, update.BindingChanges })
                || Pending(update) && update.Runtimes.Count == 0)
                throw new InvalidDataException("Pending runtime update evidence is invalid.");
            foreach (var candidate in update.Runtimes)
            {
                ExecutionRuntimeManagementSupport.ValidateIdentity(candidate.CurrentRuntime);
                ExecutionRuntimeManagementSupport.ValidateResolution(candidate.Resolution);
            }
            foreach (var change in update.BindingChanges)
            {
                if (!update.Runtimes.Any(candidate => candidate.CurrentRuntime == change.PreviousBinding.Runtime
                    && candidate.Resolution.Artifact.Identity == change.Resolution.Artifact.Identity))
                    throw new InvalidDataException("Pending binding change has no exact retained runtime candidate.");
            }
        }
    }
}
