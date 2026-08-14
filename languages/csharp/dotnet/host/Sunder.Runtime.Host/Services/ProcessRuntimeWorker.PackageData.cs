using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Storage;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ProcessRuntimeWorker
{
    private const int InlineDataUtf8Bytes = 64 * 1024;
    private static readonly UTF8Encoding StrictPayloadUtf8 = new(false, true);
    private readonly Dictionary<string, WorkerTransportPayload> _workerPayloadHandles = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<WorkerStorageMigrationPlanRule>> _declaredStorageMigrations =
        new Dictionary<string, IReadOnlyList<WorkerStorageMigrationPlanRule>>(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerStorageMigrationApplicationState> _storageMigrationStates =
        new(StringComparer.Ordinal);
    private IReadOnlyList<PackageStorageMigrationPlanIdentity> _storageMigrationPlans = [];
    private int _pendingWorkerPayloadHandles;
    private int _releasingWorkerPayloadHandles;

    private WorkerContributions ReadContributions(JsonElement contributions)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            contributions,
            "settingsSchema",
            "storageMigrations",
            "callbackHandlers",
            "authHandler",
            "rpcProviders",
            "candidateLifecycle",
            "generationLifecycle",
            "migrationPreparationLifecycle");
        var schemaValue = SunderWorkerProtocol.RequiredValue(contributions, "settingsSchema");
        var schema = schemaValue.ValueKind == JsonValueKind.Null
            ? null
            : ReadSettingsSchema(schemaValue);
        var storageMigrations = ReadStorageMigrationPlans(contributions);
        var callbackHandlerIds = ReadCallbackHandlerIds(contributions);
        var authHandler = ReadRequiredBoolean(contributions, "authHandler");
        RequireBoolean(contributions, "candidateLifecycle");
        RequireBoolean(contributions, "generationLifecycle");
        if (!ReadRequiredBoolean(contributions, "migrationPreparationLifecycle"))
        {
            throw new SunderWorkerProtocolException(
                "Process worker migrationPreparationLifecycle must be true.");
        }
        return new WorkerContributions(
            schema,
            SunderWorkerProtocol.RequiredValue(contributions, "rpcProviders"),
            callbackHandlerIds,
            authHandler,
            storageMigrations);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<WorkerStorageMigrationPlanRule>>
        ReadStorageMigrationPlans(JsonElement contributions)
    {
        var value = SunderWorkerProtocol.RequiredValue(contributions, "storageMigrations");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 2)
        {
            throw new SunderWorkerProtocolException(
                "Process worker storageMigrations must contain at most one state and one secrets plan.");
        }
        var plans = new Dictionary<string, IReadOnlyList<WorkerStorageMigrationPlanRule>>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            SunderWorkerProtocol.RequireOnlyProperties(item, "store", "rules");
            var store = ReadMigrationStore(item);
            var rulesValue = SunderWorkerProtocol.RequiredValue(item, "rules");
            if (rulesValue.ValueKind != JsonValueKind.Array
                || rulesValue.GetArrayLength() is 0 or > 32)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker storage migration plans must contain 1 to 32 rules.");
            }
            IReadOnlyList<WorkerStorageMigrationPlanRule> rules;
            try
            {
                rules = rulesValue.EnumerateArray().Select(ReadStorageMigrationPlanRule).ToArray();
            }
            catch (SunderWorkerProtocolException)
            {
                throw;
            }
            catch (ArgumentException exception)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker storage migration plan is invalid.",
                    exception);
            }
            if (!plans.TryAdd(store, rules))
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker declared more than one '{store}' storage migration plan.");
            }
        }
        return plans;
    }

    private static WorkerStorageMigrationPlanRule ReadStorageMigrationPlanRule(JsonElement rule)
    {
        SunderWorkerProtocol.RequireObject(rule, "worker storage migration plan rule");
        var kind = SunderWorkerProtocol.RequiredString(rule, "kind", 64);
        switch (kind)
        {
            case "exact":
            {
                SunderWorkerProtocol.RequireOnlyProperties(
                    rule,
                    "kind",
                    "legacyKey",
                    "destinationKey");
                var legacyKey = ReadMigrationString(rule, "legacyKey", allowEmpty: false);
                var destinationKey = ReadMigrationString(rule, "destinationKey", allowEmpty: false);
                _ = PackageStorageKeyMigration.Exact(legacyKey, destinationKey);
                return new WorkerStorageMigrationPlanRule(
                    kind,
                    legacyKey,
                    string.Empty,
                    destinationKey,
                    DestinationPrefix: null,
                    DestinationVersion: 0,
                    JsonIdentityProperty: null);
            }
            case "opaque-id":
            case "opaque-id-json":
            case "opaque-id-json-case-insensitive":
            {
                var hasIdentity = kind != "opaque-id";
                SunderWorkerProtocol.RequireOnlyProperties(
                    rule,
                    hasIdentity
                        ? [
                            "kind",
                            "legacyPrefix",
                            "legacySuffix",
                            "destinationPrefix",
                            "destinationVersion",
                            "identityProperty",
                        ]
                        : [
                            "kind",
                            "legacyPrefix",
                            "legacySuffix",
                            "destinationPrefix",
                            "destinationVersion",
                        ]);
                var legacyPrefix = ReadMigrationString(rule, "legacyPrefix", allowEmpty: false);
                var legacySuffix = ReadMigrationString(rule, "legacySuffix", allowEmpty: true);
                var destinationPrefix = ReadMigrationString(rule, "destinationPrefix", allowEmpty: false);
                var destinationVersionValue = SunderWorkerProtocol.RequiredNonNegativeInt64(
                    rule,
                    "destinationVersion");
                if (destinationVersionValue > int.MaxValue)
                {
                    throw new SunderWorkerProtocolException(
                        "Process worker migration destination version exceeds Int32.");
                }
                var destinationVersion = (int)destinationVersionValue;
                var identityProperty = hasIdentity
                    ? ReadMigrationString(rule, "identityProperty", allowEmpty: false)
                    : null;
                _ = kind switch
                {
                    "opaque-id" => PackageStorageKeyMigration.OpaqueId(
                        legacyPrefix,
                        legacySuffix,
                        destinationPrefix,
                        destinationVersion),
                    "opaque-id-json" => PackageStorageKeyMigration.OpaqueIdWithJsonIdentity(
                        legacyPrefix,
                        legacySuffix,
                        destinationPrefix,
                        destinationVersion,
                        identityProperty!),
                    _ => PackageStorageKeyMigration.OpaqueIdWithCaseInsensitiveJsonIdentity(
                        legacyPrefix,
                        legacySuffix,
                        destinationPrefix,
                        destinationVersion,
                        identityProperty!),
                };
                return new WorkerStorageMigrationPlanRule(
                    kind,
                    legacyPrefix,
                    legacySuffix,
                    DestinationKey: null,
                    destinationPrefix,
                    destinationVersion,
                    identityProperty);
            }
            case "dynamic-cleanup":
                SunderWorkerProtocol.RequireOnlyProperties(rule, "kind");
                return new WorkerStorageMigrationPlanRule(
                    kind,
                    LegacyPrefix: null,
                    LegacySuffix: null,
                    DestinationKey: null,
                    DestinationPrefix: null,
                    DestinationVersion: 0,
                    JsonIdentityProperty: null);
            default:
                throw new SunderWorkerProtocolException(
                    $"Process worker storage migration kind '{kind}' is unsupported.");
        }
    }

    private static IReadOnlyList<string> ReadCallbackHandlerIds(JsonElement contributions)
    {
        var value = SunderWorkerProtocol.RequiredValue(contributions, "callbackHandlers");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32)
        {
            throw new SunderWorkerProtocolException(
                "Process worker callbackHandlers must be an array containing at most 32 ids.");
        }
        var ids = value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new SunderWorkerProtocolException(
                    "Process worker callback handler ids must be strings."))
            .ToArray();
        if (ids.Any(static id => string.IsNullOrWhiteSpace(id)
                                 || id.Length > 128
                                 || id.Any(static character =>
                                     !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length
            || ids.Contains(PackageCallbackHandlerIds.Authentication, StringComparer.OrdinalIgnoreCase))
        {
            throw new SunderWorkerProtocolException(
                "Process worker callback handler ids must be unique safe tokens and must not use the reserved authentication id.");
        }
        return ids;
    }

    private void ConfigureStorageMigrationPlans(
        IReadOnlyDictionary<string, IReadOnlyList<WorkerStorageMigrationPlanRule>> plans)
    {
        lock (_stateGate)
        {
            if (_state != WorkerState.Starting)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker storage migrations were configured outside composition.");
            }
            _declaredStorageMigrations = plans;
            _storageMigrationPlans = plans
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => CreateStorageMigrationPlanIdentity(pair.Key, pair.Value))
                .ToArray();
            _storageMigrationStates.Clear();
            foreach (var store in plans.Keys)
            {
                _storageMigrationStates.Add(store, WorkerStorageMigrationApplicationState.Pending);
            }
        }
    }

    private static bool ReadRequiredBoolean(JsonElement root, string propertyName)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new SunderWorkerProtocolException(
                $"Process worker contribution property '{propertyName}' must be a boolean."),
        };
    }

    private static PackageSettingsSchema ReadSettingsSchema(JsonElement value)
    {
        try
        {
            SunderWorkerProtocol.RequireOnlyProperties(value, "summary", "sections");
            var sectionsValue = SunderWorkerProtocol.RequiredValue(value, "sections");
            if (sectionsValue.ValueKind != JsonValueKind.Array)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker settings schema sections must be an array.");
            }
            var sections = sectionsValue.EnumerateArray().Select(ReadSettingsSection).ToArray();
            return new PackageSettingsSchema(ReadNullableSchemaString(value, "summary"), sections);
        }
        catch (SunderWorkerProtocolException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            throw new SunderWorkerProtocolException(
                "Process worker settings schema is invalid.",
                exception);
        }
    }

    private static PackageSettingsSection ReadSettingsSection(JsonElement value)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            "sectionId",
            "title",
            "description",
            "fields");
        var fieldsValue = SunderWorkerProtocol.RequiredValue(value, "fields");
        if (fieldsValue.ValueKind != JsonValueKind.Array)
        {
            throw new SunderWorkerProtocolException(
                "Process worker settings schema fields must be an array.");
        }
        return new PackageSettingsSection(
            ReadSchemaString(value, "sectionId"),
            ReadSchemaString(value, "title"),
            ReadNullableSchemaString(value, "description"),
            fieldsValue.EnumerateArray().Select(ReadSettingsField).ToArray());
    }

    private static PackageSettingsField ReadSettingsField(JsonElement value)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            "key",
            "label",
            "kind",
            "description",
            "isRequired",
            "placeholder",
            "defaultValue",
            "options");
        var kind = ReadSchemaString(value, "kind") switch
        {
            "text" => PackageSettingsFieldKind.Text,
            "secret" => PackageSettingsFieldKind.Secret,
            "boolean" => PackageSettingsFieldKind.Boolean,
            "select" => PackageSettingsFieldKind.Select,
            _ => throw new SunderWorkerProtocolException(
                "Process worker settings schema field kind is invalid."),
        };
        var requiredValue = SunderWorkerProtocol.RequiredValue(value, "isRequired");
        var isRequired = requiredValue.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new SunderWorkerProtocolException(
                "Process worker settings schema isRequired must be a Boolean."),
        };
        var optionsValue = SunderWorkerProtocol.RequiredValue(value, "options");
        if (optionsValue.ValueKind != JsonValueKind.Array)
        {
            throw new SunderWorkerProtocolException(
                "Process worker settings schema options must be an array.");
        }
        return new PackageSettingsField(
            ReadSchemaString(value, "key"),
            ReadSchemaString(value, "label"),
            kind,
            ReadNullableSchemaString(value, "description"),
            isRequired,
            ReadNullableSchemaString(value, "placeholder"),
            ReadNullableSchemaString(value, "defaultValue"),
            optionsValue.EnumerateArray().Select(ReadSettingsOption).ToArray());
    }

    private static PackageSettingsOption ReadSettingsOption(JsonElement value)
    {
        SunderWorkerProtocol.RequireOnlyProperties(value, "value", "label");
        return new PackageSettingsOption(
            ReadSchemaString(value, "value"),
            ReadSchemaString(value, "label"));
    }

    private static string ReadSchemaString(JsonElement root, string propertyName)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new SunderWorkerProtocolException(
                $"Process worker settings schema property '{propertyName}' must be a string.");
        }
        return EnsureStrictSchemaString(value.GetString()!, propertyName);
    }

    private static string? ReadNullableSchemaString(JsonElement root, string propertyName)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new SunderWorkerProtocolException(
                $"Process worker settings schema property '{propertyName}' must be a string or null.");
        }
        return EnsureStrictSchemaString(value.GetString()!, propertyName);
    }

    private static string EnsureStrictSchemaString(string value, string propertyName)
    {
        try
        {
            _ = StrictPayloadUtf8.GetByteCount(value);
            return value;
        }
        catch (EncoderFallbackException exception)
        {
            throw new SunderWorkerProtocolException(
                $"Process worker settings schema property '{propertyName}' is not valid Unicode.",
                exception);
        }
    }

    private async Task<bool> TryRunPackageDataCallAsync(
        string type,
        JsonElement root,
        WorkerCall call)
    {
        using var lease = IsPackageDataCall(type)
            ? await AcquirePackageDataLeaseForCallAsync(type, root, call).ConfigureAwait(false)
            : null;
        using var leaseCancellation = lease?.CreateLinkedCancellation(
            call.Cancellation.Token,
            _hostStopping);
        var operationCancellation = leaseCancellation?.Token ?? call.Cancellation.Token;
        switch (type)
        {
            case "worker.payload-allocate":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "length");
                var length = SunderWorkerProtocol.RequiredNonNegativeInt64(root, "length");
                if (length > _policy.MaxWorkerFilePayloadBytes)
                {
                    throw new SunderWorkerProtocolException(
                        "Process worker payload allocation exceeds the configured limit.");
                }
                var payload = CreateWorkerUploadPayload(length, call.Cancellation.Token);
                var retained = false;
                try
                {
                    call.Cancellation.Token.ThrowIfCancellationRequested();
                    QueueEnvelope(new
                    {
                        type = "host.result",
                        id = call.Id,
                        value = new
                        {
                            handleId = payload.Id,
                            filePath = payload.FilePath,
                            length = payload.Length,
                        },
                    });
                    retained = true;
                }
                finally
                {
                    if (!retained) RemoveAndDeleteWorkerPayload(payload, call);
                }
                return true;
            }
            case "worker.payload-release":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "handleId");
                WorkerTransportPayload payload;
                try
                {
                    payload = RemoveWorkerPayload(ReadSafeId(root, "handleId"), call);
                }
                catch (SunderWorkerProtocolException) when (IsStopping())
                {
                    QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                    return true;
                }
                DisposeAndDeleteWorkerPayload(payload);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.settings-get":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "mode");
                var key = ReadDataString(root, "key");
                var result = ReadDataString(root, "mode") switch
                {
                    "effective" => await _packageData.Settings.GetValueAsync(
                        key,
                        operationCancellation).ConfigureAwait(false),
                    "stored" => await _packageData.Settings.GetStoredValueAsync(
                        key,
                        operationCancellation).ConfigureAwait(false),
                    _ => throw new SunderWorkerProtocolException(
                        "Process worker settings get mode is invalid."),
                };
                await QueueDataStringResultAsync(call, result, operationCancellation).ConfigureAwait(false);
                return true;
            }
            case "worker.migration-keys":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "store");
                var store = ReadMigrationStore(root);
                RequireStorageMigrationKeySnapshot(store);
                var keys = await ListPhysicalMigrationKeysAsync(
                    store,
                    operationCancellation).ConfigureAwait(false);
                if (keys.Count > 2048
                    || keys.Any(static key => key.Length is 0 or > 4096
                                              || !PackageStorageValidation.IsValidValue(key)))
                {
                    throw new InvalidOperationException(
                        "The package physical-key snapshot exceeds Worker V3 migration bounds.");
                }
                QueueEnvelope(new
                {
                    type = "host.result",
                    id = call.Id,
                    value = new { keys },
                });
                return true;
            }
            case "worker.migration-apply":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "store", "rules");
                var store = ReadMigrationStore(root);
                var migrations = ReadMigrationRules(
                    SunderWorkerProtocol.RequiredValue(root, "rules"),
                    out var expectedPhysicalKeys,
                    out var plan);
                BeginStorageMigrationApplication(store, plan);
                var identity = GetStorageMigrationPlanIdentity(store);
                if (!await _packageData.MigrationJournal.IsCompletedAsync(
                        identity,
                        operationCancellation).ConfigureAwait(false))
                {
                    await ApplyStorageMigrationsAsync(
                        store,
                        migrations,
                        expectedPhysicalKeys,
                        operationCancellation).ConfigureAwait(false);
                }
                CompleteStorageMigrationApplication(store);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.settings-set":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "value");
                var key = ReadDataString(root, "key");
                var value = await ReadWorkerDataValueAsync(
                    SunderWorkerProtocol.RequiredValue(root, "value"),
                    call).ConfigureAwait(false);
                await _packageData.Settings.SetValueAsync(
                    key,
                    value,
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.secrets-get":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                await QueueDataStringResultAsync(
                    call,
                    await _packageData.Secrets.GetSecretAsync(
                        ReadDataString(root, "key"),
                        operationCancellation).ConfigureAwait(false),
                    operationCancellation).ConfigureAwait(false);
                return true;
            }
            case "worker.secrets-set":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "value");
                var value = await ReadWorkerDataValueAsync(
                    SunderWorkerProtocol.RequiredValue(root, "value"),
                    call,
                    sensitive: true).ConfigureAwait(false);
                await _packageData.Secrets.SetSecretAsync(
                    ReadDataString(root, "key"),
                    value,
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.secrets-delete":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                await _packageData.Secrets.DeleteSecretAsync(
                    ReadDataString(root, "key"),
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.settings-delete":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                await _packageData.Settings.DeleteValueAsync(
                    ReadDataString(root, "key"),
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.state-get":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "mode");
                var key = ReadDataString(root, "key");
                switch (ReadDataString(root, "mode"))
                {
                    case "value":
                        await QueueDataStringResultAsync(
                            call,
                            await _packageData.Storage.State.GetValueAsync(
                                key,
                                operationCancellation).ConfigureAwait(false),
                            operationCancellation).ConfigureAwait(false);
                        return true;
                    case "contains":
                        var contains = await _packageData.Storage.State.ContainsKeyAsync(
                            key,
                            operationCancellation).ConfigureAwait(false);
                        QueueEnvelope(new { type = "host.result", id = call.Id, value = contains });
                        return true;
                    default:
                        throw new SunderWorkerProtocolException(
                            "Process worker state get mode is invalid.");
                }
            }
            case "worker.state-set":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "value");
                var key = ReadDataString(root, "key");
                var value = await ReadWorkerDataValueAsync(
                    SunderWorkerProtocol.RequiredValue(root, "value"),
                    call).ConfigureAwait(false);
                await _packageData.Storage.State.SetValueAsync(
                    key,
                    value,
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.state-delete":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                await _packageData.Storage.State.DeleteValueAsync(
                    ReadDataString(root, "key"),
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.state-list":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "prefix");
                var keys = await _packageData.Storage.State.ListKeysAsync(
                    ReadNullableDataString(root, "prefix"),
                    operationCancellation).ConfigureAwait(false);
                await QueueDataKeysResultAsync(call, keys, operationCancellation).ConfigureAwait(false);
                return true;
            }
            case "worker.files-read":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "relativePath");
                var contents = await _packageData.Storage.Files.ReadAsync(
                    ReadDataString(root, "relativePath"),
                    operationCancellation).ConfigureAwait(false);
                if (contents is null)
                {
                    QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                }
                else
                {
                    await QueuePayloadResultAsync(
                        call,
                        contents,
                        _policy.MaxWorkerFilePayloadBytes).ConfigureAwait(false);
                }
                return true;
            }
            case "worker.files-write":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "relativePath", "contents");
                var contents = await ReadWorkerPayloadBytesAsync(
                    SunderWorkerProtocol.RequiredValue(root, "contents"),
                    call,
                    _policy.MaxWorkerFilePayloadBytes).ConfigureAwait(false);
                await _packageData.Storage.Files.WriteAsync(
                    ReadDataString(root, "relativePath"),
                    contents,
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.files-delete":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "relativePath");
                await _packageData.Storage.Files.DeleteAsync(
                    ReadDataString(root, "relativePath"),
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            default:
                return false;
        }
    }

    private async Task QueueDataStringResultAsync(
        WorkerCall call,
        string? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null || StrictPayloadUtf8.GetByteCount(value) <= InlineDataUtf8Bytes)
        {
            QueueEnvelope(new
            {
                type = "host.result",
                id = call.Id,
                value = new { kind = "inline", value },
            });
            return;
        }
        var bytes = StrictPayloadUtf8.GetBytes(value);
        await QueuePayloadResultAsync(call, bytes, _policy.MaxWorkerPayloadBytes).ConfigureAwait(false);
    }

    private async Task QueueDataKeysResultAsync(
        WorkerCall call,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(keys);
        if (bytes.Length <= InlineDataUtf8Bytes)
        {
            QueueEnvelope(new
            {
                type = "host.result",
                id = call.Id,
                value = new { kind = "inline", keys },
            });
            return;
        }
        await QueuePayloadResultAsync(call, bytes, _policy.MaxWorkerPayloadBytes).ConfigureAwait(false);
    }

    private Task QueuePayloadResultAsync(WorkerCall call, byte[] bytes, int maximumPayloadBytes)
    {
        if (bytes.Length > maximumPayloadBytes)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.ResourceExhausted,
                "worker.payload.byte-limit",
                "The package-data result exceeds the worker transport payload limit."));
        }
        call.Cancellation.Token.ThrowIfCancellationRequested();
        var payload = CreateWorkerDownloadPayload(bytes, call.Cancellation.Token);
        var retained = false;
        try
        {
            call.Cancellation.Token.ThrowIfCancellationRequested();
            QueueEnvelope(new
            {
                type = "host.result",
                id = call.Id,
                value = new
                {
                    kind = "payload",
                    handleId = payload.Id,
                    filePath = payload.FilePath,
                    length = payload.Length,
                    sha256 = payload.Sha256,
                },
            });
            retained = true;
            return Task.CompletedTask;
        }
        finally
        {
            if (!retained) RemoveAndDeleteWorkerPayload(payload, call);
        }
    }

    private WorkerTransportPayload CreateWorkerUploadPayload(long length, CancellationToken cancellationToken)
    {
        ReserveWorkerPayloadHandle();
        WorkerTransportPayload? payload = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (id, filePath, stream) = CreatePayloadFile();
            payload = new WorkerTransportPayload(
                id,
                filePath,
                length,
                null,
                WorkerPayloadDirection.Upload,
                stream);
            AddWorkerPayload(payload);
            return payload;
        }
        catch
        {
            if (payload is not null) DisposeAndDeleteWorkerPayload(payload);
            ReleasePendingWorkerPayloadHandle();
            throw;
        }
    }

    private WorkerTransportPayload CreateWorkerDownloadPayload(byte[] bytes, CancellationToken cancellationToken)
    {
        ReserveWorkerPayloadHandle();
        WorkerTransportPayload? payload = null;
        string? filePath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allocation = CreatePayloadFile();
            var id = allocation.Id;
            filePath = allocation.FilePath;
            using (allocation.Stream)
            {
                allocation.Stream.Write(bytes);
                allocation.Stream.Flush(flushToDisk: true);
            }
            payload = new WorkerTransportPayload(
                id,
                filePath,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                WorkerPayloadDirection.Download,
                Stream: null);
            AddWorkerPayload(payload);
            return payload;
        }
        catch
        {
            if (filePath is not null) TryDeleteFile(filePath);
            ReleasePendingWorkerPayloadHandle();
            throw;
        }
    }

    private (string Id, string FilePath, FileStream Stream) CreatePayloadFile()
    {
        var directory = PackageWorkspacePath.Resolve(_workerTemporaryPath, "payload");
        Directory.CreateDirectory(directory);
        RestrictWorkerDirectory(directory);
        directory = PackageWorkspacePath.Resolve(_workerTemporaryPath, "payload");
        string id;
        string filePath;
        do
        {
            id = "worker-payload-" + Guid.NewGuid().ToString("N");
            filePath = Path.Combine(directory, id + ".data");
        }
        while (File.Exists(filePath));
        var stream = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return (id, filePath, stream);
    }

    private async Task<string> ReadWorkerDataValueAsync(
        JsonElement value,
        WorkerCall call,
        bool sensitive = false)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            value,
            ReadDataString(value, "kind") == "inline"
                ? ["kind", "value"]
                : ["kind", "handleId", "length", "sha256"]);
        var kind = ReadDataString(value, "kind");
        if (kind == "inline")
        {
            return ReadInlineDataValue(value);
        }
        if (kind != "payload")
        {
            throw new SunderWorkerProtocolException(
                "Process worker package-data value kind is invalid.");
        }

        var bytes = await ReadWorkerPayloadBytesAsync(
            value,
            call,
            _policy.MaxWorkerPayloadBytes).ConfigureAwait(false);
        try
        {
            return StrictPayloadUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SunderWorkerProtocolException(
                "Process worker package-data payload is not strict UTF-8.",
                exception);
        }
        finally
        {
            if (sensitive) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<byte[]> ReadWorkerPayloadBytesAsync(
        JsonElement value,
        WorkerCall call,
        int maximumPayloadBytes)
    {
        SunderWorkerProtocol.RequireOnlyProperties(value, "kind", "handleId", "length", "sha256");
        if (ReadDataString(value, "kind") != "payload")
        {
            throw new SunderWorkerProtocolException(
                "Process worker binary package-data value must use a payload.");
        }

        var handleId = ReadSafeId(value, "handleId");
        var length = SunderWorkerProtocol.RequiredNonNegativeInt64(value, "length");
        if (length > maximumPayloadBytes)
        {
            throw new SunderWorkerProtocolException(
                "Process worker package-data payload exceeds the configured limit.");
        }
        var sha256 = SunderWorkerProtocol.RequiredString(value, "sha256", 64);
        if (sha256.Length != 64 || sha256.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new SunderWorkerProtocolException(
                "Process worker package-data payload hash is invalid.");
        }
        var payload = RemoveWorkerPayload(handleId, call, WorkerPayloadDirection.Upload);
        try
        {
            if (length != payload.Length)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker package-data payload length does not match its allocation.");
            }
            var stream = payload.Stream
                         ?? throw new SunderWorkerProtocolException(
                             "Process worker package-data upload payload has no retained file handle.");
            stream.Position = 0;
            if (stream.Length != length)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker package-data payload file length is invalid.");
            }
            var bytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            var expected = Convert.FromHexString(sha256);
            var actual = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new SunderWorkerProtocolException(
                    "Process worker package-data payload hash does not match its descriptor.");
            }
            return bytes;
        }
        catch (SunderWorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or EndOfStreamException
                or OverflowException
                or FormatException)
        {
            throw new SunderWorkerProtocolException(
                "Process worker package-data payload file is malformed or unavailable.",
                exception);
        }
        finally
        {
            DisposeAndDeleteWorkerPayload(payload);
        }
    }

    private void ReserveWorkerPayloadHandle()
    {
        lock (_callGate)
        {
            if (_workerPayloadHandles.Count
                + _pendingWorkerPayloadHandles
                + _releasingWorkerPayloadHandles
                >= _policy.MaxWorkerPayloadHandles)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.ResourceExhausted,
                    "worker.payload.handle-limit",
                    "The process worker transport payload-handle limit was reached."));
            }
            _pendingWorkerPayloadHandles++;
        }
    }

    private void AddWorkerPayload(WorkerTransportPayload payload)
    {
        lock (_callGate)
        {
            _pendingWorkerPayloadHandles--;
            if (!_workerPayloadHandles.TryAdd(payload.Id, payload))
            {
                throw new SunderWorkerProtocolException(
                    $"Host generated duplicate worker payload handle '{payload.Id}'.");
            }
        }
    }

    private void ReleasePendingWorkerPayloadHandle()
    {
        lock (_callGate)
        {
            if (_pendingWorkerPayloadHandles > 0) _pendingWorkerPayloadHandles--;
        }
    }

    private WorkerTransportPayload RemoveWorkerPayload(
        string handleId,
        WorkerCall call,
        WorkerPayloadDirection? direction = null)
    {
        lock (_callGate)
        {
            if (!_workerPayloadHandles.TryGetValue(handleId, out var payload)
                || direction is not null && payload.Direction != direction)
            {
                throw new SunderWorkerProtocolException(
                    $"Process worker used unknown or invalid payload handle '{handleId}'.");
            }
            _workerPayloadHandles.Remove(handleId);
            _releasingWorkerPayloadHandles++;
            call.ReleasesPayloadHandle = true;
            return payload;
        }
    }

    private void RemoveAndDeleteWorkerPayload(WorkerTransportPayload payload, WorkerCall call)
    {
        lock (_callGate)
        {
            if (_workerPayloadHandles.TryGetValue(payload.Id, out var registered)
                && ReferenceEquals(registered, payload))
            {
                _workerPayloadHandles.Remove(payload.Id);
                _releasingWorkerPayloadHandles++;
                call.ReleasesPayloadHandle = true;
            }
        }
        DisposeAndDeleteWorkerPayload(payload);
    }

    private void CleanupAllWorkerPayloads()
    {
        WorkerTransportPayload[] payloads;
        lock (_callGate)
        {
            payloads = _workerPayloadHandles.Values.ToArray();
            _workerPayloadHandles.Clear();
        }
        foreach (var payload in payloads) DisposeAndDeleteWorkerPayload(payload);
    }

    private static string ReadDataString(JsonElement root, string propertyName)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new SunderWorkerProtocolException(
                $"Process worker package-data property '{propertyName}' must be a string.");
        }
        return value.GetString()!;
    }

    private static string? ReadNullableDataString(JsonElement root, string propertyName)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new SunderWorkerProtocolException(
                $"Process worker package-data property '{propertyName}' must be a string or null.");
        }
        return value.GetString();
    }

    private static string ReadMigrationStore(JsonElement root)
        => SunderWorkerProtocol.RequiredString(root, "store", 16) switch
        {
            "state" => "state",
            "secrets" => "secrets",
            _ => throw new SunderWorkerProtocolException(
                "Process worker migration store must be 'state' or 'secrets'."),
        };

    private void RequireStorageMigrationKeySnapshot(string store)
    {
        lock (_stateGate)
        {
            if (_state != WorkerState.Activating
                || !_declaredStorageMigrations.TryGetValue(store, out var plan)
                || !_storageMigrationStates.TryGetValue(
                    store,
                    out var state)
                || state != WorkerStorageMigrationApplicationState.Pending
                || !plan.Any(static rule => rule.Kind == "dynamic-cleanup"))
            {
                throw new SunderWorkerProtocolException(
                    "Process worker requested an undeclared or out-of-order migration key snapshot.");
            }
        }
    }

    private void BeginStorageMigrationApplication(
        string store,
        IReadOnlyList<WorkerStorageMigrationPlanRule> plan)
    {
        lock (_stateGate)
        {
            if (_state != WorkerState.Activating
                || !_declaredStorageMigrations.TryGetValue(store, out var declared)
                || !declared.SequenceEqual(plan)
                || !_storageMigrationStates.TryGetValue(
                    store,
                    out var state)
                || state != WorkerStorageMigrationApplicationState.Pending)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker attempted an undeclared, duplicate, or out-of-order storage migration.");
            }
            _storageMigrationStates[store] = WorkerStorageMigrationApplicationState.Applying;
        }
    }

    private void CompleteStorageMigrationApplication(string store)
    {
        lock (_stateGate)
        {
            if (!_storageMigrationStates.TryGetValue(
                    store,
                    out var state)
                || state != WorkerStorageMigrationApplicationState.Applying)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker storage migration application state changed unexpectedly.");
            }
            _storageMigrationStates[store] = WorkerStorageMigrationApplicationState.Applied;
        }
    }

    private bool AreStorageMigrationsAppliedUnderStateGate()
        => _storageMigrationStates.Values.All(static state =>
            state == WorkerStorageMigrationApplicationState.Applied);

    private PackageStorageMigrationPlanIdentity GetStorageMigrationPlanIdentity(string store)
    {
        lock (_stateGate)
        {
            return _storageMigrationPlans.Single(plan => string.Equals(
                plan.Store,
                store,
                StringComparison.Ordinal));
        }
    }

    private PackageStorageMigrationPlanIdentity CreateStorageMigrationPlanIdentity(
        string store,
        IReadOnlyList<WorkerStorageMigrationPlanRule> rules)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("store", store);
            writer.WritePropertyName("rules");
            writer.WriteStartArray();
            foreach (var rule in rules)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", rule.Kind);
                if (rule.LegacyPrefix is null) writer.WriteNull("legacyPrefix");
                else writer.WriteString("legacyPrefix", rule.LegacyPrefix);
                if (rule.LegacySuffix is null) writer.WriteNull("legacySuffix");
                else writer.WriteString("legacySuffix", rule.LegacySuffix);
                if (rule.DestinationKey is null) writer.WriteNull("destinationKey");
                else writer.WriteString("destinationKey", rule.DestinationKey);
                if (rule.DestinationPrefix is null) writer.WriteNull("destinationPrefix");
                else writer.WriteString("destinationPrefix", rule.DestinationPrefix);
                writer.WriteNumber("destinationVersion", rule.DestinationVersion);
                if (rule.JsonIdentityProperty is null) writer.WriteNull("jsonIdentityProperty");
                else writer.WriteString("jsonIdentityProperty", rule.JsonIdentityProperty);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var planSha256 = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
        return new PackageStorageMigrationPlanIdentity(
            store,
            $"{store}:{_package.Version}",
            planSha256);
    }

    private Task<IReadOnlyList<string>> ListPhysicalMigrationKeysAsync(
        string store,
        CancellationToken cancellationToken)
        => store == "state"
            ? ((JsonPackageKeyValueStore)_packageData.Storage.State)
                .ListPhysicalKeysForMigrationAsync(cancellationToken)
            : _packageData.Secrets.ListPhysicalKeysForMigrationAsync(cancellationToken);

    private Task ApplyStorageMigrationsAsync(
        string store,
        IReadOnlyList<PackageStorageKeyMigration> migrations,
        IReadOnlyList<string>? expectedPhysicalKeys,
        CancellationToken cancellationToken)
    {
        if (store == "state")
        {
            var state = (JsonPackageKeyValueStore)_packageData.Storage.State;
            return expectedPhysicalKeys is null
                ? state.MigrateKeysAsync(migrations, cancellationToken)
                : state.MigrateKeysAsync(migrations, expectedPhysicalKeys, cancellationToken);
        }
        return expectedPhysicalKeys is null
            ? _packageData.Secrets.MigrateKeysAsync(migrations, cancellationToken)
            : _packageData.Secrets.MigrateKeysAsync(
                migrations,
                expectedPhysicalKeys,
                cancellationToken);
    }

    private static IReadOnlyList<PackageStorageKeyMigration> ReadMigrationRules(
        JsonElement value,
        out IReadOnlyList<string>? expectedPhysicalKeys,
        out IReadOnlyList<WorkerStorageMigrationPlanRule> plan)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > 32)
        {
            throw new SunderWorkerProtocolException(
                "Process worker migration rules must contain 1 to 32 entries.");
        }
        var migrations = new List<PackageStorageKeyMigration>();
        var planRules = new List<WorkerStorageMigrationPlanRule>();
        expectedPhysicalKeys = null;
        foreach (var rule in value.EnumerateArray())
        {
            SunderWorkerProtocol.RequireObject(rule, "worker migration rule");
            var kind = SunderWorkerProtocol.RequiredString(rule, "kind", 64);
            switch (kind)
            {
                case "exact":
                    SunderWorkerProtocol.RequireOnlyProperties(
                        rule,
                        "kind",
                        "legacyKey",
                        "destinationKey");
                    migrations.Add(PackageStorageKeyMigration.Exact(
                        ReadMigrationString(rule, "legacyKey", allowEmpty: false),
                        ReadMigrationString(rule, "destinationKey", allowEmpty: false)));
                    planRules.Add(ReadStorageMigrationPlanRule(rule));
                    break;
                case "opaque-id":
                case "opaque-id-json":
                case "opaque-id-json-case-insensitive":
                {
                    var hasIdentity = kind != "opaque-id";
                    SunderWorkerProtocol.RequireOnlyProperties(
                        rule,
                        hasIdentity
                            ? [
                                "kind",
                                "legacyPrefix",
                                "legacySuffix",
                                "destinationPrefix",
                                "destinationVersion",
                                "identityProperty",
                            ]
                            : [
                                "kind",
                                "legacyPrefix",
                                "legacySuffix",
                                "destinationPrefix",
                                "destinationVersion",
                            ]);
                    var legacyPrefix = ReadMigrationString(rule, "legacyPrefix", allowEmpty: false);
                    var legacySuffix = ReadMigrationString(rule, "legacySuffix", allowEmpty: true);
                    var destinationPrefix = ReadMigrationString(rule, "destinationPrefix", allowEmpty: false);
                    var destinationVersionValue = SunderWorkerProtocol.RequiredNonNegativeInt64(
                        rule,
                        "destinationVersion");
                    if (destinationVersionValue > int.MaxValue)
                    {
                        throw new SunderWorkerProtocolException(
                            "Process worker migration destination version exceeds Int32.");
                    }
                    var destinationVersion = (int)destinationVersionValue;
                    migrations.Add(kind switch
                    {
                        "opaque-id" => PackageStorageKeyMigration.OpaqueId(
                            legacyPrefix,
                            legacySuffix,
                            destinationPrefix,
                            destinationVersion),
                        "opaque-id-json" => PackageStorageKeyMigration.OpaqueIdWithJsonIdentity(
                            legacyPrefix,
                            legacySuffix,
                            destinationPrefix,
                            destinationVersion,
                            ReadMigrationString(rule, "identityProperty", allowEmpty: false)),
                        _ => PackageStorageKeyMigration.OpaqueIdWithCaseInsensitiveJsonIdentity(
                            legacyPrefix,
                            legacySuffix,
                            destinationPrefix,
                            destinationVersion,
                            ReadMigrationString(rule, "identityProperty", allowEmpty: false)),
                    });
                    planRules.Add(new WorkerStorageMigrationPlanRule(
                        kind,
                        legacyPrefix,
                        legacySuffix,
                        DestinationKey: null,
                        destinationPrefix,
                        destinationVersion,
                        hasIdentity
                            ? ReadMigrationString(rule, "identityProperty", allowEmpty: false)
                            : null));
                    break;
                }
                case "dynamic-cleanup":
                {
                    SunderWorkerProtocol.RequireOnlyProperties(rule, "kind", "actions");
                    var actions = ReadDynamicMigrationActions(
                        SunderWorkerProtocol.RequiredValue(rule, "actions"),
                        out var physicalKeys);
                    if (expectedPhysicalKeys is not null
                        && !expectedPhysicalKeys.SequenceEqual(physicalKeys, StringComparer.Ordinal))
                    {
                        throw new SunderWorkerProtocolException(
                            "Process worker dynamic migration rules must resolve one identical physical-key snapshot.");
                    }
                    expectedPhysicalKeys ??= physicalKeys;
                    migrations.Add(PackageStorageKeyMigration.DynamicCleanup(key =>
                        actions.TryGetValue(key, out var action)
                            ? action
                            : PackageStorageKeyMigrationAction.NoMatch));
                    planRules.Add(new WorkerStorageMigrationPlanRule(
                        kind,
                        LegacyPrefix: null,
                        LegacySuffix: null,
                        DestinationKey: null,
                        DestinationPrefix: null,
                        DestinationVersion: 0,
                        JsonIdentityProperty: null));
                    break;
                }
                default:
                    throw new SunderWorkerProtocolException(
                        $"Process worker migration kind '{kind}' is unsupported.");
            }
        }
        plan = planRules;
        return migrations;
    }

    private static IReadOnlyDictionary<string, PackageStorageKeyMigrationAction> ReadDynamicMigrationActions(
        JsonElement value,
        out IReadOnlyList<string> physicalKeys)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 2048)
        {
            throw new SunderWorkerProtocolException(
                "Process worker dynamic migration actions must contain at most 2048 entries.");
        }
        var actions = new Dictionary<string, PackageStorageKeyMigrationAction>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            SunderWorkerProtocol.RequireOnlyProperties(
                item,
                "key",
                "kind",
                "destinationKey",
                "precedence");
            var key = ReadMigrationString(item, "key", allowEmpty: false);
            var kind = SunderWorkerProtocol.RequiredString(item, "kind", 32);
            var destination = ReadNullableMigrationString(item, "destinationKey");
            var precedenceValue = SunderWorkerProtocol.RequiredNonNegativeInt64(item, "precedence");
            if (precedenceValue > int.MaxValue)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker migration precedence exceeds Int32.");
            }
            var action = kind switch
            {
                "no-match" when destination is null && precedenceValue == 0
                    => PackageStorageKeyMigrationAction.NoMatch,
                "delete" when destination is null && precedenceValue == 0
                    => PackageStorageKeyMigrationAction.Delete,
                "rewrite" when destination is not null
                    => PackageStorageKeyMigrationAction.Rewrite(destination, (int)precedenceValue),
                _ => throw new SunderWorkerProtocolException(
                    "Process worker dynamic migration action is inconsistent."),
            };
            if (!actions.TryAdd(key, action))
            {
                throw new SunderWorkerProtocolException(
                    "Process worker dynamic migration action keys must be unique.");
            }
        }
        physicalKeys = actions.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!actions.Keys.SequenceEqual(physicalKeys, StringComparer.Ordinal))
        {
            throw new SunderWorkerProtocolException(
                "Process worker dynamic migration actions must be ordinally sorted.");
        }
        return actions;
    }

    private static string ReadMigrationString(
        JsonElement root,
        string propertyName,
        bool allowEmpty)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        if (value.ValueKind != JsonValueKind.String
            || !allowEmpty && value.GetString()!.Length == 0
            || value.GetString()!.Length > 4096
            || !PackageStorageValidation.IsValidValue(value.GetString()!))
        {
            throw new SunderWorkerProtocolException(
                $"Process worker migration property '{propertyName}' is invalid or oversized.");
        }
        return value.GetString()!;
    }

    private static string? ReadNullableMigrationString(JsonElement root, string propertyName)
    {
        var value = SunderWorkerProtocol.RequiredValue(root, propertyName);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : ReadMigrationString(root, propertyName, allowEmpty: false);
    }

    private static bool IsPackageDataCall(string type)
        => type.StartsWith("worker.settings-", StringComparison.Ordinal)
           || type.StartsWith("worker.state-", StringComparison.Ordinal)
           || type.StartsWith("worker.secrets-", StringComparison.Ordinal)
           || type.StartsWith("worker.files-", StringComparison.Ordinal)
           || type.StartsWith("worker.migration-", StringComparison.Ordinal);

    private PackageSessionLease? AcquirePackageDataLease()
    {
        if (_packageSessionState is null) return null;
        PackageSessionLease lease;
        try
        {
            long sessionGeneration;
            lock (_stateGate)
            {
                if (_sessionGeneration is null)
                {
                    throw new RuntimeUnavailableException(
                        "The process worker has no committed Runtime generation.");
                }
                sessionGeneration = _sessionGeneration.Value;
            }
            lease = _packageSessionState.AcquirePackageActivationLease(
                _package.PackageId,
                new RuntimePackageGeneration(_activationId, sessionGeneration));
        }
        catch (RuntimeUnavailableException)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Unavailable,
                "worker.activation.unavailable",
                "The process worker activation is stale or unavailable."));
        }
        if (!lease.Session.OwnsPackageActivation(_package.PackageId, _activationId))
        {
            lease.Dispose();
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Unavailable,
                "worker.activation.unavailable",
                "The process worker activation is stale or unavailable."));
        }
        return lease;
    }

    private async ValueTask<PackageSessionLease?> AcquirePackageDataLeaseForCallAsync(
        string type,
        JsonElement root,
        WorkerCall call)
    {
        ValidatePackageDataEnvelope(type, root);
        try
        {
            return AcquirePackageDataLease();
        }
        catch
        {
            if (type is "worker.settings-set" or "worker.state-set" or "worker.secrets-set")
            {
                _ = await ReadWorkerDataValueAsync(
                    SunderWorkerProtocol.RequiredValue(root, "value"),
                    call,
                    sensitive: type == "worker.secrets-set").ConfigureAwait(false);
            }
            else if (type == "worker.files-write")
            {
                _ = await ReadWorkerPayloadBytesAsync(
                    SunderWorkerProtocol.RequiredValue(root, "contents"),
                    call,
                    _policy.MaxWorkerFilePayloadBytes).ConfigureAwait(false);
            }
            throw;
        }
    }

    private void ValidatePackageDataEnvelope(string type, JsonElement root)
    {
        switch (type)
        {
            case "worker.settings-get":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "mode");
                _ = ReadDataString(root, "key");
                if (ReadDataString(root, "mode") is not ("effective" or "stored"))
                {
                    throw new SunderWorkerProtocolException(
                        "Process worker settings get mode is invalid.");
                }
                return;
            }
            case "worker.settings-set":
            case "worker.state-set":
            case "worker.secrets-set":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "value");
                _ = ReadDataString(root, "key");
                ValidateWorkerDataValueEnvelope(
                    SunderWorkerProtocol.RequiredValue(root, "value"),
                    _policy.MaxWorkerPayloadBytes);
                return;
            }
            case "worker.settings-delete":
            case "worker.state-delete":
            case "worker.secrets-get":
            case "worker.secrets-delete":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                _ = ReadDataString(root, "key");
                return;
            }
            case "worker.state-get":
            {
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "mode");
                _ = ReadDataString(root, "key");
                if (ReadDataString(root, "mode") is not ("value" or "contains"))
                {
                    throw new SunderWorkerProtocolException(
                        "Process worker state get mode is invalid.");
                }
                return;
            }
            case "worker.state-list":
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "prefix");
                _ = ReadNullableDataString(root, "prefix");
                return;
            case "worker.migration-keys":
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "store");
                _ = ReadMigrationStore(root);
                return;
            case "worker.migration-apply":
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "store", "rules");
                _ = ReadMigrationStore(root);
                _ = ReadMigrationRules(
                    SunderWorkerProtocol.RequiredValue(root, "rules"),
                    out _,
                    out _);
                return;
            case "worker.files-read":
            case "worker.files-delete":
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "relativePath");
                _ = ReadDataString(root, "relativePath");
                return;
            case "worker.files-write":
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "relativePath", "contents");
                _ = ReadDataString(root, "relativePath");
                ValidateWorkerPayloadEnvelope(
                    SunderWorkerProtocol.RequiredValue(root, "contents"),
                    _policy.MaxWorkerFilePayloadBytes);
                return;
        }
    }

    private void ValidateWorkerDataValueEnvelope(JsonElement value, int maximumPayloadBytes)
    {
        var kind = ReadDataString(value, "kind");
        if (kind == "inline")
        {
            SunderWorkerProtocol.RequireOnlyProperties(value, "kind", "value");
            _ = ReadInlineDataValue(value);
            return;
        }
        if (kind != "payload")
        {
            throw new SunderWorkerProtocolException(
                "Process worker package-data value kind is invalid.");
        }

        ValidateWorkerPayloadEnvelope(value, maximumPayloadBytes);
    }

    private static void ValidateWorkerPayloadEnvelope(JsonElement value, int maximumPayloadBytes)
    {
        SunderWorkerProtocol.RequireOnlyProperties(value, "kind", "handleId", "length", "sha256");
        if (ReadDataString(value, "kind") != "payload")
        {
            throw new SunderWorkerProtocolException(
                "Process worker binary package-data value must use a payload.");
        }
        _ = ReadSafeId(value, "handleId");
        var length = SunderWorkerProtocol.RequiredNonNegativeInt64(value, "length");
        if (length > maximumPayloadBytes)
        {
            throw new SunderWorkerProtocolException(
                "Process worker package-data payload exceeds the configured limit.");
        }
        var sha256 = SunderWorkerProtocol.RequiredString(value, "sha256", 64);
        if (sha256.Length != 64 || sha256.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new SunderWorkerProtocolException(
                "Process worker package-data payload hash is invalid.");
        }
    }

    private static string ReadInlineDataValue(JsonElement value)
    {
        var inline = SunderWorkerProtocol.RequiredValue(value, "value");
        if (inline.ValueKind != JsonValueKind.String)
        {
            throw new SunderWorkerProtocolException(
                "Process worker inline package-data value must be a string.");
        }
        var result = inline.GetString()!;
        try
        {
            if (StrictPayloadUtf8.GetByteCount(result) > InlineDataUtf8Bytes)
            {
                throw new SunderWorkerProtocolException(
                    "Process worker inline package-data value exceeds the inline transport limit.");
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new SunderWorkerProtocolException(
                "Process worker inline package-data value is not valid Unicode.",
                exception);
        }
        return result;
    }

    private static void DisposeAndDeleteWorkerPayload(WorkerTransportPayload payload)
    {
        try
        {
            payload.Stream?.Dispose();
        }
        catch
        {
        }
        TryDeleteFile(payload.FilePath);
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record WorkerContributions(
        PackageSettingsSchema? SettingsSchema,
        JsonElement RpcProviders,
        IReadOnlyList<string> CallbackHandlerIds,
        bool HasAuthHandler,
        IReadOnlyDictionary<string, IReadOnlyList<WorkerStorageMigrationPlanRule>> StorageMigrations);

    private sealed record WorkerStorageMigrationPlanRule(
        string Kind,
        string? LegacyPrefix,
        string? LegacySuffix,
        string? DestinationKey,
        string? DestinationPrefix,
        int DestinationVersion,
        string? JsonIdentityProperty);

    private enum WorkerStorageMigrationApplicationState
    {
        Pending,
        Applying,
        Applied,
    }

    private sealed record WorkerTransportPayload(
        string Id,
        string FilePath,
        long Length,
        string? Sha256,
        WorkerPayloadDirection Direction,
        FileStream? Stream);

    private enum WorkerPayloadDirection
    {
        Upload,
        Download,
    }
}
