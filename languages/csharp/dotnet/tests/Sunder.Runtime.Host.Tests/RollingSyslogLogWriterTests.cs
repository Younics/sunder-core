using System.Globalization;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Host.Infrastructure.Logging;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RollingSyslogLogWriterTests
{
    [Fact]
    public void Constructor_PreservesPreExistingRuntimeAndEventJsonLinesFiles()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-rolling-log-tests");
        var runtimePath = directory.WriteAllText("runtime-2024-01-01.jsonl", "legacy runtime");
        var eventsPath = directory.WriteAllText("events-2024-01-01.jsonl", "legacy events");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));

        using var runtimeWriter = new RollingSyslogLogWriter(
            directory.RootPath,
            "runtime",
            TimeSpan.FromDays(7),
            clock);
        using var eventsWriter = new RollingSyslogLogWriter(
            directory.RootPath,
            "events",
            TimeSpan.FromDays(7),
            clock);

        Assert.Equal("legacy runtime", File.ReadAllText(runtimePath));
        Assert.Equal("legacy events", File.ReadAllText(eventsPath));
    }

    [Fact]
    public void Write_WritesCurrentLogAndAppliesRetentionAtStartupAndRollover()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-rolling-log-tests");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var expiredPath = directory.WriteAllText(LogName("runtime", today.AddDays(-8)), "expired");
        var retainedPath = directory.WriteAllText(LogName("runtime", today.AddDays(-7)), "retained");

        using var writer = new RollingSyslogLogWriter(
            directory.RootPath,
            "runtime",
            TimeSpan.FromDays(7),
            clock);

        Assert.False(File.Exists(expiredPath));
        Assert.True(File.Exists(retainedPath));

        writer.Write("first");
        var firstPath = directory.GetPath(LogName("runtime", today));
        Assert.Equal("first" + Environment.NewLine, File.ReadAllText(firstPath));

        clock.Advance(TimeSpan.FromDays(8));
        writer.Write("second");

        var rolloverDate = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var rolloverPath = directory.GetPath(LogName("runtime", rolloverDate));
        Assert.False(File.Exists(firstPath));
        Assert.False(File.Exists(retainedPath));
        Assert.Equal("second" + Environment.NewLine, File.ReadAllText(rolloverPath));
    }

    private static string LogName(string prefix, DateOnly date)
        => $"{prefix}-{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log";
}
