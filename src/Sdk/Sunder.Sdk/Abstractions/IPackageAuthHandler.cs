using Sunder.Sdk.Authentication;

namespace Sunder.Sdk.Abstractions;

public interface IPackageAuthHandler
{
    ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(PackageAuthSessionStartContext context, CancellationToken cancellationToken = default);

    Task<PackageAuthStatus> CompleteAuthorizationAsync(PackageAuthSessionCompletionContext context, CancellationToken cancellationToken = default);

    Task<PackageAuthStatus> DisconnectAsync(CancellationToken cancellationToken = default);
}
