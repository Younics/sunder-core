using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ProcessRuntimeWorker
{
    private const int InlineDataUtf8Bytes = 64 * 1024;
    private static readonly UTF8Encoding StrictPayloadUtf8 = new(false, true);
    private readonly Dictionary<string, WorkerTransportPayload> _workerPayloadHandles = new(StringComparer.Ordinal);
    private int _pendingWorkerPayloadHandles;
    private int _releasingWorkerPayloadHandles;

    private V2WorkerContributions ReadV2Contributions(JsonElement contributions)
    {
        SunderWorkerProtocol.RequireOnlyProperties(
            contributions,
            "settingsSchema",
            "runtimeOperations",
            "runtimeStreams",
            "callbackHandlers",
            "authHandler",
            "rpcProviders",
            "candidateLifecycle",
            "generationLifecycle");
        var schemaValue = SunderWorkerProtocol.RequiredValue(contributions, "settingsSchema");
        var schema = schemaValue.ValueKind == JsonValueKind.Null
            ? null
            : ReadSettingsSchema(schemaValue);
        RequireEmptyV2ContributionArray(contributions, "runtimeOperations");
        RequireEmptyV2ContributionArray(contributions, "runtimeStreams");
        RequireEmptyV2ContributionArray(contributions, "callbackHandlers");
        if (SunderWorkerProtocol.RequiredValue(contributions, "authHandler").ValueKind != JsonValueKind.False)
        {
            throw new SunderWorkerProtocolException(
                "This Host slice does not yet support V2 worker auth contributions.");
        }
        RequireBoolean(contributions, "candidateLifecycle");
        RequireBoolean(contributions, "generationLifecycle");
        return new V2WorkerContributions(
            schema,
            SunderWorkerProtocol.RequiredValue(contributions, "rpcProviders"));
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
                RequireV2WorkerCall(type);
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
                RequireV2WorkerCall(type);
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
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "mode");
                var key = ReadDataString(root, "key");
                var result = ReadDataString(root, "mode") switch
                {
                    "effective" => await _packageContext.Settings.GetValueAsync(
                        key,
                        operationCancellation).ConfigureAwait(false),
                    "stored" => await _packageContext.Settings.GetStoredValueAsync(
                        key,
                        operationCancellation).ConfigureAwait(false),
                    _ => throw new SunderWorkerProtocolException(
                        "Process worker settings get mode is invalid."),
                };
                await QueueDataStringResultAsync(call, result, operationCancellation).ConfigureAwait(false);
                return true;
            }
            case "worker.settings-set":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "value");
                var key = ReadDataString(root, "key");
                var value = await ReadWorkerDataValueAsync(
                    SunderWorkerProtocol.RequiredValue(root, "value"),
                    call).ConfigureAwait(false);
                await _packageContext.Settings.SetValueAsync(
                    key,
                    value,
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.secrets-get":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                await QueueDataStringResultAsync(
                    call,
                    await _packageContext.Secrets.GetSecretAsync(
                        ReadDataString(root, "key"),
                        operationCancellation).ConfigureAwait(false),
                    operationCancellation).ConfigureAwait(false);
                return true;
            }
            case "worker.secrets-set":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "value");
                var value = await ReadWorkerDataValueAsync(
                    SunderWorkerProtocol.RequiredValue(root, "value"),
                    call,
                    sensitive: true).ConfigureAwait(false);
                await _packageContext.Secrets.SetSecretAsync(
                    ReadDataString(root, "key"),
                    value,
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.secrets-delete":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                await _packageContext.Secrets.DeleteSecretAsync(
                    ReadDataString(root, "key"),
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.settings-delete":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                await _packageContext.Settings.DeleteValueAsync(
                    ReadDataString(root, "key"),
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.state-get":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "mode");
                var key = ReadDataString(root, "key");
                switch (ReadDataString(root, "mode"))
                {
                    case "value":
                        await QueueDataStringResultAsync(
                            call,
                            await _packageContext.Storage.State.GetValueAsync(
                                key,
                                operationCancellation).ConfigureAwait(false),
                            operationCancellation).ConfigureAwait(false);
                        return true;
                    case "contains":
                        var contains = await _packageContext.Storage.State.ContainsKeyAsync(
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
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key", "value");
                var key = ReadDataString(root, "key");
                var value = await ReadWorkerDataValueAsync(
                    SunderWorkerProtocol.RequiredValue(root, "value"),
                    call).ConfigureAwait(false);
                await _packageContext.Storage.State.SetValueAsync(
                    key,
                    value,
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.state-delete":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "key");
                await _packageContext.Storage.State.DeleteValueAsync(
                    ReadDataString(root, "key"),
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.state-list":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "prefix");
                var keys = await _packageContext.Storage.State.ListKeysAsync(
                    ReadNullableDataString(root, "prefix"),
                    operationCancellation).ConfigureAwait(false);
                await QueueDataKeysResultAsync(call, keys, operationCancellation).ConfigureAwait(false);
                return true;
            }
            case "worker.files-read":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "relativePath");
                var contents = await _packageContext.Storage.Files.ReadAsync(
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
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "relativePath", "contents");
                var contents = await ReadWorkerPayloadBytesAsync(
                    SunderWorkerProtocol.RequiredValue(root, "contents"),
                    call,
                    _policy.MaxWorkerFilePayloadBytes).ConfigureAwait(false);
                await _packageContext.Storage.Files.WriteAsync(
                    ReadDataString(root, "relativePath"),
                    contents,
                    operationCancellation).ConfigureAwait(false);
                QueueEnvelope(new { type = "host.result", id = call.Id, value = (object?)null });
                return true;
            }
            case "worker.files-delete":
            {
                RequireV2WorkerCall(type);
                SunderWorkerProtocol.RequireOnlyProperties(root, "type", "id", "relativePath");
                await _packageContext.Storage.Files.DeleteAsync(
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

    private static bool IsPackageDataCall(string type)
        => type.StartsWith("worker.settings-", StringComparison.Ordinal)
           || type.StartsWith("worker.state-", StringComparison.Ordinal)
           || type.StartsWith("worker.secrets-", StringComparison.Ordinal)
           || type.StartsWith("worker.files-", StringComparison.Ordinal);

    private PackageSessionLease? AcquirePackageDataLease()
    {
        if (_packageSessionState is null) return null;
        PackageSessionLease lease;
        try
        {
            lease = _packageSessionState.AcquireLease();
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

    private sealed record V2WorkerContributions(
        PackageSettingsSchema? SettingsSchema,
        JsonElement RpcProviders);

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
