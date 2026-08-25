using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient
{
    private static readonly UTF8Encoding StrictDataUtf8 = new(false, true);

    internal Task<string?> GetSettingAsync(
        string key,
        bool storedOnly,
        CancellationToken cancellationToken)
        => GetDataStringAsync(
            "worker.settings-get",
            key,
            storedOnly ? "stored" : "effective",
            cancellationToken,
            sensitive: false);

    internal Task SetSettingAsync(string key, string value, CancellationToken cancellationToken)
        => SetDataStringAsync("worker.settings-set", key, value, cancellationToken, sensitive: false);

    internal Task DeleteSettingAsync(string key, CancellationToken cancellationToken)
        => DeleteDataAsync("worker.settings-delete", key, cancellationToken);

    internal Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken)
        => GetDataStringAsync(
            "worker.secrets-get",
            key,
            null,
            cancellationToken,
            sensitive: true);

    internal Task SetSecretAsync(string key, string value, CancellationToken cancellationToken)
        => SetDataStringAsync("worker.secrets-set", key, value, cancellationToken, sensitive: true);

    internal Task DeleteSecretAsync(string key, CancellationToken cancellationToken)
        => DeleteDataAsync("worker.secrets-delete", key, cancellationToken);

    internal Task<string?> GetStateAsync(string key, CancellationToken cancellationToken)
        => GetDataStringAsync("worker.state-get", key, "value", cancellationToken, sensitive: false);

    internal Task<string?> GetStateForMigrationPreparationAsync(
        string key,
        CancellationToken cancellationToken)
        => GetDataStringAsync(
            "worker.state-get",
            key,
            "value",
            cancellationToken,
            sensitive: false,
            migrationPreparationRead: true);

    internal async Task<bool> ContainsStateKeyAsync(string key, CancellationToken cancellationToken)
    {
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.state-get");
                writer.WriteString("id", id);
                writer.WriteString("key", key);
                writer.WriteString("mode", "contains");
                writer.WriteEndObject();
            }),
            cancellationToken).ConfigureAwait(false);
        return ParseHostValue(value, static result =>
        {
            if (result.ValueKind is JsonValueKind.True or JsonValueKind.False) return result.GetBoolean();
            throw new WorkerProtocolException("Host package-state contains result must be a Boolean.");
        });
    }

    internal async Task<bool> ContainsStateKeyForMigrationPreparationAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.state-get");
                writer.WriteString("id", id);
                writer.WriteString("key", key);
                writer.WriteString("mode", "contains");
                writer.WriteEndObject();
            }),
            cancellationToken,
            migrationPreparationRead: true).ConfigureAwait(false);
        return ParseHostValue(value, static result =>
        {
            if (result.ValueKind is JsonValueKind.True or JsonValueKind.False) return result.GetBoolean();
            throw new WorkerProtocolException("Host package-state contains result must be a Boolean.");
        });
    }

    internal Task SetStateAsync(string key, string value, CancellationToken cancellationToken)
        => SetDataStringAsync("worker.state-set", key, value, cancellationToken, sensitive: false);

    internal Task DeleteStateAsync(string key, CancellationToken cancellationToken)
        => DeleteDataAsync("worker.state-delete", key, cancellationToken);

    internal async Task MigrateStorageKeysAsync(
        string store,
        IReadOnlyList<PackageStorageKeyMigration> migrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0 || migrations.Any(static migration => migration is null))
        {
            throw new ArgumentException(
                "At least one non-null package storage key migration is required.",
                nameof(migrations));
        }
        if (migrations.Count > 32)
        {
            throw new ArgumentException("A worker storage migration cannot contain more than 32 rules.", nameof(migrations));
        }
        var descriptors = migrations.Select(static migration => migration.Descriptor).ToArray();
        IReadOnlyList<string> physicalKeys = [];
        if (descriptors.Any(static descriptor =>
                descriptor.Kind == PackageStorageKeyMigrationDescriptorKind.DynamicCleanup))
        {
            physicalKeys = await ListPhysicalMigrationKeysAsync(store, cancellationToken).ConfigureAwait(false);
        }
        var dynamicActionCount = checked(physicalKeys.Count
                                         * descriptors.Count(static descriptor =>
                                             descriptor.Kind == PackageStorageKeyMigrationDescriptorKind.DynamicCleanup));
        if (dynamicActionCount > 4096)
        {
            throw new InvalidOperationException(
                "The worker storage migration exceeds the bounded dynamic-action limit.");
        }

        await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.migration-apply");
                writer.WriteString("id", id);
                writer.WriteString("store", store);
                writer.WritePropertyName("rules");
                writer.WriteStartArray();
                for (var index = 0; index < descriptors.Length; index++)
                {
                    WriteMigrationRule(writer, migrations[index], descriptors[index], physicalKeys);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ListPhysicalMigrationKeysAsync(
        string store,
        CancellationToken cancellationToken)
    {
        var value = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.migration-keys");
                writer.WriteString("id", id);
                writer.WriteString("store", store);
                writer.WriteEndObject();
            }),
            cancellationToken).ConfigureAwait(false);
        return ParseHostValue(value, static result =>
        {
            WorkerJson.RequireOnlyProperties(result, "keys");
            var keys = WorkerJson.RequiredValue(result, "keys");
            if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() > 2048)
            {
                throw new WorkerProtocolException(
                    "Host migration key snapshot must contain at most 2048 keys.");
            }
            var returned = keys.EnumerateArray().Select(static item =>
            {
                if (item.ValueKind != JsonValueKind.String
                    || item.GetString()!.Length is 0 or > 4096)
                {
                    throw new WorkerProtocolException(
                        "Host migration key snapshots contain only bounded non-empty strings.");
                }
                return item.GetString()!;
            }).ToArray();
            if (returned.Distinct(StringComparer.Ordinal).Count() != returned.Length
                || !returned.SequenceEqual(returned.Order(StringComparer.Ordinal)))
            {
                throw new WorkerProtocolException(
                    "Host migration key snapshot must be unique and ordinally sorted.");
            }
            return (IReadOnlyList<string>)returned;
        });
    }

    private static void WriteMigrationRule(
        Utf8JsonWriter writer,
        PackageStorageKeyMigration migration,
        PackageStorageKeyMigrationDescriptor descriptor,
        IReadOnlyList<string> physicalKeys)
    {
        writer.WriteStartObject();
        switch (descriptor.Kind)
        {
            case PackageStorageKeyMigrationDescriptorKind.Exact:
                writer.WriteString("kind", "exact");
                writer.WriteString("legacyKey", descriptor.LegacyPrefix);
                writer.WriteString("destinationKey", descriptor.DestinationKey);
                break;
            case PackageStorageKeyMigrationDescriptorKind.OpaqueId:
            case PackageStorageKeyMigrationDescriptorKind.OpaqueIdWithJsonIdentity:
            case PackageStorageKeyMigrationDescriptorKind.OpaqueIdWithCaseInsensitiveJsonIdentity:
                writer.WriteString("kind", descriptor.Kind switch
                {
                    PackageStorageKeyMigrationDescriptorKind.OpaqueId => "opaque-id",
                    PackageStorageKeyMigrationDescriptorKind.OpaqueIdWithJsonIdentity => "opaque-id-json",
                    _ => "opaque-id-json-case-insensitive",
                });
                writer.WriteString("legacyPrefix", descriptor.LegacyPrefix);
                writer.WriteString("legacySuffix", descriptor.LegacySuffix);
                writer.WriteString("destinationPrefix", descriptor.DestinationPrefix);
                writer.WriteNumber("destinationVersion", descriptor.DestinationVersion);
                if (descriptor.JsonIdentityProperty is not null)
                {
                    writer.WriteString("identityProperty", descriptor.JsonIdentityProperty);
                }
                break;
            case PackageStorageKeyMigrationDescriptorKind.DynamicCleanup:
                writer.WriteString("kind", "dynamic-cleanup");
                writer.WritePropertyName("actions");
                writer.WriteStartArray();
                foreach (var key in physicalKeys)
                {
                    var action = descriptor.CleanupResolver!(key)
                                 ?? throw new InvalidOperationException(
                                     "A dynamic package storage cleanup resolver returned null.");
                    writer.WriteStartObject();
                    writer.WriteString("key", key);
                    writer.WriteString("kind", action.Kind switch
                    {
                        PackageStorageKeyMigrationActionKind.NoMatch => "no-match",
                        PackageStorageKeyMigrationActionKind.Delete => "delete",
                        PackageStorageKeyMigrationActionKind.Rewrite => "rewrite",
                        _ => throw new InvalidOperationException(
                            "A dynamic package storage cleanup resolver returned an unsupported action."),
                    });
                    if (action.DestinationKey is null) writer.WriteNull("destinationKey");
                    else writer.WriteString("destinationKey", action.DestinationKey);
                    writer.WriteNumber("precedence", action.Precedence);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException("The worker storage migration kind is unsupported.");
        }
        writer.WriteEndObject();
    }

    internal async Task<IReadOnlyList<string>> ListStateKeysAsync(
        string? prefix,
        CancellationToken cancellationToken,
        bool migrationPreparationRead = false)
    {
        var reservation = ReservePayloadHandle(migrationPreparationRead);
        var retained = false;
        IReadOnlyList<string>? returnedKeys = null;
        try
        {
            await UnaryCallAsync(
                id => Serialize(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "worker.state-list");
                    writer.WriteString("id", id);
                    if (prefix is null) writer.WriteNull("prefix");
                    else writer.WriteString("prefix", prefix);
                    writer.WriteEndObject();
                }),
                cancellationToken,
                terminalResourceRelease: reservation.Release,
                abandonedResult: AbandonDataKeysResultAsync,
                resultConsumer: async value =>
                {
                    var result = ParseHostValue(
                        value,
                        item => WorkerPackagePayloadCodec.ReadDataKeys(item, _limits.MaximumPayloadBytes));
                    if (result.Keys is not null)
                    {
                        returnedKeys = ValidateReturnedKeys(result.Keys);
                        return;
                    }

                    var handle = RegisterPayloadHandle(
                        result.Payload!,
                        reservation,
                        migrationPreparationRead);
                    retained = true;
                    try
                    {
                        var bytes = await ReadPayloadBytesAsync(handle, cancellationToken).ConfigureAwait(false);
                        returnedKeys = ParsePayloadKeys(bytes);
                    }
                    finally
                    {
                        await ReleasePayloadHandleAsync(handle).ConfigureAwait(false);
                    }
                },
                migrationPreparationRead: migrationPreparationRead).ConfigureAwait(false);
            return returnedKeys!;
        }
        finally
        {
            if (!retained) reservation.Release();
        }
    }

    internal Task<IReadOnlyList<string>> ListStateKeysForMigrationPreparationAsync(
        string? prefix,
        CancellationToken cancellationToken)
        => ListStateKeysAsync(prefix, cancellationToken, migrationPreparationRead: true);

    internal async Task<byte[]?> ReadPackageFileAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var reservation = ReservePayloadHandle();
        var retained = false;
        byte[]? returnedContents = null;
        try
        {
            await UnaryCallAsync(
                id => Serialize(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "worker.files-read");
                    writer.WriteString("id", id);
                    writer.WriteString("relativePath", relativePath);
                    writer.WriteEndObject();
                }),
                cancellationToken,
                terminalResourceRelease: reservation.Release,
                abandonedResult: AbandonFileResultAsync,
                resultConsumer: async value =>
                {
                    var descriptor = ParseHostValue(
                        value,
                        item => WorkerPackagePayloadCodec.ReadFilePayload(item, _limits.MaximumFilePayloadBytes));
                    if (descriptor is null) return;

                    var handle = RegisterPayloadHandle(descriptor, reservation);
                    retained = true;
                    try
                    {
                        returnedContents = await ReadPayloadBytesAsync(handle, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await ReleasePayloadHandleAsync(handle).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            return returnedContents;
        }
        finally
        {
            if (!retained) reservation.Release();
        }
    }

    internal Task WritePackageFileAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
        => UseUploadPayloadAsync(
            contents.Length,
            _limits.MaximumFilePayloadBytes,
            async handle =>
            {
                var submitted = false;
                try
                {
                    await WriteUploadPayloadAsync(handle, contents, cancellationToken).ConfigureAwait(false);
                    var sha256 = Convert.ToHexString(SHA256.HashData(contents.Span)).ToLowerInvariant();
                    var result = await UnaryCallAsync(
                        id => Serialize(writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "worker.files-write");
                            writer.WriteString("id", id);
                            writer.WriteString("relativePath", relativePath);
                            writer.WritePropertyName("contents");
                            WorkerPackagePayloadCodec.WriteUploadDataValue(writer, handle.Id, contents.Length, sha256);
                            writer.WriteEndObject();
                        }),
                        cancellationToken,
                        awaitHostTerminalAfterCancellation: true,
                        started: () =>
                        {
                            ForgetConsumedPayloadHandle(handle);
                            submitted = true;
                        }).ConfigureAwait(false);
                    EnsureNullResult(result, "Host package-file write result must be null.");
                }
                finally
                {
                    if (!submitted) await ReleasePayloadHandleAsync(handle).ConfigureAwait(false);
                }
            },
            cancellationToken);

    internal async Task DeletePackageFileAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var result = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.files-delete");
                writer.WriteString("id", id);
                writer.WriteString("relativePath", relativePath);
                writer.WriteEndObject();
            }),
            cancellationToken,
            awaitHostTerminalAfterCancellation: true).ConfigureAwait(false);
        EnsureNullResult(result, "Host package-file delete result must be null.");
    }

    private async Task<string?> GetDataStringAsync(
        string type,
        string key,
        string? mode,
        CancellationToken cancellationToken,
        bool sensitive,
        bool migrationPreparationRead = false)
    {
        var reservation = ReservePayloadHandle(migrationPreparationRead);
        var retained = false;
        string? returnedValue = null;
        try
        {
            await UnaryCallAsync(
                id => Serialize(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", type);
                    writer.WriteString("id", id);
                    writer.WriteString("key", key);
                    if (mode is not null) writer.WriteString("mode", mode);
                    writer.WriteEndObject();
                }),
                cancellationToken,
                terminalResourceRelease: reservation.Release,
                abandonedResult: AbandonDataValueResultAsync,
                resultConsumer: async value =>
                {
                    var result = ParseHostValue(
                        value,
                        item => WorkerPackagePayloadCodec.ReadDataValue(item, _limits.MaximumPayloadBytes));
                    if (result.IsInline)
                    {
                        returnedValue = result.Value;
                        return;
                    }

                    var handle = RegisterPayloadHandle(
                        result.Payload!,
                        reservation,
                        migrationPreparationRead);
                    retained = true;
                    byte[]? bytes = null;
                    try
                    {
                        bytes = await ReadPayloadBytesAsync(handle, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            returnedValue = WorkerPackagePayloadCodec.DecodeDataString(bytes);
                        }
                        catch (WorkerProtocolException exception)
                        {
                            throw FailProtocol(exception);
                        }
                    }
                    finally
                    {
                        if (sensitive && bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                        await ReleasePayloadHandleAsync(handle).ConfigureAwait(false);
                    }
                },
                migrationPreparationRead: migrationPreparationRead).ConfigureAwait(false);
            return returnedValue;
        }
        finally
        {
            if (!retained) reservation.Release();
        }
    }

    private async Task SetDataStringAsync(
        string type,
        string key,
        string value,
        CancellationToken cancellationToken,
        bool sensitive)
    {
        var byteCount = StrictDataUtf8.GetByteCount(value);
        if (byteCount <= WorkerPackagePayloadCodec.InlineDataUtf8Bytes)
        {
            var result = await UnaryCallAsync(
                id => Serialize(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", type);
                    writer.WriteString("id", id);
                    writer.WriteString("key", key);
                    writer.WritePropertyName("value");
                    WorkerPackagePayloadCodec.WriteInlineDataValue(writer, value);
                    writer.WriteEndObject();
                }),
                cancellationToken,
                awaitHostTerminalAfterCancellation: true).ConfigureAwait(false);
            EnsureNullResult(result, "Host package-data mutation result must be null.");
            return;
        }

        var bytes = StrictDataUtf8.GetBytes(value);
        try
        {
            await UseUploadPayloadAsync(
                bytes.Length,
                _limits.MaximumPayloadBytes,
                async handle =>
                {
                    var submitted = false;
                    try
                    {
                        await WriteUploadPayloadAsync(handle, bytes, cancellationToken).ConfigureAwait(false);
                        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                        var result = await UnaryCallAsync(
                            id => Serialize(writer =>
                            {
                                writer.WriteStartObject();
                                writer.WriteString("type", type);
                                writer.WriteString("id", id);
                                writer.WriteString("key", key);
                                writer.WritePropertyName("value");
                                WorkerPackagePayloadCodec.WriteUploadDataValue(writer, handle.Id, bytes.Length, sha256);
                                writer.WriteEndObject();
                            }),
                            cancellationToken,
                            awaitHostTerminalAfterCancellation: true,
                            started: () =>
                            {
                                ForgetConsumedPayloadHandle(handle);
                                submitted = true;
                            }).ConfigureAwait(false);
                        EnsureNullResult(result, "Host package-data mutation result must be null.");
                    }
                    finally
                    {
                        if (!submitted)
                        {
                            await ReleasePayloadHandleAsync(handle).ConfigureAwait(false);
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (sensitive) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task DeleteDataAsync(string type, string key, CancellationToken cancellationToken)
    {
        var result = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", type);
                writer.WriteString("id", id);
                writer.WriteString("key", key);
                writer.WriteEndObject();
            }),
            cancellationToken,
            awaitHostTerminalAfterCancellation: true).ConfigureAwait(false);
        EnsureNullResult(result, "Host package-data delete result must be null.");
    }

    private IReadOnlyList<string> ParsePayloadKeys(byte[] bytes)
    {
        try
        {
            return WorkerPackagePayloadCodec.ReadPayloadKeys(bytes, _limits.MaximumMessageDepth);
        }
        catch (WorkerProtocolException exception)
        {
            throw FailProtocol(exception);
        }
    }

    private IReadOnlyList<string> ValidateReturnedKeys(IReadOnlyList<string> keys)
    {
        try
        {
            return WorkerPackagePayloadCodec.ValidateReturnedKeys(keys);
        }
        catch (WorkerProtocolException exception)
        {
            throw FailProtocol(exception);
        }
    }
}
