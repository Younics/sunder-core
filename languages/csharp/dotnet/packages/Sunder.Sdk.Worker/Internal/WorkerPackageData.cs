using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker.Internal;

internal sealed class WorkerPackageSettings(WorkerRpcClient client) : IPackageSettings
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key, nameof(key));
        return TranslateValidationAsync(
            client.GetSettingAsync(key, storedOnly: false, cancellationToken),
            nameof(key));
    }

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key, nameof(key));
        return TranslateValidationAsync(
            client.GetSettingAsync(key, storedOnly: true, cancellationToken),
            nameof(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key, nameof(key));
        ValidateValue(value, nameof(value));
        return TranslateValidationAsync(
            client.SetSettingAsync(key, value, cancellationToken),
            nameof(value));
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key, nameof(key));
        return TranslateValidationAsync(client.DeleteSettingAsync(key, cancellationToken), nameof(key));
    }

    private static async Task<T> TranslateValidationAsync<T>(Task<T> operation, string parameterName)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, parameterName, exception);
        }
    }

    private static async Task TranslateValidationAsync(Task operation, string parameterName)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, parameterName, exception);
        }
    }

    internal static void ValidateKey(string key, string parameterName)
    {
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException(
                $"Package storage keys must be portable ASCII tokens of at most {PackageStorageValidation.MaximumKeyLength} characters.",
                parameterName);
        }
    }

    internal static void ValidateValue(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException(
                $"Package storage values cannot exceed {PackageStorageValidation.MaximumValueUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }
    }
}

internal sealed class WorkerPackageKeyValueStore(WorkerRpcClient client) : IPackageKeyValueStore
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageSettings.ValidateKey(key, nameof(key));
        return TranslateValidationAsync(client.GetStateAsync(key, cancellationToken), nameof(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        WorkerPackageSettings.ValidateKey(key, nameof(key));
        WorkerPackageSettings.ValidateValue(value, nameof(value));
        return TranslateValidationAsync(client.SetStateAsync(key, value, cancellationToken), nameof(value));
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageSettings.ValidateKey(key, nameof(key));
        return TranslateValidationAsync(client.ContainsStateKeyAsync(key, cancellationToken), nameof(key));
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageSettings.ValidateKey(key, nameof(key));
        return TranslateValidationAsync(client.DeleteStateAsync(key, cancellationToken), nameof(key));
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        if (prefix is not null && prefix.Length > 0 && !PackageStorageValidation.IsValidKey(prefix))
        {
            throw new ArgumentException(
                $"Package storage key prefixes must be empty or portable ASCII tokens of at most {PackageStorageValidation.MaximumKeyLength} characters.",
                nameof(prefix));
        }
        return TranslateValidationAsync(client.ListStateKeysAsync(prefix, cancellationToken), nameof(prefix));
    }

    private static async Task<T> TranslateValidationAsync<T>(Task<T> operation, string parameterName)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, parameterName, exception);
        }
    }

    private static async Task TranslateValidationAsync(Task operation, string parameterName)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, parameterName, exception);
        }
    }
}

internal sealed class WorkerPackageSecrets(WorkerRpcClient client) : IPackageSecrets
{
    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageSettings.ValidateKey(key, nameof(key));
        return TranslateValidationAsync(client.GetSecretAsync(key, cancellationToken), nameof(key));
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        WorkerPackageSettings.ValidateKey(key, nameof(key));
        WorkerPackageSettings.ValidateValue(value, nameof(value));
        return TranslateValidationAsync(client.SetSecretAsync(key, value, cancellationToken), nameof(value));
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        WorkerPackageSettings.ValidateKey(key, nameof(key));
        return TranslateValidationAsync(client.DeleteSecretAsync(key, cancellationToken), nameof(key));
    }

    private static async Task<T> TranslateValidationAsync<T>(Task<T> operation, string parameterName)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, parameterName, exception);
        }
    }

    private static async Task TranslateValidationAsync(Task operation, string parameterName)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, parameterName, exception);
        }
    }
}

