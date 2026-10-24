using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class WorkerExecutionObservationCapabilityProfileTests
{
    [Theory]
    [InlineData(SunderSdkCapabilities.WorkerExecutionObservationsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentRevisionsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentReloadV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    [InlineData(SunderSdkCapabilities.AppActivationServicesV1)]
    public void ObservationCapabilitiesAreAppOnlyAndRejectedBeforeWorkerLaunch(string capability)
    {
        var app = new SunderPackageTargetManifest
        {
            Role = "app", Rid = "win-x64", Kind = "avalonia", EntryPoint = "App.dll", TargetFramework = "net10.0",
            SdkVersion = "1.1.0", RequiredHostCapabilities = [SunderSdkCapabilities.Baseline11V1, capability],
        };
        Assert.Empty(SunderSdkCompatibilityProfile.Validate("test.package", new("app", "win-x64"), app));
        var worker = new SunderPackageTargetManifest
        {
            Role = "runtime", Rid = "win-x64", Kind = SunderPackageFormat.WorkerTargetKind, EntryPoint = "Worker.dll",
            TargetFramework = "net10.0", SdkVersion = "1.1.0",
            RequiredHostCapabilities = [SunderSdkCapabilities.Baseline11V1, SunderWorkerProtocol.V1Capability, capability],
        };
        var error = Assert.Single(SunderSdkCompatibilityProfile.Validate("test.package", new("runtime", "win-x64"), worker));
        Assert.Contains("Worker Protocol V1 does not implement", error, StringComparison.Ordinal);
        Assert.False(SunderSdkCompatibilityProfile.WorkerV1CapabilityClassification[capability]);
    }

    [Theory]
    [InlineData(SunderSdkCapabilities.WorkerExecutionObservationsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentRevisionsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentReloadV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
    [InlineData(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    [InlineData(SunderSdkCapabilities.AppActivationServicesV1)]
    public void DotnetAppObservationCapabilitiesCannotBeAdvertisedForWebTargets(string capability)
    {
        var web = new SunderPackageTargetManifest
        {
            Role = "app", Rid = "win-x64", Kind = "web", EntryPoint = "index.html", SdkVersion = "1.1.0",
            RequiredHostCapabilities = [SunderSdkCapabilities.Baseline11V1, capability],
        };
        var error = Assert.Single(SunderSdkCompatibilityProfile.Validate("test.package", new("app", "win-x64"), web));
        Assert.Contains(capability, error, StringComparison.Ordinal);
        Assert.Contains("managed App", error, StringComparison.Ordinal);
    }
}
