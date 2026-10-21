using System.ComponentModel;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageWorkerExecutionObservationService(RuntimeRpcBroker broker, ExecutionRuntimeStore store)
{
    internal Task<RuntimeWorkerExecutionReference> CaptureAsync(RuntimeRpcAppExecutionCaptureRequest request, CancellationToken token)
        => TranslateAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
            ArgumentNullException.ThrowIfNull(request.Provider);
            return broker.WithAppExecutionObservationAsync(request.SessionId, request.CallScopeId, request.Provider,
                (owner, reservation, current) => store.CaptureWorkerExecutionAsync(
                    reservation ?? throw new InvalidOperationException("Execution capture has no authorized reservation."), owner, current), token);
        });

    internal Task<RuntimeWorkerExecutionObservation> ObserveAsync(RuntimeRpcAppExecutionObserveRequest request, CancellationToken token)
        => TranslateAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
            ArgumentNullException.ThrowIfNull(request.Reference);
            if (request.Reference.RuntimeInstanceId == Guid.Empty
                || string.IsNullOrWhiteSpace(request.Reference.Value) || request.Reference.Value.Length > 256)
                throw new ArgumentException("An execution reference requires its original Host and a bounded opaque value.", nameof(request));
            return broker.WithAppExecutionObservationAsync(request.SessionId, request.CallScopeId, null,
                (owner, _, current) => store.ObserveWorkerExecutionAsync(request.Reference, owner, current), token);
        });

    internal static async Task<T> TranslateAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (ExecutionRuntimeObservationOwnerException)
        { throw Error(SunderRpcErrorKind.PermissionDenied, "rpc.execution.owner-denied", "The execution observation is not owned by this App package."); }
        catch (ExecutionRuntimeObservationCapacityException)
        { throw Error(SunderRpcErrorKind.ResourceExhausted, "rpc.execution.retention-limit", "Execution observation retention is full. Existing references remain retained."); }
        catch (Exception error) when (error is ExecutionRuntimeObservationCorruptException or InvalidDataException or JsonException)
        { throw Error(SunderRpcErrorKind.Protocol, "rpc.execution.invalid-evidence", "The Runtime could not validate exact execution ownership evidence."); }
        catch (Exception error) when (IsFileAccessDenied(error))
        { throw Error(SunderRpcErrorKind.PermissionDenied, "rpc.execution.access-denied", "The Runtime is not permitted to access execution ownership evidence."); }
        catch (ArgumentException)
        { throw Error(SunderRpcErrorKind.Validation, "rpc.execution.invalid-request", "The execution observation request is invalid."); }
        catch (Exception error) when (error is IOException or RuntimeUnavailableException)
        { throw Error(SunderRpcErrorKind.Unavailable, "rpc.execution.unavailable", "Execution ownership evidence is temporarily unavailable."); }
    }

    private static bool IsFileAccessDenied(Exception error)
    {
        // Durable writes and native move/directory-sync operations can wrap an
        // access denial in IO exceptions. Do not unwrap unrelated RPC/cancellation
        // failures or confuse Unix errno 5 (EIO) with Windows ERROR_ACCESS_DENIED.
        while (true)
        {
            if (error is UnauthorizedAccessException) return true;
            if (error is Win32Exception native)
                return OperatingSystem.IsWindows() ? native.NativeErrorCode == 5 : native.NativeErrorCode is 1 or 13;
            if (error is not IOException || error.InnerException is not { } inner) return false;
            error = inner;
        }
    }

    private static SunderRpcException Error(SunderRpcErrorKind kind, string code, string message)
        => SunderRpcException.Infrastructure(kind, code, message);
}