internal sealed class WorkerPackageFileStore(WorkerRpcClient client) : IPackageFileStore
{
    public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        ValidatePath(relativePath);
        return TranslateValidationAsync(client.ReadPackageFileAsync(relativePath, cancellationToken));
    }

    public Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(relativePath);
        if (!PackageStorageValidation.IsValidFileLength(contents.Length))
        {
            throw new ArgumentException(
                $"Package files cannot exceed {PackageStorageValidation.MaximumFileBytes} bytes.",
                nameof(contents));
        }
        return TranslateValidationAsync(client.WritePackageFileAsync(relativePath, contents, cancellationToken));
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        ValidatePath(relativePath);
        return TranslateValidationAsync(client.DeletePackageFileAsync(relativePath, cancellationToken));
    }

    private static void ValidatePath(string relativePath)
    {
        if (!PackageStorageValidation.IsValidRelativePath(relativePath))
        {
            throw new ArgumentException(
                "Package file paths must be relative, portable, non-empty, free of traversal, and within the declared length limits.",
                nameof(relativePath));
        }
    }

    private static async Task<T> TranslateValidationAsync<T>(Task<T> operation)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, "relativePath", exception);
        }
    }

    private static async Task TranslateValidationAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Validation)
        {
            throw new ArgumentException(exception.Error.Message, "relativePath", exception);
        }
    }
}

internal sealed partial class WorkerRpcClient
{
    internal const int InlineDataUtf8Bytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<string, WorkerPayloadHandle> _payloadHandles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rememberedPayloadHandles = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedPayloadHandleOrder = new();
    private int _openPayloadHandles;

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

    internal Task SetStateAsync(string key, string value, CancellationToken cancellationToken)
        => SetDataStringAsync("worker.state-set", key, value, cancellationToken, sensitive: false);

    internal Task DeleteStateAsync(string key, CancellationToken cancellationToken)
        => DeleteDataAsync("worker.state-delete", key, cancellationToken);

    internal async Task<IReadOnlyList<string>> ListStateKeysAsync(
        string? prefix,
        CancellationToken cancellationToken)
    {
        var reservation = ReservePayloadHandle();
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
                        item => WorkerWire.ReadDataKeys(item, _limits.MaximumPayloadBytes));
                    if (result.Keys is not null)
                    {
                        returnedKeys = ValidateReturnedKeys(result.Keys);
                        return;
                    }

