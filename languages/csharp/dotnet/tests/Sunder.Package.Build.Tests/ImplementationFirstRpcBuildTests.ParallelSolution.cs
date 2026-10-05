using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task ParallelSolution_DirectPackageAndDistinctRoleConsumersBuildRebuildAndPreserveFailedOutputs(bool immutableTasks)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-parallel-role-solution");
        var root = ResolvePhysicalTemporaryPath(scope.RootPath);
        var packageRoot = Path.Combine(root, "package");
        var package = await CreateSingleProjectFixtureAsync(packageRoot, immutableTasks);
        var projectText = await File.ReadAllTextAsync(package);
        var foreignRid = RuntimeInformation.RuntimeIdentifier == "win-x64" ? "linux-x64" : "win-x64";
        projectText = projectText.Replace($"<RuntimeIdentifiers>{RuntimeInformation.RuntimeIdentifier}</RuntimeIdentifiers>",
            $"<RuntimeIdentifiers>{RuntimeInformation.RuntimeIdentifier};{foreignRid}</RuntimeIdentifiers><EnableRuntimePackDownload>false</EnableRuntimePackDownload><EnableAppHostPackDownload>false</EnableAppHostPackDownload>", StringComparison.Ordinal)
            .Replace("<Import Project=", "<Import Condition=\"'$(SunderPackageProject)' == 'true'\" Project=", StringComparison.Ordinal);
        var helperRoot = Path.Combine(root, "helper"); Directory.CreateDirectory(helperRoot);
        await File.WriteAllTextAsync(Path.Combine(helperRoot, "Helper.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="RejectLeakedRoleGlobals" BeforeTargets="PrepareForBuild;CoreClean;GetTargetPath">
                <Error Condition="'$(RuntimeIdentifier)' != '' or '$(SunderPackageCompilationRole)' != '' or '$(SunderPackageReferenceRole)' != ''"
                       Text="Generated role properties leaked to the source helper." />
              </Target>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(helperRoot, "Helper.cs"), "namespace SharedHelper; public static class Formatting { public static string Format(string name) => name; }");
        var packageSource = SingleProjectSource.Replace("public string Format(string name) => name;",
            "public string Format(string name) => SharedHelper.Formatting.Format(name);", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "Mixed.cs"), packageSource);
        await File.WriteAllTextAsync(package, projectText.Replace("</Project>", """
            <ItemGroup><ProjectReference Include="../helper/Helper.csproj" /></ItemGroup>
            <Target Name="ObserveCallerGlobals" BeforeTargets="_BuildSunderPackageRoles">
              <WriteLinesToFile File="$(MSBuildProjectDirectory)/observed-$(SunderParallelProbe).txt"
                                Lines="probe=$(SunderParallelProbe);role=$(SunderPackageReferenceRole)" Overwrite="true" />
            </Target>
            </Project>
            """, StringComparison.Ordinal));
        var names = new[] { "First", "Second", "Third" };
        foreach (var name in names)
        {
            var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            var framework = name == "First" ? ";TargetFramework=net10.0" : name == "Second" ? ";Platform=AnyCPU" : string.Empty;
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{SecurityElement.Escape(package)}}" Targets="GetSunderPackageRoleReference"
                                      AdditionalProperties="SunderPackageReferenceRole=worker{{framework}}" Aliases="Worker" />
                    <ProjectReference Include="{{SecurityElement.Escape(package)}}" Targets="GetSunderPackageRoleReference"
                                      AdditionalProperties="SunderPackageReferenceRole=app{{framework}}" Aliases="App" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "Program.cs"), """
                extern alias Worker;
                extern alias App;
                public static class Program
                {
                    public static void Main()
                    {
                        Console.WriteLine(typeof(Worker::Example.Generated.GreetingService).Assembly.GetName().Name);
                        Console.WriteLine(typeof(App::Example.Generated.GreetingViewModel).Assembly.GetName().Name);
                        Console.WriteLine(typeof(Worker::Example.Generated.GreetingRequest) != typeof(App::Example.Generated.GreetingRequest));
                    }
                }
                """);
        }
        var solution = Path.Combine(root, "Parallel.slnx");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"helper/Helper.csproj\" /><Project Path=\"package/SingleProject.csproj\" />"
            + string.Concat(names.Select(name => $"<Project Path=\"{name}/{name}.csproj\" />")) + "</Solution>");
        AssertSuccess(await RunDotnetAsync(root, ["restore", solution, "--nologo", "-v:quiet"]));

        // There are more contending configured instances than worker nodes. An
        // external-lock sleep cannot consume the nodes needed by the lease holder.
        AssertSuccess(await Build());
        await AssertOutputs();
        var observed = await File.ReadAllTextAsync(Path.Combine(packageRoot, "observed-preserved.txt"));
        Assert.Equal("probe=preserved" + Environment.NewLine + "role=", observed.Trim());
        var source = Path.Combine(packageRoot, "Mixed.cs");
        var dev = Path.Combine(packageRoot, "bin/Debug/net10.0/sunder-dev");
        var before = HashTree(dev);
        await File.WriteAllTextAsync(source, "invalid complete package source");
        var failed = await Build();
        Assert.NotEqual(0, failed.ExitCode);
        Assert.DoesNotContain("Timed out", failed.StandardOutput + failed.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, HashTree(dev));
        await File.WriteAllTextAsync(source, packageSource);
        AssertSuccess(await Build(rebuild: true));
        await AssertOutputs();

        Task<ProcessResult> Build(bool rebuild = false) => RunDotnetAsync(root,
            ["build", solution, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet", "-m:2", "-p:SunderParallelProbe=preserved", .. rebuild ? new[] { "-t:Rebuild" } : []]);

        async Task AssertOutputs()
        {
            Assert.False(File.Exists(Path.Combine(packageRoot, "bin/Debug/net10.0/SingleProject.dll")));
            var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(Path.Combine(packageRoot, "bin/Debug/net10.0/sunder-dev"));
            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
            Assert.Equal(2, validation.Manifest!.Targets!.Count);
            foreach (var name in names)
            {
                var output = Path.Combine(root, name, "bin/Debug/net10.0", name);
                var run = await RunDotnetAsync(root, [output + ".dll"]); AssertSuccess(run);
                Assert.Equal(new[] { "SingleProject", "SingleProject.App", "True" }, run.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
                using var deps = JsonDocument.Parse(await File.ReadAllTextAsync(output + ".deps.json"));
                var assets = deps.RootElement.GetProperty("targets").EnumerateObject().SelectMany(target => target.Value.EnumerateObject())
                    .Where(library => library.Value.TryGetProperty("runtime", out _))
                    .SelectMany(library => library.Value.GetProperty("runtime").EnumerateObject()).Select(asset => asset.Name).ToArray();
                Assert.Single(assets, asset => asset == "SingleProject.dll");
                Assert.Single(assets, asset => asset == "SingleProject.App.dll");
            }
            foreach (var role in new[] { "worker", "app" }) Assert.True(Directory.Exists(Path.Combine(packageRoot,
                "bin/Debug/net10.0/sunder-roles", role, RuntimeInformation.RuntimeIdentifier)));
        }
    }
}
