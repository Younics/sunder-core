using Microsoft.Extensions.Logging;

namespace Sunder.Sdk.Logging;

public sealed class FilePackageLogging : IPackageLogging, IDisposable
{
    private readonly FilePackageLoggerProvider _runtimeProvider;

    public FilePackageLogging(
        string logsRootPath,
        string packageId,
        Version packageVersion,
        TimeSpan? retentionPeriod = null)
    {
        var retention = retentionPeriod ?? TimeSpan.FromDays(7);
        var resource = new PackageLogResource(packageId, packageVersion.ToString());
        _runtimeProvider = new FilePackageLoggerProvider(logsRootPath, "runtime", resource, retention);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(_runtimeProvider));
        Events = new FilePackageEventLogger(logsRootPath, resource, retention);
    }

    public ILoggerFactory LoggerFactory { get; }

    public IPackageEventLogger Events { get; }

    public void Dispose()
    {
        LoggerFactory.Dispose();
        _runtimeProvider.Dispose();
        if (Events is IDisposable disposableEvents)
        {
            disposableEvents.Dispose();
        }
    }
}

internal sealed record PackageLogResource(string PackageId, string PackageVersion);
