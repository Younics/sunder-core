using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task SingleProject_NugetAndProjectAssetsStayWithinTheirRole(bool sourceSdk)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-role-restore-build");
        var root = ResolvePhysicalTemporaryPath(scope.CreateDirectory("package"));
        var backend = ResolvePhysicalTemporaryPath(scope.CreateDirectory("backend"));
        var feed = scope.CreateDirectory("offline-feed");
        var repository = FindRepositoryRoot();
        var packages = Path.Combine(repository, "languages/csharp/dotnet/packages");
        var baseline = Path.Combine(repository, "languages/csharp/dotnet/tests/Sunder.Package.Build.TestFixtures/ImplementationFirstRpc");
        File.Copy(Path.Combine(baseline, "sunder-contracts.lock.json"), Path.Combine(root, "sunder-contracts.lock.json"));
        await File.WriteAllTextAsync(Path.Combine(backend, "Backend.Library.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.13" /></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(backend, "Engine.cs"), """
            namespace Backend;
            public static class Engine
            {
                public static string Render(string name)
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "select $name";
                    command.Parameters.AddWithValue("$name", name);
                    return (string)command.ExecuteScalar()!;
                }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), "using Avalonia.Controls;\n" + SingleProjectSource
            .Replace("public string Format(string name) => name;", "public string Format(string name) => Backend.Engine.Render(name);", StringComparison.Ordinal)
            .Replace("GreetingView : Avalonia.Controls.UserControl", "GreetingView : UserControl", StringComparison.Ordinal));
        var taskAssembly = typeof(GenerateSunderRpcBindingsTask).Assembly.Location;
        var sdkReferences = sourceSdk
            ? string.Join(Environment.NewLine, new[] { "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia" }
                .Select(name => $"<ProjectReference Include=\"{SecurityElement.Escape(Path.Combine(packages, name, name + ".csproj"))}\" />"))
            : string.Join(Environment.NewLine, new[] { "Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia" }
                .Select(name => $"<Reference Include=\"{name}\" HintPath=\"{SecurityElement.Escape(Path.Combine(Path.GetDirectoryName(taskAssembly)!, name + ".dll"))}\" Private=\"true\" />"));
        var project = Path.Combine(root, "RestoreRoles.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
                <Version>7.6.5</Version><SunderPackageProject>true</SunderPackageProject><NuGetAudit>false</NuGetAudit>
                <RuntimeIdentifiers>{{RuntimeInformation.RuntimeIdentifier}}</RuntimeIdentifiers><SelfContained>false</SelfContained><UseAppHost>false</UseAppHost>
                <SunderPackageBuildTaskAssembly>{{SecurityElement.Escape(taskAssembly)}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildTaskSourceIsImmutable>true</SunderPackageBuildTaskSourceIsImmutable>
                <SunderPackageBuildProject>no-task-source.csproj</SunderPackageBuildProject>
              </PropertyGroup>
              <ItemGroup>
                {{sdkReferences}}
                <ProjectReference Include="../backend/Backend.Library.csproj" />
                <PackageReference Include="Avalonia" Version="12.1.4" />
                <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.13" />
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.13" />
              </ItemGroup>
              <Import Project="{{SecurityElement.Escape(Path.Combine(packages, "Sunder.Package.Build/build/Sunder.Package.Build.targets"))}}" />
              <Target Name="AssertRoleBuildImports" BeforeTargets="CoreCompile" Condition="'$(SunderPackageCompilationRole)' != ''">
                <Error Condition="'$(SunderPackageCompilationRole)' == 'worker' and '$(AvaloniaBuildTasksLocation)' != ''" Text="UI NuGet build imports leaked into Worker." />
                <Error Condition="'$(SunderPackageCompilationRole)' == 'app' and '$(AvaloniaBuildTasksLocation)' == ''" Text="App lost its evaluated UI build imports." />
              </Target>
            </Project>
            """);
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--disable-parallel", "--nologo"]));
        var offline = $"-p:RestoreSources={feed}";
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet", offline]));
        var worker = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/worker", RuntimeInformation.RuntimeIdentifier);
        var app = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/app", RuntimeInformation.RuntimeIdentifier);
        Assert.True(File.Exists(Path.Combine(worker, "Backend.Library.dll")));
        Assert.True(File.Exists(Path.Combine(worker, "Microsoft.Data.Sqlite.dll")));
        Assert.Contains(Directory.EnumerateFiles(worker, "*", SearchOption.AllDirectories), path => Path.GetFileName(path) is "libe_sqlite3.dylib" or "libe_sqlite3.so" or "e_sqlite3.dll");
        Assert.DoesNotContain(Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories), path => Path.GetFileName(path).Contains("sqlite", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(app, "Backend.Library.dll")));
        Assert.DoesNotContain("Avalonia", await File.ReadAllTextAsync(Path.Combine(worker, "RestoreRoles.deps.json")), StringComparison.Ordinal);
        var appDependencies = await File.ReadAllTextAsync(Path.Combine(app, "RestoreRoles.App.deps.json"));
        Assert.DoesNotContain("Sqlite", appDependencies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Backend.Library", appDependencies, StringComparison.Ordinal);
        var nativeProbe = new RoleDependencyLoadContext(worker);
        try
        {
            var assembly = nativeProbe.LoadFromAssemblyPath(Path.Combine(worker, "Backend.Library.dll"));
            Assert.Equal("native role closure", assembly.GetType("Backend.Engine", throwOnError: true)!.GetMethod("Render")!.Invoke(null, ["native role closure"]));
        }
        finally { nativeProbe.Unload(); }
        using (var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "obj/Debug/net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc/package-role-plan.v1.json"))))
        {
            Assert.Contains(plan.RootElement.GetProperty("workerDependencies").GetProperty("libraries").EnumerateArray(), value => value.GetString()!.StartsWith("SQLitePCLRaw.lib.e_sqlite3/", StringComparison.Ordinal));
            Assert.DoesNotContain(plan.RootElement.GetProperty("appDependencies").GetProperty("libraries").EnumerateArray(), value => value.GetString()!.StartsWith("Backend.Library/", StringComparison.Ordinal));
        }
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:PackSunderPackage", "-nodeReuse:false", "-nologo", "-v:quiet", offline]));
        var archive = Path.Combine(root, "bin/Debug/net10.0/RestoreRoles.7.6.5.sunderpkg");
        var inspection = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, scope.GetPath("extracted"));
        Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Errors));
        Assert.Equal(2, inspection.Manifest!.Targets!.Count);
        foreach (var result in await Task.WhenAll(
                     RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet", offline]),
                     RunDotnetAsync(root, ["clean", project, "--disable-build-servers", "--nologo", "-v:quiet", "-p:BuildProjectReferences=false", offline]))) AssertSuccess(result);
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet", offline]));
        Assert.True(File.Exists(Path.Combine(worker, "Backend.Library.dll")));
    }

    private sealed class RoleDependencyLoadContext(string root) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(root, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            foreach (var file in new[] { name, name + ".dll", "lib" + name + ".so", "lib" + name + ".dylib" })
            {
                var path = Path.Combine(root, file);
                if (File.Exists(path)) return LoadUnmanagedDllFromPath(path);
            }
            return 0;
        }
    }
}
