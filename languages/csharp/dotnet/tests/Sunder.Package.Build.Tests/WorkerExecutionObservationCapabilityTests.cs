using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class WorkerExecutionObservationCapabilityTests
{
    [Theory]
    [InlineData(nameof(IPackageWorkerExecutionObservations))]
    [InlineData(nameof(PackageWorkerExecutionReference))]
    [InlineData(nameof(PackageWorkerExecutionObservation))]
    [InlineData(nameof(PackageWorkerExecutionState))]
    public void MetadataCatalogResolvesNewObservationContracts(string name)
    {
        var diagnostics = new List<string>();
        var catalog = SdkCapabilityCatalog.Create([typeof(IPackageContext).Assembly.Location], diagnostics);
        var result = catalog.ResolveType("Sunder.Sdk", "Sunder.Sdk.Abstractions", name);
        Assert.Empty(diagnostics);
        Assert.True(result.IsResolved);
        Assert.Equal([SunderSdkCapabilities.WorkerExecutionObservationsV1], result.DeclaredCapabilities);
    }

    [Fact]
    public void ExistingDevelopmentServiceDoesNotAcquireNewObservationRequirements()
    {
        var diagnostics = new List<string>();
        var catalog = SdkCapabilityCatalog.Create([typeof(IPackageContext).Assembly.Location], diagnostics);
        var result = catalog.ResolveType("Sunder.Sdk", "Sunder.Sdk.Abstractions", nameof(IPackageDevelopmentService));
        Assert.Empty(diagnostics);
        Assert.Equal([SunderSdkCapabilities.PackageDevelopmentV1], result.DeclaredCapabilities);
    }

    [Theory]
    [InlineData(nameof(PackageDevelopmentStatus), "get_RuntimeRevision", SunderSdkCapabilities.PackageDevelopmentRevisionsV1)]
    [InlineData(nameof(PackageDevelopmentStatus), "get_AppliedRevision", SunderSdkCapabilities.PackageDevelopmentRevisionsV1)]
    [InlineData(nameof(IPackageDevelopmentSession), "ReloadAsync", SunderSdkCapabilities.PackageDevelopmentReloadV1)]
    public void DevelopmentAnnotationsAreReadAtMemberScope(string type, string member, string capability)
    {
        // The coordinating draft owns these additive members. This test deliberately
        // checks their metadata instead of adding or redefining the revision DTO here.
        var diagnostics = new List<string>();
        var catalog = SdkCapabilityCatalog.Create([typeof(IPackageContext).Assembly.Location], diagnostics);
        var result = catalog.ResolveMember("Sunder.Sdk", "Sunder.Sdk.Abstractions", type, member);
        Assert.Empty(diagnostics);
        Assert.True(result.IsResolved);
        Assert.Contains(capability, result.DeclaredCapabilities);
        Assert.DoesNotContain(SunderSdkCapabilities.WorkerExecutionObservationsV1, result.DeclaredCapabilities);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompositionAcceptsAppObservationInjectionAndRejectsWorkerInjection(bool nullable, bool optional)
    {
        var parameter = $"Sunder.Sdk.Abstractions.IPackageWorkerExecutionObservations{(nullable ? "?" : "")} observations{(optional ? " = null" : "")}";
        var app = SunderPackageCompositionPlannerTests.Analyze(Source + $$"""
            [Sunder.Sdk.Packaging.SunderPackageView("example.view", "View", ViewModel = typeof(ViewModel))]
            public sealed class View : Avalonia.Controls.UserControl;
            public sealed class ViewModel({{parameter}})
            { public object? Observations => observations; }
            """);
        Assert.True(app.Success, string.Join(Environment.NewLine, app.RolePlan.Diagnostics.Select(error => error.Message)));
        var construction = Assert.Single(app.Composition!.App, entry => entry.Type.Name == "ViewModel");
        Assert.Equal("IPackageWorkerExecutionObservations", Assert.Single(construction.Arguments).ServiceType.Name);
        Assert.Contains("dependencies.GetRequiredService<global::Sunder.Sdk.Abstractions.IPackageWorkerExecutionObservations>()",
            SunderPackageCompositionEmitter.EmitApp(app).Text, StringComparison.Ordinal);

        var worker = SunderPackageCompositionPlannerTests.Analyze(Source.Replace("CounterService()",
            $"CounterService({parameter})", StringComparison.Ordinal));
        if (optional)
        {
            // An App-only service never becomes a Worker grant just because its
            // constructor parameter is nullable and has a local default.
            Assert.True(worker.Success);
            Assert.Empty(Assert.Single(worker.Composition!.Worker, entry => entry.Type.Name == "CounterService").Arguments);
            Assert.DoesNotContain("dependencies.GetRequiredService<global::Sunder.Sdk.Abstractions.IPackageWorkerExecutionObservations>()",
                SunderPackageCompositionEmitter.EmitWorker(worker).Text, StringComparison.Ordinal);
        }
        else Assert.Contains(worker.RolePlan.Diagnostics, error => error.Id == "package-authoring.di-unresolved");
    }

    private const string Source = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        namespace Example;
        [SunderService]
        public sealed partial class CounterService()
        {
            public Task<long> GetAsync() => Task.FromResult(1L);
        }
        """;
}
