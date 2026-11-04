using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Logging;

namespace Sunder.Sdk.Avalonia.Hosting;

internal sealed class AppLoggingAdapter(AppChannel channel) : IPackageLogging
{
    public ILoggerFactory LoggerFactory { get; } = new Factory(channel);
    public IPackageEventLogger Events { get; } = new EventsAdapter(channel);

    private sealed class Factory(AppChannel channel) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(channel, categoryName);
        public void AddProvider(ILoggerProvider provider) => provider.Dispose();
        public void Dispose() { }
    }
    private sealed class Logger(AppChannel channel, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => channel.Get<bool>("logging.enabled", new { category, level });
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            channel.Get<object?>("logging.write", new { category, level, eventId = eventId.Id, eventName = eventId.Name,
                message = formatter(state, exception), exception = exception?.ToString() });
        }
    }
    private sealed class EventsAdapter(AppChannel channel) : IPackageEventLogger
    {
        public ValueTask WriteAsync(PackageLogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Logging arbitrary package objects must not traverse their object graph.
            var values = attributes?.ToDictionary(pair => pair.Key, pair => Scalar(pair.Value));
            return channel.Call("logging.event", new { level, eventName, message, attributes = values, exception = exception?.ToString() }, cancellationToken);
        }
        private static object? Scalar(object? value) => value switch
        {
            null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
            float number when float.IsFinite(number) => number,
            double number when double.IsFinite(number) => number,
            JsonElement element => element.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }
}
