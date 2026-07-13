using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;

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

        var response = await operation.InvokeAsync(payload, linkedCancellation.Token);
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

        await foreach (var value in stream.SubscribeAsync(payload, handlerToken)
                           .WithCancellation(handlerToken))
        {
            if (value.Length > _policy.MaxEventBytes)
            {
                throw new RuntimeUploadLimitException(
                    $"Package Runtime stream event exceeds the {_policy.MaxEventBytes} byte limit.");
            }
            yield return value;
        }

        handlerToken.ThrowIfCancellationRequested();
    }
}
