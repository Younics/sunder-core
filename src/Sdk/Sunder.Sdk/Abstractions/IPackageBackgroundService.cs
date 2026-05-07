namespace Sunder.Sdk.Abstractions;

public interface IPackageBackgroundService
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
