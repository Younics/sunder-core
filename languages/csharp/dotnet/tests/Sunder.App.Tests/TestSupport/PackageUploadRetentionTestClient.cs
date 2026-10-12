using System.Collections.Concurrent;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Tests.TestSupport;

internal sealed class PackageUploadRetentionTestClient : IRuntimePackageUploadRetentionClient
{
    private readonly ConcurrentDictionary<string, RuntimePackageUploadRetentionHandle> _handles = new();
    public ConcurrentQueue<RuntimePackageUploadRetentionRequest> Retains { get; } = new();
    public ConcurrentQueue<RuntimePackageUploadRetentionHandle> Renewals { get; } = new();
    public ConcurrentQueue<RuntimePackageUploadRetentionHandle> StatusReads { get; } = new();
    public ConcurrentQueue<RuntimePackageUploadRetentionHandle> Releases { get; } = new();
    public Func<RuntimePackageUploadRetentionHandle, CancellationToken, Task<RuntimePackageUploadRetentionResult>>? Renew { get; set; }
    public Func<RuntimePackageUploadRetentionHandle, CancellationToken, Task<RuntimePackageUploadRetentionResult>>? Status { get; set; }
    public Func<RuntimePackageUploadRetentionHandle, CancellationToken, Task<RuntimePackageUploadRetentionResult>>? Release { get; set; }

    public Task<RuntimePackageUploadRetentionResult> RetainPackageUploadAsync(RuntimePackageUploadRetentionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Retains.Enqueue(request);
        var handle = new RuntimePackageUploadRetentionHandle(request.UploadId, Guid.NewGuid().ToString("N"), request.ExpectedContentHash, request.ExpectedLength);
        _handles[handle.LeaseId] = handle;
        return Task.FromResult(Available(handle));
    }

    public Task<RuntimePackageUploadRetentionResult> RenewPackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Renewals.Enqueue(handle);
        return Renew?.Invoke(handle, cancellationToken) ?? Task.FromResult(Available(handle));
    }

    public Task<RuntimePackageUploadRetentionResult> GetPackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatusReads.Enqueue(handle);
        return Status?.Invoke(handle, cancellationToken) ?? Task.FromResult(Available(handle));
    }

    public Task<RuntimePackageUploadRetentionResult> ReleasePackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); // Cleanup must get a live token even after operation cancellation.
        Releases.Enqueue(handle);
        _handles.TryRemove(handle.LeaseId, out _);
        return Release?.Invoke(handle, cancellationToken) ?? Task.FromResult(new RuntimePackageUploadRetentionResult(true, null, "Released or already consumed", null));
    }

    public static RuntimePackageUploadRetentionResult Available(RuntimePackageUploadRetentionHandle handle)
        => new(true, null, "Retained", new(handle, DateTimeOffset.UtcNow.AddMinutes(30), DateTimeOffset.UtcNow.AddHours(2)));

    public static RuntimePackageUploadRetentionResult Unavailable(string reason, string? code = null)
        => new(false, code ?? RuntimePackageUploadRetentionErrorCodes.Unavailable, reason, null);

    public int LiveHandleCount => _handles.Count;
    public void Dispose() { }
}

internal interface IUnsupportedPackageUploadRetentionTestClient : IRuntimePackageUploadRetentionClient
{
    Task<RuntimePackageUploadRetentionResult> IRuntimePackageUploadRetentionClient.RetainPackageUploadAsync(RuntimePackageUploadRetentionRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This fixture does not install local packages.");
    Task<RuntimePackageUploadRetentionResult> IRuntimePackageUploadRetentionClient.RenewPackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    Task<RuntimePackageUploadRetentionResult> IRuntimePackageUploadRetentionClient.GetPackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    Task<RuntimePackageUploadRetentionResult> IRuntimePackageUploadRetentionClient.ReleasePackageUploadRetentionAsync(RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