                    var handle = RegisterPayloadHandle(result.Payload!, reservation);
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
                }).ConfigureAwait(false);
            return returnedKeys!;
        }
        finally
        {
            if (!retained) reservation.Release();
        }
    }

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
                        item => WorkerWire.ReadFilePayload(item, _limits.MaximumFilePayloadBytes));
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
                            WorkerWire.WriteUploadDataValue(writer, handle.Id, contents.Length, sha256);
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
        bool sensitive)
    {
        var reservation = ReservePayloadHandle();
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
                        item => WorkerWire.ReadDataValue(item, _limits.MaximumPayloadBytes));
                    if (result.IsInline)
                    {
                        returnedValue = result.Value;
                        return;
                    }

                    var handle = RegisterPayloadHandle(result.Payload!, reservation);
                    retained = true;
                    byte[]? bytes = null;
                    try
                    {
                        bytes = await ReadPayloadBytesAsync(handle, cancellationToken).ConfigureAwait(false);
                        string decoded;
                        try
                        {
                            decoded = StrictUtf8.GetString(bytes);
                        }
                        catch (DecoderFallbackException exception)
                        {
                            throw FailProtocol(new WorkerProtocolException(
                                "Host package-data payload is not strict UTF-8.",
                                exception));
                        }
                        if (!PackageStorageValidation.IsValidValue(decoded))
                        {
                            throw FailProtocol(new WorkerProtocolException(
                                "Host package-data payload exceeds the package value limit."));
                        }
                        returnedValue = decoded;
                    }
                    finally
                    {
                        if (sensitive && bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                        await ReleasePayloadHandleAsync(handle).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
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
        var byteCount = StrictUtf8.GetByteCount(value);
        if (byteCount <= InlineDataUtf8Bytes)
        {
            var result = await UnaryCallAsync(
                id => Serialize(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", type);
                    writer.WriteString("id", id);
                    writer.WriteString("key", key);
                    writer.WritePropertyName("value");
                    WorkerWire.WriteInlineDataValue(writer, value);
                    writer.WriteEndObject();
                }),
                cancellationToken,
                awaitHostTerminalAfterCancellation: true).ConfigureAwait(false);
            EnsureNullResult(result, "Host package-data mutation result must be null.");
            return;
        }

        var bytes = StrictUtf8.GetBytes(value);
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
                                WorkerWire.WriteUploadDataValue(writer, handle.Id, bytes.Length, sha256);
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

    private async Task UseUploadPayloadAsync(
        int length,
        int maximumPayloadBytes,
        Func<WorkerPayloadHandle, Task> usePayload,
        CancellationToken cancellationToken)
    {
        var reservation = ReservePayloadHandle();
        var retained = false;
        try
        {
            await UnaryCallAsync(
                id => Serialize(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "worker.payload-allocate");
                    writer.WriteString("id", id);
                    writer.WriteNumber("length", length);
                    writer.WriteEndObject();
                }),
                cancellationToken,
                terminalResourceRelease: reservation.Release,
                abandonedResult: value => AbandonPayloadAllocationResultAsync(
                    value,
                    length,
                    maximumPayloadBytes),
                resultConsumer: async value =>
                {
                    var allocation = ParseHostValue(
                        value,
                        item => WorkerWire.ReadPayloadAllocation(item, maximumPayloadBytes));
                    if (allocation.Length != length)
                    {
                        throw FailProtocol(new WorkerProtocolException(
                            "Host payload allocation length does not match the requested length."));
                    }
                    var descriptor = new WorkerDataPayload(
                        allocation.HandleId,
                        allocation.FilePath,
                        allocation.Length,
                        Sha256: null);
                    var handle = TryRegisterPayloadHandleForOperation(
                        descriptor,
                        reservation,
                        cancellationToken);
                    if (handle is null)
                    {
                        ClaimAbandonedPayloadHandle(descriptor.HandleId);
                        if (CanSendControl())
                        {
                            await ReleaseUntrackedPayloadAsync(descriptor.HandleId).ConfigureAwait(false);
                        }
                        throw new OperationCanceledException(cancellationToken);
                    }
                    retained = true;
                    await usePayload(handle).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        finally
        {
            if (!retained) reservation.Release();
        }
    }

    private WorkerResourceReservation ReservePayloadHandle()
    {
        lock (_runtime.Gate)
        {
            EnsureActiveUnderGate();
            if (_openPayloadHandles >= _limits.MaximumPayloadHandles)
            {
                throw SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.ResourceExhausted,
                    "worker.payload.handle-limit",
                    "The worker transport payload-handle limit was reached.");
            }
            _openPayloadHandles++;
        }
        return new WorkerResourceReservation(() =>
        {
            lock (_runtime.Gate)
            {
                if (_openPayloadHandles > 0) _openPayloadHandles--;
            }
        });
    }

    private WorkerPayloadHandle RegisterPayloadHandle(
        WorkerDataPayload descriptor,
        WorkerResourceReservation reservation)
    {
        var path = ResolveHostPayloadPath(descriptor.FilePath);
        var handle = new WorkerPayloadHandle(
            descriptor.HandleId,
            path,
            descriptor.Length,
            descriptor.Sha256,
            reservation);
        lock (_runtime.Gate)
        {
            EnsureActiveUnderGate();
            if (_rememberedPayloadHandles.Contains(handle.Id)
                || !_payloadHandles.TryAdd(handle.Id, handle))
            {
                throw FailProtocol(new WorkerProtocolException(
                    $"Host reused transport payload handle '{handle.Id}'."));
            }
        }
        return handle;
    }

    private WorkerPayloadHandle? TryRegisterPayloadHandleForOperation(
        WorkerDataPayload descriptor,
        WorkerResourceReservation reservation,
        CancellationToken cancellationToken)
    {
        var path = ResolveHostPayloadPath(descriptor.FilePath);
        var handle = new WorkerPayloadHandle(
            descriptor.HandleId,
            path,
            descriptor.Length,
            descriptor.Sha256,
            reservation);
        lock (_runtime.Gate)
        {
            if (cancellationToken.IsCancellationRequested || !_runtime.IsActiveUnderGate)
            {
                return null;
            }
            if (_rememberedPayloadHandles.Contains(handle.Id)
                || !_payloadHandles.TryAdd(handle.Id, handle))
            {
                throw FailProtocol(new WorkerProtocolException(
                    $"Host reused transport payload handle '{handle.Id}'."));
            }
        }
        return handle;
    }

    private async Task WriteUploadPayloadAsync(
        WorkerPayloadHandle handle,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                handle.FilePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.SetLength(0);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FailProtocol(new WorkerProtocolException(
                "Host transport payload file could not be written safely.",
                exception));
        }
    }

    private async Task<byte[]> ReadPayloadBytesAsync(
        WorkerPayloadHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                handle.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != handle.Length)
            {
                throw new WorkerProtocolException(
                    "Host transport payload file length does not match its descriptor.");
            }
            var bytes = new byte[checked((int)handle.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (handle.Sha256 is not null)
            {
                var actual = SHA256.HashData(bytes);
                var expected = Convert.FromHexString(handle.Sha256);
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                {
                    throw new WorkerProtocolException(
                        "Host transport payload file hash does not match its descriptor.");
                }
            }
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerProtocolException exception)
        {
            throw FailProtocol(exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OverflowException or FormatException)
        {
            throw FailProtocol(new WorkerProtocolException(
                "Host transport payload file could not be read safely.",
                exception));
        }
    }

    private async ValueTask ReleasePayloadHandleAsync(WorkerPayloadHandle handle)
    {
        var removed = false;
        lock (_runtime.Gate)
        {
            if (_payloadHandles.TryGetValue(handle.Id, out var registered)
                && ReferenceEquals(registered, handle))
            {
                _payloadHandles.Remove(handle.Id);
                RememberPayloadHandleUnderGate(handle.Id);
                removed = true;
            }
        }
        if (!removed)
        {
            handle.Reservation.Release();
            return;
        }

        try
        {
            if (CanSendControl())
            {
                await ReleaseUntrackedPayloadAsync(handle.Id).ConfigureAwait(false);
            }
        }
        finally
        {
            handle.Reservation.Release();
        }
    }

    private void ForgetConsumedPayloadHandle(WorkerPayloadHandle handle)
    {
        lock (_runtime.Gate)
        {
            if (_payloadHandles.TryGetValue(handle.Id, out var registered)
                && ReferenceEquals(registered, handle))
            {
                _payloadHandles.Remove(handle.Id);
                RememberPayloadHandleUnderGate(handle.Id);
            }
        }
        handle.Reservation.Release();
    }

    private async ValueTask ReleaseUntrackedPayloadAsync(string handleId)
    {
        var result = await UnaryCallAsync(
            id => Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.payload-release");
                writer.WriteString("id", id);
                writer.WriteString("handleId", handleId);
                writer.WriteEndObject();
            }),
            CancellationToken.None,
            control: true).ConfigureAwait(false);
        EnsureNullResult(result, "Host payload release result must be null.");
    }

    private async ValueTask AbandonPayloadAllocationResultAsync(
        JsonElement value,
        int requestedLength,
        int maximumPayloadBytes)
    {
        var allocation = ParseHostValue(
            value,
            item => WorkerWire.ReadPayloadAllocation(item, maximumPayloadBytes));
        _ = ResolveHostPayloadPath(allocation.FilePath);
        if (allocation.Length != requestedLength)
        {
            throw FailProtocol(new WorkerProtocolException(
                "Host payload allocation length does not match the requested length."));
        }
        ClaimAbandonedPayloadHandle(allocation.HandleId);
        if (CanSendControl()) await ReleaseUntrackedPayloadAsync(allocation.HandleId).ConfigureAwait(false);
    }

    private async ValueTask AbandonDataValueResultAsync(JsonElement value)
    {
        var result = ParseHostValue(
            value,
            item => WorkerWire.ReadDataValue(item, _limits.MaximumPayloadBytes));
        if (result.Payload is not null)
        {
            _ = ResolveHostPayloadPath(result.Payload.FilePath);
            ClaimAbandonedPayloadHandle(result.Payload.HandleId);
            if (CanSendControl()) await ReleaseUntrackedPayloadAsync(result.Payload.HandleId).ConfigureAwait(false);
        }
    }

    private async ValueTask AbandonDataKeysResultAsync(JsonElement value)
    {
        var result = ParseHostValue(
            value,
            item => WorkerWire.ReadDataKeys(item, _limits.MaximumPayloadBytes));
        if (result.Payload is not null)
        {
            _ = ResolveHostPayloadPath(result.Payload.FilePath);
            ClaimAbandonedPayloadHandle(result.Payload.HandleId);
            if (CanSendControl()) await ReleaseUntrackedPayloadAsync(result.Payload.HandleId).ConfigureAwait(false);
        }
    }

    private async ValueTask AbandonFileResultAsync(JsonElement value)
    {
        var payload = ParseHostValue(
            value,
            item => WorkerWire.ReadFilePayload(item, _limits.MaximumFilePayloadBytes));
        if (payload is not null)
        {
            _ = ResolveHostPayloadPath(payload.FilePath);
            ClaimAbandonedPayloadHandle(payload.HandleId);
            if (CanSendControl()) await ReleaseUntrackedPayloadAsync(payload.HandleId).ConfigureAwait(false);
        }
    }

    private async Task ReleasePayloadForShutdownAsync(WorkerPayloadHandle handle)
    {
        try
        {
            await ReleaseUntrackedPayloadAsync(handle.Id).ConfigureAwait(false);
        }
        finally
        {
            handle.Reservation.Release();
        }
    }

    private void ClaimAbandonedPayloadHandle(string handleId)
    {
        lock (_runtime.Gate)
        {
            if (_payloadHandles.ContainsKey(handleId) || _rememberedPayloadHandles.Contains(handleId))
            {
                throw FailProtocol(new WorkerProtocolException(
                    $"Host reused transport payload handle '{handleId}'."));
            }
            RememberPayloadHandleUnderGate(handleId);
        }
    }

    private void RememberPayloadHandleUnderGate(string handleId)
    {
        if (!_rememberedPayloadHandles.Add(handleId)) return;
        _rememberedPayloadHandleOrder.Enqueue(handleId);
        while (_rememberedPayloadHandleOrder.Count > _limits.MaximumRememberedHostIds)
        {
            _rememberedPayloadHandles.Remove(_rememberedPayloadHandleOrder.Dequeue());
        }
    }

    private bool CanSendControl()
    {
        lock (_runtime.Gate)
        {
            return _runtime.CanSendControlUnderGate;
        }
    }

    private string ResolveHostPayloadPath(string value)
    {
        var dataPath = _environment.PackageDataPath
                       ?? throw FailProtocol(new WorkerProtocolException(
                           "Host transport payload paths are unavailable."));
        var payloadPath = Path.Combine(dataPath, "payload");
        try
        {
            var fullPath = Path.GetFullPath(value, payloadPath);
            var relative = Path.GetRelativePath(payloadPath, fullPath);
            if (Path.IsPathRooted(relative)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new WorkerProtocolException(
                    "Host transport payload file is outside the worker temporary directory.");
            }
            return fullPath;
        }
        catch (WorkerProtocolException exception)
        {
            throw FailProtocol(exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw FailProtocol(new WorkerProtocolException(
                "Host transport payload path is invalid.",
                exception));
        }
    }

    private IReadOnlyList<string> ParsePayloadKeys(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = _limits.MaximumMessageDepth,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new WorkerProtocolException("Host package-state key payload must be a JSON array.");
            }
            var keys = document.RootElement.EnumerateArray().Select(static item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new WorkerProtocolException("Host package-state key payload contains a non-string value.");
                }
                return item.GetString()!;
            }).ToArray();
            return ValidateReturnedKeys(keys);
        }
        catch (WorkerProtocolException exception)
        {
            throw FailProtocol(exception);
        }
        catch (JsonException exception)
        {
            throw FailProtocol(new WorkerProtocolException(
                "Host package-state key payload is not strict JSON.",
                exception));
        }
    }

    private IReadOnlyList<string> ValidateReturnedKeys(IReadOnlyList<string> keys)
    {
        string? previous = null;
        foreach (var key in keys)
        {
            if (!PackageStorageValidation.IsValidKey(key)
                || previous is not null && StringComparer.Ordinal.Compare(previous, key) >= 0)
            {
                throw FailProtocol(new WorkerProtocolException(
                    "Host package-state keys are invalid, duplicated, or not ordinally sorted."));
            }
            previous = key;
        }
        return keys;
    }
}

