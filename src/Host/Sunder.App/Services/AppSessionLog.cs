using System.Diagnostics;
using Sunder.Sdk.Logging;

namespace Sunder.App.Services;

internal static class AppSessionLog
{
    private static readonly string LogRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunder",
        "logs");

    private static readonly Lazy<IPackageLogging> Logging = new(() => new FilePackageLogging(
        LogRootPath,
        "sunder.app",
        typeof(AppSessionLog).Assembly.GetName().Version ?? new Version(0, 0)));

    public static void WriteInfo(string message)
        => Write(PackageLogLevel.Information, message, exception: null);

    public static void WriteError(string message, Exception? exception = null)
        => Write(PackageLogLevel.Error, message, exception);

    private static void Write(PackageLogLevel level, string message, Exception? exception)
    {
        Trace.WriteLine(exception is null ? message : $"{message}{Environment.NewLine}{exception}");
        try
        {
            Logging.Value.Events.WriteAsync(
                level,
                "app.session.log",
                message,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["log.scope"] = "app",
                },
                exception).GetAwaiter().GetResult();
        }
        catch
        {
            // Logging must never interrupt app startup or shutdown.
        }
    }
}
