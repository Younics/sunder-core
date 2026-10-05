using System.Diagnostics;
using System.Text.Json;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppStartupTraceTests
{
    [Fact]
    public void WriteShellRevealed_WritesStructuredMonotonicInterval()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sunder-app-startup-trace-{Guid.NewGuid():N}");
        var tracePath = Path.Combine(root, "app-startup.json");
        try
        {
            AppStartupTrace.WriteShellRevealed(tracePath, 123.456, 42, openCoreShell: false);

            using var document = JsonDocument.Parse(File.ReadAllBytes(tracePath));
            var rootElement = document.RootElement;
            Assert.Equal(1, rootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                AppStartupTrace.ShellRevealedEvent,
                rootElement.GetProperty("event").GetString()
            );
            Assert.Equal(123.456, rootElement.GetProperty("elapsedMilliseconds").GetDouble());
            Assert.Equal(42, rootElement.GetProperty("processId").GetInt32());
            Assert.False(rootElement.GetProperty("openCoreShell").GetBoolean());
            var traceEvent = Assert.Single(rootElement.GetProperty("events").EnumerateArray());
            Assert.Equal(1, traceEvent.GetProperty("sequence").GetInt32());
            Assert.Equal(
                rootElement.GetProperty("correlationId").GetString(),
                traceEvent.GetProperty("correlationId").GetString());
            Assert.Equal(
                AppStartupTrace.ShellRevealedEvent,
                traceEvent.GetProperty("event").GetString());
            Assert.Equal("succeeded", traceEvent.GetProperty("outcome").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Session_BuffersDetailedEventsUntilShellRevealAndPreservesMonotonicOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sunder-app-startup-trace-{Guid.NewGuid():N}");
        var tracePath = Path.Combine(root, "app-startup.json");
        try
        {
            var startupTimestamp = Stopwatch.GetTimestamp();
            var session = AppStartupTrace.CreateSession(tracePath, startupTimestamp, processId: 42);
            var generationId = Guid.NewGuid().ToString("N");
            var operation = Assert.IsType<AppStartupTraceOperation>(session.BeginOperation(
                AppStartupTrace.PackageModuleLoadStartedEvent,
                AppStartupTrace.PackageModuleLoadCompletedEvent,
                new AppStartupTraceMetadata(
                    PackageId: "sunder.agent",
                    PackageVersion: "1.2.3",
                    RuntimeIdentifier: "osx-arm64",
                    AppGenerationId: generationId,
                    SessionGeneration: 7),
                queuedTimestamp: startupTimestamp));

            Assert.False(File.Exists(tracePath));
            Assert.True(operation.Complete(bytesRead: 64));
            Assert.False(operation.Complete());
            Assert.False(File.Exists(tracePath));

            session.RecordShellRevealed(elapsedMilliseconds: 1000, openCoreShell: false);

            using var document = JsonDocument.Parse(File.ReadAllBytes(tracePath));
            var events = document.RootElement.GetProperty("events").EnumerateArray().ToArray();
            Assert.Equal(3, events.Length);
            Assert.Equal([1, 2, 3], events.Select(item => item.GetProperty("sequence").GetInt32()));
            Assert.Equal(
                [
                    AppStartupTrace.PackageModuleLoadStartedEvent,
                    AppStartupTrace.PackageModuleLoadCompletedEvent,
                    AppStartupTrace.ShellRevealedEvent,
                ],
                events.Select(item => item.GetProperty("event").GetString()));
            Assert.All(events, item => Assert.Equal(
                document.RootElement.GetProperty("correlationId").GetString(),
                item.GetProperty("correlationId").GetString()));
            Assert.True(events[0].GetProperty("queueDurationMilliseconds").GetDouble() >= 0);
            Assert.True(events[1].GetProperty("activeDurationMilliseconds").GetDouble() >= 0);
            Assert.Equal(64, events[1].GetProperty("bytesRead").GetInt64());
            Assert.Equal("sunder.agent", events[1].GetProperty("packageId").GetString());
            Assert.Equal(generationId, events[1].GetProperty("appGenerationId").GetString());
            Assert.Equal(7, events[1].GetProperty("sessionGeneration").GetInt64());
            Assert.True(events
                .Select(item => item.GetProperty("elapsedMilliseconds").GetDouble())
                .SequenceEqual(events
                    .Select(item => item.GetProperty("elapsedMilliseconds").GetDouble())
                    .Order()));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