internal sealed record WorkerPayloadHandle(
    string Id,
    string FilePath,
    long Length,
    string? Sha256,
    WorkerResourceReservation Reservation);

internal sealed record WorkerPayloadAllocation(string HandleId, string FilePath, long Length);

internal sealed record WorkerDataPayload(string HandleId, string FilePath, long Length, string? Sha256);

internal sealed record WorkerDataValue(bool IsInline, string? Value, WorkerDataPayload? Payload);

internal sealed record WorkerDataKeys(IReadOnlyList<string>? Keys, WorkerDataPayload? Payload);

internal static partial class WorkerWire
{
    private static readonly UTF8Encoding StrictDataUtf8 = new(false, true);

    public static WorkerPayloadAllocation ReadPayloadAllocation(JsonElement value, int maximumPayloadBytes)
    {
        WorkerJson.RequireOnlyProperties(value, "handleId", "filePath", "length");
        var length = WorkerJson.RequiredNonNegativeInt64(value, "length");
        if (length > maximumPayloadBytes)
        {
            throw new WorkerProtocolException("Host payload allocation exceeds the configured limit.");
        }
        return new WorkerPayloadAllocation(
            WorkerJson.RequiredSafeId(value, "handleId"),
            WorkerJson.RequiredString(value, "filePath", 4096),
            length);
    }

