using Sunder.Runtime.Host.Services;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class ExecutionRuntimePhaseTelemetryTests
{
    [Fact]
    public void PhaseNames_AreTheElevenExactStableValues()
    {
        string[] expected =
        [
            "runtime.plan",
            "runtime.download",
            "runtime.inspect",
            "runtime.extract",
            "runtime.publish-root",
            "runtime.bind",
            "runtime.health-check",
            "runtime.launch-resolve",
            "runtime.process-start",
            "runtime.lease",
            "runtime.gc",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), ExecutionRuntimePhaseNames.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NestedScopesPreserveAndRestoreAmbientCorrelation()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        var operationCorrelation = Guid.NewGuid();
        var nestedCorrelation = Guid.NewGuid();

        Assert.Equal(Guid.Empty, telemetry.CurrentCorrelationId);
        using (telemetry.BeginOperation(operationCorrelation))
        {
            Assert.Equal(operationCorrelation, telemetry.CurrentCorrelationId);
            using (var outer = telemetry.BeginPhase(ExecutionRuntimePhaseNames.Plan))
            {
                Assert.Equal(operationCorrelation, telemetry.CurrentCorrelationId);
                using (var nested = telemetry.BeginPhase(
                           ExecutionRuntimePhaseNames.Download,
                           correlationId: nestedCorrelation))
                {
                    Assert.Equal(nestedCorrelation, telemetry.CurrentCorrelationId);
                    nested.Succeeded();
                }
                Assert.Equal(operationCorrelation, telemetry.CurrentCorrelationId);
                outer.Succeeded();
            }
            Assert.Equal(operationCorrelation, telemetry.CurrentCorrelationId);
        }
        Assert.Equal(Guid.Empty, telemetry.CurrentCorrelationId);

        Assert.Equal(
            operationCorrelation,
            RecordedRuntimePhaseAssertions.Completed(
                RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Plan),
                ExecutionRuntimePhaseNames.Plan));
        Assert.Equal(
            nestedCorrelation,
            RecordedRuntimePhaseAssertions.Completed(
                RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Download),
                ExecutionRuntimePhaseNames.Download));
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task OfficialLaunchProcessLeaseHealthAndGarbageCollectionEmitCorrelatedSafeTelemetry()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        var fixture = new OfficialRuntimeFixture(telemetry);
        await fixture.InitializeAsync();
        try
        {
            logs.Clear();
            var package = fixture.CreatePackage(
                SyntheticRuntimeKind.DotNet,
                "runtime.telemetry",
                "normal");
            var activated = await fixture.CreateWorkerAsync(package);
            try
            {
                await activated.Worker.ComposeAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await activated.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await activated.Worker.DisposeAsync();
            }

            await fixture.Store.CollectGarbageAsync(TimeSpan.Zero, CancellationToken.None);

            var process = RecordedRuntimePhaseAssertions.Single(
                logs,
                ExecutionRuntimePhaseNames.ProcessStart);
            var correlationId = RecordedRuntimePhaseAssertions.Completed(
                process,
                ExecutionRuntimePhaseNames.ProcessStart);
            var correlated = RecordedRuntimePhaseAssertions.Entries(logs)
                .Where(entry => Equals(entry.Properties["CorrelationId"], correlationId))
                .ToArray();
            Assert.Equal(
                new[]
                {
                    ExecutionRuntimePhaseNames.HealthCheck,
                    ExecutionRuntimePhaseNames.LaunchResolve,
                    ExecutionRuntimePhaseNames.Lease,
                    ExecutionRuntimePhaseNames.ProcessStart,
                }.Order(StringComparer.Ordinal),
                correlated
                    .Select(entry => Assert.IsType<string>(entry.Properties["RuntimePhase"]))
                    .Order(StringComparer.Ordinal));
            foreach (var entry in correlated)
            {
                var phase = Assert.IsType<string>(entry.Properties["RuntimePhase"]);
                Assert.Equal(correlationId, RecordedRuntimePhaseAssertions.Completed(entry, phase));
                RecordedRuntimePhaseAssertions.Identity(entry, fixture.DotNetReceipt.Identity);
            }

            var health = Assert.Single(correlated, entry => Equals(
                entry.Properties["RuntimePhase"],
                ExecutionRuntimePhaseNames.HealthCheck));
            Assert.Equal(0L, health.Properties["Bytes"]);
            Assert.Equal(true, health.Properties["CacheHit"]);
            Assert.Null(process.Properties["Bytes"]);
            Assert.Null(process.Properties["CacheHit"]);

            var garbageCollection = RecordedRuntimePhaseAssertions.Single(
                logs,
                ExecutionRuntimePhaseNames.GarbageCollection);
            var garbageCollectionCorrelation = RecordedRuntimePhaseAssertions.Completed(
                garbageCollection,
                ExecutionRuntimePhaseNames.GarbageCollection);
            Assert.NotEqual(correlationId, garbageCollectionCorrelation);
            Assert.Null(garbageCollection.Properties["RuntimeId"]);
            Assert.Null(garbageCollection.Properties["RuntimeVendorVersion"]);
            Assert.Null(garbageCollection.Properties["RuntimeVendorChecksumDigest"]);
            Assert.Null(garbageCollection.Properties["RuntimeRid"]);
            Assert.Null(garbageCollection.Properties["Bytes"]);
            Assert.Null(garbageCollection.Properties["CacheHit"]);

            var flattened = RecordedRuntimePhaseAssertions.Flatten(logs);
            Assert.DoesNotContain(fixture.RootPath, flattened, StringComparison.Ordinal);
            Assert.DoesNotContain(package.EntryAssemblyPath!, flattened, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
