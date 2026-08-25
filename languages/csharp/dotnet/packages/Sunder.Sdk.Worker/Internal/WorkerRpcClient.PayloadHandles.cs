using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class WorkerRpcClient
{
    private readonly Dictionary<string, WorkerPayloadHandle> _payloadHandles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rememberedPayloadHandles = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedPayloadHandleOrder = new();
    private int _openPayloadHandles;

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
                        item => WorkerPackagePayloadCodec.ReadPayloadAllocation(item, maximumPayloadBytes));
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

    private WorkerResourceReservation ReservePayloadHandle(bool migrationPreparationRead = false)
    {
        lock (_runtime.Gate)
        {
            EnsureDataReadAvailableUnderGate(migrationPreparationRead);
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
        WorkerResourceReservation reservation,
        bool migrationPreparationRead = false)
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
            EnsureDataReadAvailableUnderGate(migrationPreparationRead);
            if (_rememberedPayloadHandles.Contains(handle.Id)
                || !_payloadHandles.TryAdd(handle.Id, handle))
            {
                throw FailProtocol(new WorkerProtocolException(
                    $"Host reused transport payload handle '{handle.Id}'."));
            }
        }
        return handle;
    }

    private void EnsureDataReadAvailableUnderGate(bool migrationPreparationRead)
    {
        if (migrationPreparationRead)
        {
            if (!_runtime.CanReadStateForMigrationPreparationUnderGate)
            {
                throw SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.Unavailable,
                    "rpc.worker.not-preparing-migrations",
                    "The process worker is not preparing storage migrations.");
            }
            return;
        }
        EnsureActiveUnderGate();
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
            item => WorkerPackagePayloadCodec.ReadPayloadAllocation(item, maximumPayloadBytes));
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
            item => WorkerPackagePayloadCodec.ReadDataValue(item, _limits.MaximumPayloadBytes));
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
            item => WorkerPackagePayloadCodec.ReadDataKeys(item, _limits.MaximumPayloadBytes));
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
            item => WorkerPackagePayloadCodec.ReadFilePayload(item, _limits.MaximumFilePayloadBytes));
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
}

internal sealed record WorkerPayloadHandle(
    string Id,
    string FilePath,
    long Length,
    string? Sha256,
    WorkerResourceReservation Reservation);