    public static WorkerDataValue ReadDataValue(JsonElement value, int maximumPayloadBytes)
    {
        WorkerJson.RequireObject(value, "Host package-data result");
        var kind = WorkerJson.RequiredString(value, "kind", 32);
        if (string.Equals(kind, "inline", StringComparison.Ordinal))
        {
            WorkerJson.RequireOnlyProperties(value, "kind", "value");
            var item = WorkerJson.RequiredValue(value, "value");
            if (item.ValueKind == JsonValueKind.Null) return new WorkerDataValue(true, null, null);
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new WorkerProtocolException("Host inline package-data value must be a string or null.");
            }
            var text = item.GetString()!;
            if (StrictDataUtf8.GetByteCount(text) > WorkerRpcClient.InlineDataUtf8Bytes)
            {
                throw new WorkerProtocolException("Host inline package-data value exceeds the inline transport limit.");
            }
            if (!PackageStorageValidation.IsValidValue(text))
            {
                throw new WorkerProtocolException("Host inline package-data value exceeds the package value limit.");
            }
            return new WorkerDataValue(true, text, null);
        }
        if (!string.Equals(kind, "payload", StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("Host package-data result kind is invalid.");
        }
        return new WorkerDataValue(false, null, ReadDataPayload(value, maximumPayloadBytes));
    }

