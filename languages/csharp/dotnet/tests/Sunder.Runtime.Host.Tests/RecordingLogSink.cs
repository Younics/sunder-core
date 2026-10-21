using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

internal sealed class RecordingLogSink
{
    private readonly ConcurrentQueue<RecordedLogEntry> _entries = new();

    public IReadOnlyList<RecordedLogEntry> Entries => _entries.ToArray();

    public ILogger<T> CreateLogger<T>() => new RecordingLogger<T>(this);

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }

    private void Record<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        _entries.Enqueue(new RecordedLogEntry(
            category,
            level,
            eventId,
            formatter(state, exception),
            properties,
            exception));
    }

    private sealed class RecordingLogger<T>(RecordingLogSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => sink.Record(typeof(T).FullName ?? typeof(T).Name, logLevel, eventId, state, exception, formatter);
    }
}

internal sealed record RecordedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);

internal static class RecordedRuntimePhaseAssertions
{
    private static readonly string[] RequiredProperties =
    [
        "RuntimePhase",
        "Outcome",
        "DurationMs",
        "CorrelationId",
        "RuntimeSourceId",
        "RuntimeId",
        "RuntimeVendorVersion",
        "RuntimeRid",
        "RuntimeModuleAbi",
        "RuntimeVendorChecksumAlgorithm",
        "RuntimeVendorChecksumDigest",
        "Bytes",
        "CacheHit",
        "FailureType",
    ];

    public static IReadOnlyList<RecordedLogEntry> Entries(RecordingLogSink sink)
        => sink.Entries.Where(static entry => entry.EventId.Id == 1701).ToArray();

    public static RecordedLogEntry Single(RecordingLogSink sink, string phase)
        => Assert.Single(Entries(sink), entry => string.Equals(
            entry.Properties["RuntimePhase"] as string,
            phase,
            StringComparison.Ordinal));

    public static Guid Completed(
        RecordedLogEntry entry,
        string phase,
        string outcome = "succeeded")
    {
        Assert.Equal(1701, entry.EventId.Id);
        Assert.Equal("ExecutionRuntimePhase", entry.EventId.Name);
        Assert.Equal(outcome == "succeeded" ? LogLevel.Information : LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(phase, entry.Properties["RuntimePhase"]);
        Assert.Equal(outcome, entry.Properties["Outcome"]);
        Assert.All(RequiredProperties, property => Assert.True(
            entry.Properties.ContainsKey(property),
            $"Phase '{phase}' omitted structured property '{property}'."));
        Assert.True(Assert.IsType<double>(entry.Properties["DurationMs"]) >= 0);
        var correlationId = Assert.IsType<Guid>(entry.Properties["CorrelationId"]);
        Assert.NotEqual(Guid.Empty, correlationId);
        if (outcome == "succeeded")
        {
            Assert.Null(entry.Properties["FailureType"]);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(entry.Properties["FailureType"])));
        }
        return correlationId;
    }

    public static void Identity(RecordedLogEntry entry, ExecutionRuntimeIdentity identity)
    {
        Assert.Equal(identity.SourceId.ToString().ToLowerInvariant(), entry.Properties["RuntimeSourceId"]);
        Assert.Equal(identity.RuntimeId, entry.Properties["RuntimeId"]);
        Assert.Equal(identity.VendorVersion, entry.Properties["RuntimeVendorVersion"]);
        Assert.Equal(identity.Rid, entry.Properties["RuntimeRid"]);
        Assert.Equal(identity.ModuleAbi, entry.Properties["RuntimeModuleAbi"]);
        Assert.Equal(identity.VendorChecksumAlgorithm.ToString().ToLowerInvariant(), entry.Properties["RuntimeVendorChecksumAlgorithm"]);
        Assert.Equal(identity.VendorChecksumDigest, entry.Properties["RuntimeVendorChecksumDigest"]);
    }

    public static string Flatten(RecordingLogSink sink)
        => string.Join(
            '\n',
            sink.Entries.Select(entry => entry.Message + "\n" + string.Join(
                '\n',
                entry.Properties.Select(static property => $"{property.Key}={property.Value}"))));
}
