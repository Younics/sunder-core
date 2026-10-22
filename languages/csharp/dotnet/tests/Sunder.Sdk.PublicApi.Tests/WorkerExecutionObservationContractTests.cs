using System.Reflection;
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class WorkerExecutionObservationContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void ReferenceRejectsMissingOpaqueIdentity(string? value)
        => Assert.Throws<ArgumentException>(() => new PackageWorkerExecutionReference(Guid.NewGuid(), value!));

    [Fact]
    public void ReferenceRequiresOriginalHostAndBoundsOpaqueValue()
    {
        Assert.Throws<ArgumentException>(() => new PackageWorkerExecutionReference(Guid.Empty, "reference"));
        Assert.Throws<ArgumentException>(() => new PackageWorkerExecutionReference(Guid.NewGuid(), new('x', 257)));
        var reference = new PackageWorkerExecutionReference(Guid.NewGuid(), new('x', 256));
        var (runtime, value) = reference;
        Assert.Equal(reference.RuntimeInstanceId, runtime);
        Assert.Equal(reference.Value, value);
        Assert.Null(typeof(PackageWorkerExecutionReference).GetProperty(nameof(reference.Value))!.SetMethod);
    }

    [Fact]
    public void ObservationRejectsUnknownEnumAndConfirmationTimeWithoutConfirmedCleanup()
    {
        var reference = new PackageWorkerExecutionReference(Guid.NewGuid(), "original-execution");
        Assert.Throws<ArgumentNullException>(() => new PackageWorkerExecutionObservation(null!, PackageWorkerExecutionState.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageWorkerExecutionObservation(reference, (PackageWorkerExecutionState)99));
        Assert.Throws<ArgumentException>(() => new PackageWorkerExecutionObservation(reference, PackageWorkerExecutionState.CleanupPending)
            { ConfirmedAtUtc = DateTimeOffset.UtcNow });
        var unknown = new PackageWorkerExecutionObservation(reference, PackageWorkerExecutionState.Unknown);
        Assert.Null(unknown.ConfirmedAtUtc);
        Assert.Null((unknown with { }).ConfirmedAtUtc);
        var confirmed = new PackageWorkerExecutionObservation(reference, PackageWorkerExecutionState.CleanupConfirmed)
            { ConfirmedAtUtc = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(2)) };
        Assert.Equal(TimeSpan.Zero, confirmed.ConfirmedAtUtc!.Value.Offset);
    }

    [Fact]
    public void ConfirmedObservationRoundTripsWithoutChangingOriginalHostIdentity()
    {
        var reference = new PackageWorkerExecutionReference(Guid.NewGuid(), "historical-execution");
        var observation = new PackageWorkerExecutionObservation(reference, PackageWorkerExecutionState.CleanupConfirmed)
            { ConfirmedAtUtc = DateTimeOffset.UtcNow };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<PackageWorkerExecutionObservation>(JsonSerializer.Serialize(observation, options), options);
        Assert.Equal(observation, restored);
        Assert.Equal(reference.RuntimeInstanceId, restored!.Reference.RuntimeInstanceId);
    }

    [Fact]
    public void DevelopmentRevisionValidatesOriginAndCanonicalIdentityIndependentlyOfReadiness()
    {
        var runtime = Guid.NewGuid();
        var hash = new string('a', 64);
        Assert.Throws<ArgumentException>(() => new PackageDevelopmentRevision(Guid.Empty, 4, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageDevelopmentRevision(runtime, -1, hash));
        foreach (var invalid in new[] { null, "", "source", new string('A', 64), new string('a', 63) })
            Assert.Throws<ArgumentException>(() => new PackageDevelopmentRevision(runtime, 4, invalid!));
        var revision = new PackageDevelopmentRevision(runtime, 4, hash);
        Assert.Equal(revision, JsonSerializer.Deserialize<PackageDevelopmentRevision>(JsonSerializer.Serialize(revision)));
        var status = new PackageDevelopmentStatus("package", false, false, PackageDevelopmentRuntimeReadiness.Unknown, null, false);
        var (package, loaded, watch, readiness, error, installed) = status;
        Assert.Equal("package", package);
        Assert.False(loaded || watch || installed);
        Assert.Equal(PackageDevelopmentRuntimeReadiness.Unknown, readiness);
        Assert.Equal(PackageDevelopmentHostState.Unknown, status.HostState);
        Assert.Equal(PackageDevelopmentAppReadiness.Unknown, status.AppReadiness);
        Assert.Null(error);
        Assert.Null(status.RuntimeRevision);
        Assert.Null(status.AppliedRevision);
    }

    [Fact]
    public void NewContractsDeclareOnlyTheirOwnCapabilityAndDoNotExtendDevelopmentInterfaces()
    {
        foreach (var type in new[] { typeof(IPackageWorkerExecutionObservations), typeof(PackageWorkerExecutionReference),
                     typeof(PackageWorkerExecutionObservation), typeof(PackageWorkerExecutionState) })
            Assert.Equal(SunderSdkCapabilities.WorkerExecutionObservationsV1,
                Assert.Single(type.GetCustomAttributes<SunderSdkCapabilityAttribute>()).Capability);
        Assert.Equal(["LoadAsync"], typeof(IPackageDevelopmentService).GetMethods().Select(method => method.Name));
        Assert.Equal(["GetStatusAsync", "SetWatchAsync", "get_PackageId"],
            typeof(IPackageDevelopmentSession).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal("package-development-revisions.v1", SunderSdkCapabilities.PackageDevelopmentRevisionsV1);
    }
}
