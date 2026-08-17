namespace Sunder.App.Services;

public sealed class AppRuntimeResourcePolicySettingsService
{
    private readonly object _syncRoot = new();

    public AppRuntimeResourcePolicySettingsService(string? settingsFilePath = null)
    {
        SettingsFilePath = Path.GetFullPath(settingsFilePath ?? GetDefaultSettingsFilePath());
    }

    public string SettingsFilePath { get; }

    internal static string GetDefaultSettingsFilePath()
        => AppLocalState.GetPath("runtime-resource-policy.json");

    public RuntimeResourcePolicySelection Load()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(SettingsFilePath))
            {
                return RuntimeResourcePolicies.DefaultSelection;
            }

            try
            {
                var file = new FileInfo(SettingsFilePath);
                if (file.Length <= 0 || file.Length > RuntimeResourcePolicyContract.MaximumSettingsFileBytes)
                {
                    throw new InvalidDataException(
                        $"Runtime resource policy settings must be between 1 and {RuntimeResourcePolicyContract.MaximumSettingsFileBytes} bytes.");
                }
                return RuntimeResourcePolicyContract.ParseSettings(File.ReadAllBytes(SettingsFilePath));
            }
            catch (Exception exception)
            {
                AppSessionLog.WriteError(
                    "Runtime resource policy settings are invalid; using Standard.",
                    exception);
                QuarantineInvalidSettings();
                return RuntimeResourcePolicies.DefaultSelection;
            }
        }
    }

    public void Save(RuntimeResourcePolicySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var snapshot = Snapshot(selection);
        var bytes = RuntimeResourcePolicyContract.SerializeSettings(snapshot);

        lock (_syncRoot)
        {
            var directory = Path.GetDirectoryName(SettingsFilePath)
                ?? throw new InvalidOperationException("Runtime resource policy settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(SettingsFilePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, SettingsFilePath, overwrite: true);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }
    }

    public Task SaveAsync(
        RuntimeResourcePolicySelection selection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot(selection);
        return Task.Run(() => Save(snapshot), cancellationToken);
    }

    private static RuntimeResourcePolicySelection Snapshot(RuntimeResourcePolicySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var custom = selection.CustomPolicy;
        return new RuntimeResourcePolicySelection(
            selection.Preset,
            new RuntimeResourcePolicy(
                custom.MaximumRpcValueBytes,
                custom.MaximumValueDepth,
                custom.MaximumValueNodes,
                custom.MaximumObjectMembers,
                custom.MaximumInlineDecodedBytes,
                custom.MaximumContentReferenceBytes));
    }

    private void QuarantineInvalidSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                File.Move(
                    SettingsFilePath,
                    $"{SettingsFilePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
            }
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to quarantine invalid Runtime resource policy settings.", exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to delete a temporary Runtime resource policy settings file.", exception);
        }
    }
}

public sealed record RuntimeResourcePolicyEffectiveStatus
{
    private RuntimeResourcePolicyEffectiveStatus(
        RuntimeResourcePolicy? effectivePolicy,
        string? effectiveProfileHash,
        string? unavailableReason)
    {
        EffectivePolicy = effectivePolicy;
        EffectiveProfileHash = effectiveProfileHash;
        UnavailableReason = unavailableReason;
    }

    public RuntimeResourcePolicy? EffectivePolicy { get; }

    public string? EffectiveProfileHash { get; }

    public string? UnavailableReason { get; }

    public bool IsAvailable => EffectivePolicy is not null;

    public static RuntimeResourcePolicyEffectiveStatus Available(RuntimeResourcePolicy effectivePolicy)
    {
        ArgumentNullException.ThrowIfNull(effectivePolicy);
        return new RuntimeResourcePolicyEffectiveStatus(
            effectivePolicy,
            RuntimeResourcePolicyContract.GetEffectiveProfileHash(effectivePolicy),
            unavailableReason: null);
    }

    public static RuntimeResourcePolicyEffectiveStatus Unavailable(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An unavailability reason is required.", nameof(reason));
        }
        return new RuntimeResourcePolicyEffectiveStatus(null, null, reason.Trim());
    }
}

public interface IRuntimeResourcePolicyStatusSource
{
    ValueTask<RuntimeResourcePolicyEffectiveStatus> GetCurrentEffectivePolicyAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableRuntimeResourcePolicyStatusSource : IRuntimeResourcePolicyStatusSource
{
    public ValueTask<RuntimeResourcePolicyEffectiveStatus> GetCurrentEffectivePolicyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RuntimeResourcePolicyEffectiveStatus.Unavailable(
            "This Runtime version does not report its effective resource policy yet."));
    }
}