    public static WorkerDataKeys ReadDataKeys(JsonElement value, int maximumPayloadBytes)
    {
        WorkerJson.RequireObject(value, "Host package-state list result");
        var kind = WorkerJson.RequiredString(value, "kind", 32);
        if (string.Equals(kind, "inline", StringComparison.Ordinal))
        {
            WorkerJson.RequireOnlyProperties(value, "kind", "keys");
            var keysValue = WorkerJson.RequiredValue(value, "keys");
            if (keysValue.ValueKind != JsonValueKind.Array)
            {
                throw new WorkerProtocolException("Host inline package-state keys must be an array.");
            }
            var keys = keysValue.EnumerateArray().Select(static item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new WorkerProtocolException("Host inline package-state keys contain a non-string value.");
                }
                return item.GetString()!;
            }).ToArray();
            if (StrictDataUtf8.GetByteCount(keysValue.GetRawText()) > WorkerRpcClient.InlineDataUtf8Bytes)
            {
                throw new WorkerProtocolException("Host inline package-state keys exceed the inline transport limit.");
            }
            return new WorkerDataKeys(keys, null);
        }
        if (!string.Equals(kind, "payload", StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("Host package-state list result kind is invalid.");
        }
        return new WorkerDataKeys(null, ReadDataPayload(value, maximumPayloadBytes));
    }

    public static WorkerDataPayload? ReadFilePayload(JsonElement value, int maximumPayloadBytes)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        WorkerJson.RequireObject(value, "Host package-file result");
        if (!string.Equals(WorkerJson.RequiredString(value, "kind", 32), "payload", StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("Host package-file result kind is invalid.");
        }
        return ReadDataPayload(value, maximumPayloadBytes);
    }

    public static void WriteInlineDataValue(Utf8JsonWriter writer, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "inline");
        writer.WriteString("value", value);
        writer.WriteEndObject();
    }

    public static void WriteUploadDataValue(
        Utf8JsonWriter writer,
        string handleId,
        long length,
        string sha256)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "payload");
        writer.WriteString("handleId", handleId);
        writer.WriteNumber("length", length);
        writer.WriteString("sha256", sha256);
        writer.WriteEndObject();
    }

    private static WorkerDataPayload ReadDataPayload(JsonElement value, int maximumPayloadBytes)
    {
        WorkerJson.RequireOnlyProperties(value, "kind", "handleId", "filePath", "length", "sha256");
        var length = WorkerJson.RequiredNonNegativeInt64(value, "length");
        if (length > maximumPayloadBytes)
        {
            throw new WorkerProtocolException("Host package-data payload exceeds the configured limit.");
        }
        var sha256 = WorkerJson.RequiredString(value, "sha256", 64);
        if (sha256.Length != 64 || sha256.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new WorkerProtocolException("Host package-data payload hash is invalid.");
        }
        return new WorkerDataPayload(
            WorkerJson.RequiredSafeId(value, "handleId"),
            WorkerJson.RequiredString(value, "filePath", 4096),
            length,
            sha256);
    }
}
