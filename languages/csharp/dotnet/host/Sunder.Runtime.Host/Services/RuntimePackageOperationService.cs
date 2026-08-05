using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageOperationService
{
    private readonly PackageSessionState _sessions;
    private readonly RuntimePackageOperationPolicyOptions _policy;
    private readonly CancellationToken _hostStopping;

    public RuntimePackageOperationService(
        PackageSessionState sessions,
        RuntimePackageOperationPolicyOptions policy,
        IHostApplicationLifetime hostLifetime)
        : this(sessions, policy, hostLifetime.ApplicationStopping)
    {
    }

    internal RuntimePackageOperationService(
        PackageSessionState sessions,
        RuntimePackageOperationPolicyOptions? policy = null,
        CancellationToken hostStopping = default)
    {
        _sessions = sessions;
        _policy = policy ?? new RuntimePackageOperationPolicyOptions();
        _hostStopping = hostStopping;
    }

    public int MaxRequestBytes => _policy.MaxRequestBytes;
    public int MaxStreamRecordBytes => _policy.MaxStreamRecordBytes;
    public int MaxStreamErrorMessageCharacters => _policy.MaxStreamErrorMessageCharacters;

    public async ValueTask<byte[]> InvokeAsync(
        string packageId,
        string operationId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > _policy.MaxRequestBytes)
        {
            throw new RuntimeUploadLimitException(
                $"Package Runtime operation request exceeds the {_policy.MaxRequestBytes} byte limit.");
        }

        using var lease = _sessions.AcquireLease();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _hostStopping,
            lease.RetirementToken);
        if (!lease.Session.TryGetLoadedPackage(packageId, out var package) || package is null)
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }
        if (!package.RuntimeOperations.TryGetValue(operationId, out var operation))
        {
            throw new RuntimeNotFoundException(
                $"Package Runtime operation '{operationId}' is not registered for '{packageId}'.");
        }

        byte[] response;
        try
        {
            response = await operation.InvokeAsync(payload, linkedCancellation.Token);
        }
        catch (Exception exception)
        {
            await LogRpcFailureAsync(package, "operation", operationId, exception).ConfigureAwait(false);
            throw;
        }
        if (response.Length > _policy.MaxResponseBytes)
        {
            throw new RuntimeUploadLimitException(
                $"Package Runtime operation response exceeds the {_policy.MaxResponseBytes} byte limit.");
        }

        return response;
    }

    public async IAsyncEnumerable<byte[]> SubscribeAsync(
        string packageId,
        string streamId,
        ReadOnlyMemory<byte> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (payload.Length > _policy.MaxRequestBytes)
        {
            throw new RuntimeUploadLimitException(
                $"Package Runtime stream request exceeds the {_policy.MaxRequestBytes} byte limit.");
        }

        using var lease = _sessions.AcquireLease();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _hostStopping,
            lease.RetirementToken);
        var handlerToken = linkedCancellation.Token;
        if (!lease.Session.TryGetLoadedPackage(packageId, out var package) || package is null)
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }
        if (!package.RuntimeStreams.TryGetValue(streamId, out var stream))
        {
            throw new RuntimeNotFoundException(
                $"Package Runtime stream '{streamId}' is not registered for '{packageId}'.");
        }

        var enumerator = stream.SubscribeAsync(payload, handlerToken).GetAsyncEnumerator(handlerToken);
        try
        {
            while (true)
            {
                byte[] value;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }
                    value = enumerator.Current;
                }
                catch (Exception exception)
                {
                    await LogRpcFailureAsync(package, "stream", streamId, exception).ConfigureAwait(false);
                    throw;
                }
                if (value.Length > _policy.MaxEventBytes)
                {
                    throw new RuntimeUploadLimitException(
                        $"Package Runtime stream event exceeds the {_policy.MaxEventBytes} byte limit.");
                }
                yield return value;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (Exception exception)
            {
                await LogRpcFailureAsync(package, "stream", streamId, exception).ConfigureAwait(false);
                throw;
            }
        }

        handlerToken.ThrowIfCancellationRequested();
    }

    private static async ValueTask LogRpcFailureAsync(
        ActiveLoadedPackage package,
        string operationKind,
        string operationId,
        Exception exception)
    {
        var rpcException = FindHostAuthenticatedRpcException(exception);
        if (rpcException is null)
        {
            return;
        }

        try
        {
            var eventLogger = package.ServiceProvider.GetService<IPackageContext>()?.Logging.Events;
            if (eventLogger is null)
            {
                return;
            }
            RuntimeRpcFailureContextStore.TryGet(rpcException, out var rpcContext);
            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runtime.operation_kind"] = operationKind,
                ["runtime.operation_id"] = operationId,
                ["rpc.error_kind"] = rpcContext?.ErrorKind.ToString() ?? rpcException.Error.Kind.ToString(),
                ["rpc.error_code"] = rpcContext?.ErrorCode ?? Bound(rpcException.Error.Code, 256),
            };
            if (rpcContext is not null)
            {
                attributes["rpc.caller_package_id"] = rpcContext.CallerPackageId;
                attributes["rpc.provider_package_id"] = rpcContext.ProviderPackageId;
                attributes["rpc.provider_package_version"] = rpcContext.ProviderPackageVersion;
                attributes["rpc.provider_id"] = rpcContext.ProviderId;
                attributes["rpc.contract_id"] = rpcContext.ContractId;
                attributes["rpc.provider_activation_id"] = rpcContext.ProviderActivationId;
                attributes["rpc.service_id"] = rpcContext.ServiceId;
                attributes["rpc.method_id"] = rpcContext.MethodId;
                attributes["rpc.exception_type"] = rpcContext.ExceptionType;
                attributes["rpc.exception_fingerprint"] = rpcContext.ExceptionFingerprint;
            }
            await eventLogger.WriteAsync(
                PackageLogLevel.Error,
                $"runtime.{operationKind}.rpc-failed",
                $"A package Runtime {operationKind} failed because an internal RPC call was rejected.",
                attributes).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must not replace the original package failure.
        }
    }

    private static SunderRpcException? FindHostAuthenticatedRpcException(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException { IsHostAuthenticated: true } rpcException)
            {
                return rpcException;
            }
            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
        return null;
    }

    private static string Bound(string? value, int maximumLength)
        => string.IsNullOrWhiteSpace(value)
            ? "rpc.unknown"
            : value[..Math.Min(value.Length, maximumLength)];
}
