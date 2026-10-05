using System.Runtime.InteropServices;
using System.Security;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("packed")]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task SingleProject_ActualWorkspaceLoadsUnbuiltReferencesAndRefreshesBothGenerators(string sdkMode)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-live-editor-workspace");
        var root = ResolvePhysicalTemporaryPath(scope.CreateDirectory("fixture"));
        var sourceSdk = sdkMode == "source";
        var packedVersion = sdkMode == "packed" ? "1.1.0-editor-workspace.g" + Guid.NewGuid().ToString("N") : null;
        var feed = packedVersion is null ? null : scope.CreateDirectory("feed");
        if (packedVersion is not null) await PackEditorSdkAsync(feed!, packedVersion);
        var project = await CreateEditorWorkspaceFixtureAsync(root, packedVersion, feed);
        var package = Path.GetDirectoryName(project)!;
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--disable-parallel", "--nologo", "-v:quiet"]));
        if (packedVersion is not null)
            AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--locked-mode", "--disable-parallel", "--source", feed!, "--nologo", "-v:quiet"]));
        var authoring = AuthoringInputs(root);
        var probe = scope.CreateDirectory("workspace probe");
        var probeProject = Path.Combine(probe, "WorkspaceProbe.csproj");
        await File.WriteAllTextAsync(probeProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
                <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Build.Locator" Version="1.7.8" />
                <PackageReference Include="Microsoft.Build.Framework" Version="17.11.31" ExcludeAssets="runtime" />
                <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.0.0" />
                <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="5.0.0" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(probe, "Program.cs"), await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(),
            "languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Fixtures/EditorWorkspaceProbe.cs.txt")));
        AssertSuccess(await RunDotnetAsync(probe, ["build", probeProject, "--disable-build-servers", "--nologo", "-v:quiet"]));
        AssertSuccess(await RunDotnetAsync(root, [Path.Combine(probe, "bin/Debug/net10.0/WorkspaceProbe.dll"), project, sourceSdk.ToString()]));
        Assert.Equal(authoring, AuthoringInputs(root));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "source library"), "*.dll", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(package, "bin/Debug/net10.0/EditorWorkspace.dll")));
        Assert.False(Directory.Exists(Path.Combine(package, "bin/Debug/net10.0/sunder-dev")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.sunderpkg", SearchOption.AllDirectories));

        if (packedVersion is not null)
        {
            var sourcePath = Path.Combine(package, "Mixed.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            var baselinePath = Path.Combine(package, "sunder-contracts.owned.json");
            var baseline = await File.ReadAllBytesAsync(baselinePath);
            await File.WriteAllTextAsync(sourcePath, source.Replace("GreetAsync", "WelcomeAsync", StringComparison.Ordinal));
            var unaccepted = await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]);
            Assert.NotEqual(0, unaccepted.ExitCode);
            Assert.Contains("AcceptSunderContracts", unaccepted.StandardOutput + unaccepted.StandardError, StringComparison.Ordinal);
            Assert.Equal(baseline, await File.ReadAllBytesAsync(baselinePath));
            await File.WriteAllTextAsync(sourcePath, source);
            AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]));
            AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:FreezeSunderContracts;PackSunderPackage", "-nodeReuse:false", "-nologo", "-v:quiet"]));
            var archive = Path.Combine(package, "bin/Debug/net10.0/EditorWorkspace.7.6.5.sunderpkg");
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, scope.GetPath("extracted"));
            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
            Assert.Equal(2, validation.Manifest!.Targets!.Count);
            Assert.DoesNotContain(Directory.EnumerateFiles(scope.GetPath("extracted"), "*.dll", SearchOption.AllDirectories),
                path => Path.GetFileName(path).StartsWith("Sunder.Package.Build", StringComparison.Ordinal));
            var editorRoot = Path.Combine(package, "obj/Debug/net10.0/sunder-design-time");
            var snapshots = Directory.GetFiles(Path.Combine(editorRoot, "sunder-editor-snapshots"), "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(snapshots);
            AssertSuccess(await RunDotnetAsync(root, ["clean", project, "--disable-build-servers", "--nologo", "-v:quiet"]));
            Assert.False(Directory.Exists(Path.Combine(editorRoot, "sunder-rpc")));
            Assert.All(snapshots, path => Assert.True(File.Exists(path)));
        }
    }

    private static async Task<string> CreateEditorWorkspaceFixtureAsync(string root, string? packedVersion, string? feed)
    {
        var package = Path.Combine(root, "package with spaces");
        var library = Path.Combine(root, "source library");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(library);
        var repository = FindRepositoryRoot();
        var baseline = Path.Combine(repository, "languages/csharp/dotnet/tests/Sunder.Package.Build.TestFixtures/ImplementationFirstRpc");
        foreach (var file in new[] { "sunder-contracts.owned.json", "sunder-contracts.lock.json" })
            File.Copy(Path.Combine(baseline, file), Path.Combine(package, file));
        await File.WriteAllTextAsync(Path.Combine(library, "SourceLibrary.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(library, "Formatter.cs"), """
            namespace SourceLibrary;
            public static class Formatter { public static string Format(string name) => "source: " + name; }
            """);
        await File.WriteAllTextAsync(Path.Combine(package, "Mixed.cs"), SingleProjectSource
            .Replace("public string Format(string name) => name;", "public string Format(string name) => SourceLibrary.Formatter.Format(name);", StringComparison.Ordinal)
            .Replace("public sealed class GreetingView : Avalonia.Controls.UserControl;", """
                public sealed partial class GreetingView : Avalonia.Controls.UserControl
                {
                    public GreetingView() { InitializeComponent(); }
                    public string NamedText => Status.Text ?? "";
                }
                """, StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(package, "GreetingView.axaml"), """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Example.Generated.GreetingView">
              <TextBlock x:Name="Status" Text="workspace" />
            </UserControl>
            """);
        var sdkReferences = packedVersion is not null
            ? $"<PackageReference Include=\"Sunder.Sdk.Worker\" Version=\"{packedVersion}\" /><PackageReference Include=\"Sunder.Sdk.Avalonia\" Version=\"{packedVersion}\" /><PackageReference Include=\"Sunder.Package.Build\" Version=\"{packedVersion}\" PrivateAssets=\"all\" />"
            : string.Join(Environment.NewLine, new[] { "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia" }.Select(name =>
                $"<ProjectReference Include=\"{SecurityElement.Escape(Path.Combine(repository, "languages/csharp/dotnet/packages", name, name + ".csproj"))}\" />"));
        var project = Path.Combine(package, "EditorWorkspace.csproj");
        var sourceTooling = packedVersion is null
            ? "<SunderPackageBuildTaskSourceIsImmutable>false</SunderPackageBuildTaskSourceIsImmutable>"
            : "<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>";
        var buildImport = packedVersion is null
            ? $"<Import Project=\"{SecurityElement.Escape(Path.Combine(repository, "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"))}\" />"
            : "";
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
                <Version>7.6.5</Version><SunderPackageProject>true</SunderPackageProject><NuGetAudit>false</NuGetAudit>
                <RuntimeIdentifiers>{{RuntimeInformation.RuntimeIdentifier}}</RuntimeIdentifiers><SelfContained>false</SelfContained><UseAppHost>false</UseAppHost>
                {{sourceTooling}}
              </PropertyGroup>
              <ItemGroup>
                {{sdkReferences}}
                <ProjectReference Include="../source library/SourceLibrary.csproj" />
                <PackageReference Include="Avalonia" Version="12.0.3" />
                <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.8" />
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.8" />
              </ItemGroup>
              {{buildImport}}
            </Project>
            """);
        if (packedVersion is not null)
            await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), $$"""
                <configuration>
                  <config><add key="globalPackagesFolder" value="{{SecurityElement.Escape(Path.GetFullPath(Path.Combine(root, "..", "packages")))}}" /></config>
                  <packageSources><clear /><add key="local" value="{{SecurityElement.Escape(feed)}}" /><add key="public" value="https://api.nuget.org/v3/index.json" /></packageSources>
                  <packageSourceMapping><clear /><packageSource key="local"><package pattern="Sunder.*" /></packageSource><packageSource key="public"><package pattern="*" /></packageSource></packageSourceMapping>
                </configuration>
                """);
        return project;
    }

    private static async Task PackEditorSdkAsync(string feed, string version)
    {
        var repository = FindRepositoryRoot();
        var configuration = "EditorWorkspace-" + Guid.NewGuid().ToString("N");
        foreach (var name in new[] { "Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia", "Sunder.Package.Build" })
        {
            var project = Path.Combine(repository, "languages/csharp/dotnet/packages", name, name + ".csproj");
            AssertSuccess(await RunDotnetAsync(repository, ["pack", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet", "-o", feed,
                $"-p:Configuration={configuration}", $"-p:SunderDeveloperPackageVersion={version}",
                $"-p:SunderDeveloperPackageVersionRange=[{version}]", "-p:GeneratePackageOnBuild=false"]));
            Assert.True(File.Exists(Path.Combine(feed, name + "." + version + ".nupkg")));
        }
    }
}
