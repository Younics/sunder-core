using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task SingleProject_ReevaluatesRidSourceAndValidatesFrameworkPreviewSelection()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-target-selection");
        var root = ResolvePhysicalTemporaryPath(scope.CreateDirectory("package with spaces"));
        var sources = ResolvePhysicalTemporaryPath(scope.CreateDirectory("linked"));
        var repository = FindRepositoryRoot();
        var baseline = Path.Combine(repository, "languages/csharp/dotnet/tests/Sunder.Package.Build.TestFixtures/ImplementationFirstRpc");
        File.Copy(Path.Combine(baseline, "sunder-contracts.lock.json"), Path.Combine(root, "sunder-contracts.lock.json"));
        var hostRid = RuntimeInformation.RuntimeIdentifier;
        var otherRid = hostRid == "linux-x64" ? "win-x64" : "linux-x64";
        await File.WriteAllTextAsync(Path.Combine(sources, "Host.cs"), Flavor("host"));
        await File.WriteAllTextAsync(Path.Combine(sources, "Other.cs"), Flavor("other"));
        var source = SingleProjectSource.Replace("public string Format(string name) => name;", "public string Format(string name) => RidFlavor.Format(name);", StringComparison.Ordinal);
        var sourcePath = Path.Combine(root, "Mixed.cs"); await File.WriteAllTextAsync(sourcePath, source);
        var taskAssembly = typeof(GenerateSunderRpcBindingsTask).Assembly.Location;
        var project = Path.Combine(root, "TargetSelection.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks><EnableWindowsTargeting>true</EnableWindowsTargeting>
                <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><Version>7.6.5</Version><NuGetAudit>false</NuGetAudit>
                <RuntimeIdentifiers>{{hostRid}};{{otherRid}}</RuntimeIdentifiers><SelfContained>false</SelfContained><UseAppHost>false</UseAppHost>
                <SunderPackageProject>true</SunderPackageProject><SunderPackageBuildTaskSourceIsImmutable>true</SunderPackageBuildTaskSourceIsImmutable>
                <SunderPackageBuildTaskAssembly>{{SecurityElement.Escape(taskAssembly)}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildProject>no-task-source.csproj</SunderPackageBuildProject>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0-windows'"><DefineConstants>$(DefineConstants);WINDOWS_FLAVOR</DefineConstants></PropertyGroup>
              <ItemGroup>
                {{string.Join(Environment.NewLine, new[] { "Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia" }.Select(name => $"<Reference Include=\"{name}\" HintPath=\"{SecurityElement.Escape(Path.Combine(Path.GetDirectoryName(taskAssembly)!, name + ".dll"))}\" Private=\"true\" />"))}}
                <PackageReference Include="Avalonia" Version="12.0.3" />
                <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.8" />
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.8" />
                <Compile Include="../linked/Host.cs" Condition="'$(RuntimeIdentifier)' != '{{otherRid}}'" />
                <Compile Include="../linked/Other.cs" Condition="'$(RuntimeIdentifier)' == '{{otherRid}}'" />
              </ItemGroup>
              <Import Project="{{SecurityElement.Escape(Path.Combine(repository, "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"))}}" />
              <Target Name="CapturePreview" DependsOnTargets="GetSunderPackageAppPreviewInputs">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/preview.txt" Overwrite="true"
                  Lines="@(_SunderPackagePreviewInput->'%(FullPath)|%(DepsFile)|%(RuntimeConfigFile)|%(TargetFramework)|%(RuntimeIdentifier)')" />
              </Target>
            </Project>
            """);
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--disable-parallel", "--nologo"]));
        var unsupported = await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "-f", "net10.0-windows", "--nologo", "-v:quiet"]);
        Assert.NotEqual(0, unsupported.ExitCode); Assert.Contains("framework-unsupported", unsupported.StandardOutput + unsupported.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Worker("net10.0-windows", hostRid)));
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "-f", "net10.0", "--nologo", "-v:quiet"]));
        Assert.Equal("host", Constant(Worker("net10.0", hostRid), "Value"));
        Assert.Equal("portable", Constant(Worker("net10.0", hostRid), "Framework"));
        Assert.True(File.Exists(Path.Combine(root, "obj/Debug/net10.0/sunder-analysis", hostRid, "sunder-rpc/package-role-plan.v1.json")));
        var frameworkRequired = await RunDotnetAsync(root, ["msbuild", project, "-t:PackSunderPackage", "-nologo", "-v:quiet"]);
        Assert.NotEqual(0, frameworkRequired.ExitCode); Assert.Contains("framework-required", frameworkRequired.StandardOutput + frameworkRequired.StandardError, StringComparison.Ordinal);
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "-f", "net10.0", "-r", otherRid, "--nologo", "-v:quiet"]));
        Assert.Equal("other", Constant(Worker("net10.0", otherRid), "Value"));
        using (var plan = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "obj/Debug/net10.0/sunder-analysis", otherRid, "sunder-rpc/package-role-plan.v1.json"))))
            Assert.Equal(otherRid, plan.RootElement.GetProperty("runtimeIdentifier").GetString());
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-p:TargetFramework=net10.0", "-t:PackSunderPackage", "-nodeReuse:false", "-nologo", "-v:quiet"]));
        var archive = Path.Combine(root, "bin/Debug/net10.0/TargetSelection.7.6.5.sunderpkg");
        var inspection = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, scope.GetPath("extracted"));
        Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Errors));
        Assert.Equal(4, inspection.Manifest!.Targets!.Count);
        Assert.Equal(new[] { hostRid, otherRid }.Order(StringComparer.Ordinal), inspection.Manifest.Targets.Select(target => target!.Rid!).Distinct().Order(StringComparer.Ordinal));
        var packedBytes = await File.ReadAllBytesAsync(archive);
        var duplicate = await RunDotnetAsync(root, ["msbuild", project, "-p:TargetFramework=net10.0", $"-p:SunderPackageComposeRuntimeIdentifiers=\"{hostRid};{hostRid}\"", "-t:PackSunderPackage", "-nologo", "-v:quiet"]);
        Assert.NotEqual(0, duplicate.ExitCode); Assert.Contains("contains duplicates", duplicate.StandardOutput + duplicate.StandardError, StringComparison.Ordinal);
        Assert.Equal(packedBytes, await File.ReadAllBytesAsync(archive));
        var unknown = await RunDotnetAsync(root, ["msbuild", project, "-p:TargetFramework=net10.0", "-p:RuntimeIdentifier=linux-unknown", "-t:Build", "-nologo", "-v:quiet"]);
        Assert.NotEqual(0, unknown.ExitCode); Assert.Contains("rid-invalid", unknown.StandardOutput + unknown.StandardError, StringComparison.Ordinal);

        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-p:TargetFramework=net10.0", "-t:CapturePreview", "-nodeReuse:false", "-nologo", "-v:quiet"]));
        var preview = (await File.ReadAllTextAsync(Path.Combine(root, "preview.txt"))).Trim().Split('|');
        Assert.Equal(Path.Combine(root, "bin/Debug/net10.0/sunder-roles/app", hostRid, "TargetSelection.App.dll"), preview[0]);
        Assert.All(preview.Take(3), path => Assert.True(File.Exists(path), path));
        Assert.Equal("net10.0", preview[3]); Assert.Equal(hostRid, preview[4]);
        using (var runtime = JsonDocument.Parse(await File.ReadAllBytesAsync(preview[2])))
            Assert.Equal("net10.0", runtime.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString());
        var previewSource = Path.Combine(root, "Preview View.axaml");
        await File.WriteAllTextAsync(previewSource, "<UserControl xmlns=\"https://github.com/avaloniaui\" />");
        var probe = scope.CreateDirectory("preview probe");
        var probeProject = Path.Combine(probe, "PreviewProbe.csproj");
        await File.WriteAllTextAsync(probeProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        var invocation = Path.Combine(probe, "invocation.json");
        await File.WriteAllTextAsync(Path.Combine(probe, "Program.cs"), $$"""
            System.IO.File.WriteAllText({{JsonSerializer.Serialize(invocation)}}, System.Text.Json.JsonSerializer.Serialize(args));
            """);
        AssertSuccess(await RunDotnetAsync(probe, ["build", probeProject, "--disable-build-servers", "--nologo", "-v:quiet"]));
        var probeAssembly = Path.Combine(probe, "bin/Debug/net10.0/PreviewProbe.dll");
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-p:TargetFramework=net10.0", "-t:AvaloniaFilePreview",
            $"-p:AvaloniaPreviewerNetCoreToolPath={probeAssembly}", $"-p:APreviewFile={previewSource}", "-nodeReuse:false", "-nologo", "-v:quiet"]));
        Assert.Equal(new[] { "--method", "html", "--html-url", "http://127.0.0.1:6001", "--transport", previewSource, preview[0] },
            JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(invocation))!);
        var cliProbe = Path.Combine(probe, "bin/Debug/net10.0", OperatingSystem.IsWindows() ? "PreviewProbe.exe" : "PreviewProbe");
        Assert.True(File.Exists(cliProbe));
        AssertSuccess(await RunDotnetAsync(root, ["run", "--project", project, "--framework", "net10.0", "--no-build",
            $"-p:SunderCliPath={cliProbe}", "--", "--watch"]));
        Assert.Equal(new[] { "dev", "package", "run", Path.Combine(root, "bin/Debug/net10.0/sunder-dev"), "--watch" },
            JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(invocation))!);
        File.Delete(previewSource);
        var foreign = await RunDotnetAsync(root, ["msbuild", project, "-p:TargetFramework=net10.0", $"-p:RuntimeIdentifier={otherRid}", "-t:GetSunderPackageAppPreviewInputs", "-nologo", "-v:quiet"]);
        Assert.NotEqual(0, foreign.ExitCode); Assert.Contains("preview-rid-invalid", foreign.StandardOutput + foreign.StandardError, StringComparison.Ordinal);
        await File.WriteAllTextAsync(sourcePath, source[..source.IndexOf("[SunderPackageView", StringComparison.Ordinal)]);
        var headless = await RunDotnetAsync(root, ["msbuild", project, "-p:TargetFramework=net10.0", "-t:GetSunderPackageAppPreviewInputs", "-nologo", "-v:quiet"]);
        Assert.NotEqual(0, headless.ExitCode); Assert.Contains("preview-unavailable", headless.StandardOutput + headless.StandardError, StringComparison.Ordinal);
        AssertSuccess(await RunDotnetAsync(root, ["clean", project, "--disable-build-servers", "--nologo", "-v:quiet"]));
        Assert.False(File.Exists(Worker("net10.0", hostRid))); Assert.False(File.Exists(Worker("net10.0", otherRid)));
        Assert.False(File.Exists(Worker("net10.0-windows", hostRid)));
        foreach (var rid in new[] { hostRid, otherRid })
        {
            Assert.False(Directory.Exists(Path.Combine(root, "obj/Debug/net10.0/sunder-analysis", rid, "sunder-rpc")));
            Assert.False(Directory.Exists(Path.Combine(root, "bin/Debug/net10.0/sunder-targets", rid, "sunder-dev")));
        }
        Assert.False(Directory.Exists(Path.Combine(root, "bin/Debug/net10.0/sunder-dev")));

        string Worker(string tfm, string rid) => Path.Combine(root, "bin/Debug", tfm, "sunder-roles/worker", rid, "TargetSelection.dll");
        static string Flavor(string value) => $$"""
            namespace Example.Generated;
            public static class RidFlavor
            {
                public const string Value = "{{value}}";
            #if WINDOWS_FLAVOR
                public const string Framework = "windows";
            #else
                public const string Framework = "portable";
            #endif
                public static string Format(string value) => value + Value + Framework;
            }
            """;
    }

    private static string Constant(string path, string name)
    {
        using var stream = File.OpenRead(path); using var reader = new PEReader(stream); var metadata = reader.GetMetadataReader();
        var type = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Single(type => metadata.GetString(type.Name) == "RidFlavor");
        var field = type.GetFields().Select(metadata.GetFieldDefinition).Single(field => metadata.GetString(field.Name) == name);
        return Encoding.Unicode.GetString(metadata.GetBlobBytes(metadata.GetConstant(field.GetDefaultValue()).Value));
    }
}
