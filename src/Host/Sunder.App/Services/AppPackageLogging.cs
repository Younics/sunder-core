using Microsoft.Extensions.Logging;
using Sunder.Sdk.Logging;

namespace Sunder.App.Services;

internal sealed class AppPackageLogging : IPackageLogging, IDisposable
{
    public AppPackageLogging(string packageId)
    {
        LoggerFactory = new AppPackageLoggerFactory(packageId);
        Events = new AppPackageEventLogger(packageId);
    }

    public ILoggerFactory LoggerFactory { get; }

    public IPackageEventLogger Events { get; }

    public void Dispose() => LoggerFactory.Dispose();

    private sealed class AppPackageLoggerFactory(string packageId) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new AppPackageLogger(packageId, categoryName);

        public void AddProvider(ILoggerProvider provider) => provider.Dispose();

        public void Dispose()
        {
        }
    }

    private sealed class AppPackageLogger(string packageId, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                AppSessionLog.WritePackage(
                    ToPackageLogLevel(logLevel),
                    packageId,
                    $"{categoryName}: {formatter(state, exception)}",
                    exception);
            }
        }
    }

    private sealed class AppPackageEventLogger(string packageId) : IPackageEventLogger
    {
        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppSessionLog.WritePackage(level, packageId, $"{eventName}: {message}", exception);
            return ValueTask.CompletedTask;
        }
    }

    private static PackageLogLevel ToPackageLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => PackageLogLevel.Trace,
        LogLevel.Debug => PackageLogLevel.Debug,
        LogLevel.Information => PackageLogLevel.Information,
        LogLevel.Warning => PackageLogLevel.Warning,
        LogLevel.Error => PackageLogLevel.Error,
        LogLevel.Critical => PackageLogLevel.Critical,
        _ => PackageLogLevel.Information,
    };

    private sealed class NullScope : IDisposable
    {
        internal static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
