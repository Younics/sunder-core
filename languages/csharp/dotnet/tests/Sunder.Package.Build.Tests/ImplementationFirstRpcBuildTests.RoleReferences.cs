using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task SingleProject_RoleReferencesPreserveDistinctAliasesAndResolveWithoutRebuilding()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-role-references");
        var root = ResolvePhysicalTemporaryPath(scope.GetPath("fixture"));
        var project = await CreateSingleProjectFixtureAsync(root, immutableTasks: true);
        var source = Path.Combine(root, "Mixed.cs");
        var worker = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/worker", RuntimeInformation.RuntimeIdentifier, "SingleProject.dll");
        var app = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/app", RuntimeInformation.RuntimeIdentifier, "SingleProject.App.dll");
        var consumer = await AssertRoleProjectReferencesAsync(scope.GetPath("consumer"), project, source, worker, app);
        await File.WriteAllTextAsync(source, SingleProjectSource[..SingleProjectSource.IndexOf("[SunderPackageView", StringComparison.Ordinal)]);
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]));
        Assert.False(File.Exists(app));
        var unavailable = await RunDotnetAsync(Path.GetDirectoryName(consumer)!,
            ["build", consumer, "--no-restore", "--disable-build-servers", "-p:BuildProjectReferences=false", "--nologo", "-v:quiet"]);
        Assert.NotEqual(0, unavailable.ExitCode);
        Assert.Contains("package-authoring.reference-role-unavailable", unavailable.StandardOutput + unavailable.StandardError, StringComparison.Ordinal);
    }

    // Reuse the ordinary single-project integration fixture and its already-built
    // roles. The consumer is a normal SDK project, not another repository project.
    private static async Task<string> AssertRoleProjectReferencesAsync(string root, string packageProject, string sourcePath, string worker, string app)
    {
        root = ResolvePhysicalTemporaryPath(root);
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "RoleConsumer.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{SecurityElement.Escape(packageProject)}}" Targets="GetSunderPackageRoleReference"
                                  AdditionalProperties="SunderPackageReferenceRole=worker" Aliases="Worker" />
                <ProjectReference Include="{{SecurityElement.Escape(packageProject)}}" Targets="GetSunderPackageRoleReference"
                                  AdditionalProperties="SunderPackageReferenceRole=app" Aliases="App" />
              </ItemGroup>
              <Target Name="RecordRoleReferences" AfterTargets="ResolveProjectReferences">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/resolved.txt" Lines="@(_ResolvedProjectReferencePaths)" Overwrite="true" />
              </Target>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "Probe.cs"), """
            extern alias Worker;
            extern alias App;
            public static class Probe
            {
                public static void Main() => Console.WriteLine(RoleLocalTypesAreDistinct);
                public static Type WorkerImplementation => typeof(Worker::Example.Generated.GreetingService);
                public static Type AppImplementation => typeof(App::Example.Generated.GreetingViewModel);
                public static bool RoleLocalTypesAreDistinct => typeof(Worker::Example.Generated.GreetingRequest) != typeof(App::Example.Generated.GreetingRequest);
            }
            """);
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--disable-parallel", "--nologo"]));
        AssertSuccess(await Build(false));
        AssertResolvedRoles();
        using (var deps = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "bin/Debug/net10.0/RoleConsumer.deps.json"))))
        {
            var runtimeAssets = deps.RootElement.GetProperty("targets").EnumerateObject().SelectMany(target => target.Value.EnumerateObject())
                .Where(library => library.Value.TryGetProperty("runtime", out _))
                .SelectMany(library => library.Value.GetProperty("runtime").EnumerateObject()).Select(asset => asset.Name).ToArray();
            Assert.Single(runtimeAssets, name => name == "SingleProject.dll");
            Assert.Single(runtimeAssets, name => name == "SingleProject.App.dll");
        }
        var execution = await RunDotnetAsync(root, [Path.Combine(root, "bin/Debug/net10.0/RoleConsumer.dll")]);
        AssertSuccess(execution); Assert.Equal("True", execution.StandardOutput.Trim());
        using (var stream = File.OpenRead(Path.Combine(root, "bin/Debug/net10.0/RoleConsumer.dll")))
        using (var pe = new PEReader(stream))
        {
            var metadata = pe.GetMetadataReader();
            var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
            Assert.Contains("SingleProject", references);
            Assert.Contains("SingleProject.App", references);
        }

        // A no-build project reference must resolve existing current-role output
        // without compiling invalid, newly edited package source.
        var source = await File.ReadAllTextAsync(sourcePath);
        var times = new[] { File.GetLastWriteTimeUtc(worker), File.GetLastWriteTimeUtc(app) };
        try
        {
            await File.WriteAllTextAsync(sourcePath, "invalid uncompiled package source");
            AssertSuccess(await Build(true));
            AssertResolvedRoles();
            Assert.Equal(times, new[] { File.GetLastWriteTimeUtc(worker), File.GetLastWriteTimeUtc(app) });
            foreach (var role in new[] { "", "combined" })
            {
                var invalidRole = await RunDotnetAsync(Path.GetDirectoryName(packageProject)!,
                    ["msbuild", packageProject, "-t:GetSunderPackageRoleReference", "-p:SunderPackageReferenceRole=" + role, "-nodeReuse:false", "-nologo", "-v:quiet"]);
                Assert.NotEqual(0, invalidRole.ExitCode);
                Assert.Contains("package-authoring.reference-role-invalid", invalidRole.StandardOutput + invalidRole.StandardError, StringComparison.Ordinal);
            }
        }
        finally { await File.WriteAllTextAsync(sourcePath, source); }
        return project;

        Task<ProcessResult> Build(bool noReferences) => RunDotnetAsync(root,
            ["build", project, "--no-restore", "--disable-build-servers", "-p:BuildProjectReferences=" + (!noReferences).ToString().ToLowerInvariant(), "--nologo", "-v:quiet"]);
        void AssertResolvedRoles()
        {
            Assert.Equal(new[] { worker, app }.Order(StringComparer.Ordinal),
                File.ReadAllLines(Path.Combine(root, "resolved.txt")).Order(StringComparer.Ordinal));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(packageProject)!, "bin/Debug/net10.0/SingleProject.dll")));
        }
    }
}
