using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task SingleProject_CompletionOnlyOwnedServicesCheckAndBuildWithoutAuthoredUnit()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-completion-unit-build");
        var root = ResolvePhysicalTemporaryPath(scope.GetPath("fixture"));
        var project = await CreateSingleProjectFixtureAsync(root, immutableTasks: true);
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), """
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Services;
            [assembly: SunderPackage(Id = "example.generated", Name = "Completion Unit")]
            namespace Example.Generated;
            [SunderService(SunderServiceExposure.Public)] public interface CommandsService { ValueTask ExecuteAsync(string request); }
            [SunderServiceProvider(typeof(CommandsService))] public sealed partial class CommandsProvider : CommandsService {
                public ValueTask ExecuteAsync(string request) => ValueTask.CompletedTask;
            }
            [SunderService] internal sealed partial class PrivateService {
                public ValueTask ExecuteAsync(string request) => ValueTask.CompletedTask;
            }
            [SunderService] public sealed partial class MixedService {
                public ValueTask FlushAsync() => ValueTask.CompletedTask;
                public ValueTask ExecuteAsync(string request) => ValueTask.CompletedTask;
            }
            """);
        foreach (var target in new[] { "AcceptSunderContracts", "CheckSunderContracts", "Build" })
            AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:" + target, "-p:BuildProjectReferences=false", "-nodeReuse:false", "-nologo", "-v:quiet"]));
        Assert.False((await File.ReadAllTextAsync(Path.Combine(root, "Mixed.cs"))).Contains("class Unit", StringComparison.Ordinal));
    }
}
