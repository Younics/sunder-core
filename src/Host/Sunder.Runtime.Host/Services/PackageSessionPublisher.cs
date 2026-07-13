using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionPublisher(
    RuntimeSessionOwner sessions,
    ILogger<PackageSessionPublisher> logger)
{
    public async Task<IReadOnlyList<string>> PublishAsync(
        ActivePackageSession session,
        PackageSessionSourceSnapshot? sources,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.StartBackgroundServicesAsync(logger, cancellationToken);
            if (sources is not null) sessions.Sources.Replace(sources);
            return await sessions.PublishAsync(session, cancellationToken);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }
}
