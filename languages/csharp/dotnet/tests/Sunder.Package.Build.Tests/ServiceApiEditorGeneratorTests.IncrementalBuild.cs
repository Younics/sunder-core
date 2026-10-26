using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ServiceApiEditorGeneratorTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task PortableAnalyzer_RestoreRebuildsOlderSelectedContractsWithoutDisablingIncrementality()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-editor-restore-inputs");
        var target = Assert.Single(XDocument.Load(EditorProjectPath()).Root!.Elements("Target"),
            element => (string?)element.Attribute("Name") == "_BuildSunderEditorGenerator");
        var project = scope.GetPath("EditorContract.csproj");
        // Exercise the real compile target in isolation, not a second copy of its
        // incremental policy or a rebuild of every task/package dependency.
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            XElement.Parse("""
                <PropertyGroup>
                  <TargetFramework>net10.0</TargetFramework>
                  <NuGetAudit>false</NuGetAudit>
                </PropertyGroup>
                """),
            XElement.Parse("""
                <ItemGroup>
                  <PackageReference Include="NETStandard.Library" Version="2.0.3" ExcludeAssets="all" GeneratePathProperty="true" />
                  <PackageReference Include="System.Collections.Immutable" Version="$(ImmutableContractVersion)"
                                    ExcludeAssets="all" GeneratePathProperty="true" NoWarn="NU1510" />
                  <_SunderEditorSource Include="Contract.cs" />
                  <_SunderEditorReference Include="$(PkgNETStandard_Library)/build/netstandard2.0/ref/*.dll;$(PkgSystem_Collections_Immutable)/lib/netstandard2.0/System.Collections.Immutable.dll" />
                </ItemGroup>
                """),
            new XElement("Target", new XAttribute("Name", "_AcquireSunderPackageBuildTaskSourceLockForOwner")),
            new XElement(target))).Save(project);
        scope.WriteAllText("Contract.cs", """
            public static class SelectedContract
            {
                public static System.Collections.Immutable.ImmutableArray<int> Value => default;
            }
            """);
        var output = scope.GetPath("obj/Debug/net10.0/sunder-editor-generator/Sunder.Package.Build.Editor.dll");
        await Restore("10.0.13");
        await Build("10.0.13");
        Assert.Equal(new Version(10, 0, 0, 0), ImmutableVersion());
        var stamp = File.GetLastWriteTimeUtc(output);
        await Build("10.0.13");
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(output));

        var projectStamp = File.GetLastWriteTimeUtc(project);
        await Restore("9.0.21");
        Assert.Equal(projectStamp, File.GetLastWriteTimeUtc(project));
        await Build("9.0.21");
        Assert.Equal(new Version(9, 0, 0, 0), ImmutableVersion());
        stamp = File.GetLastWriteTimeUtc(output);
        await Build("9.0.21");
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(output));

        Task Restore(string version) => Run("restore", project, "--nologo", "--disable-parallel",
            "-p:RestoreLockedMode=false", "-p:ImmutableContractVersion=" + version);
        Task Build(string version) => Run("msbuild", project, "-nologo", "-nodeReuse:false",
            "-t:_BuildSunderEditorGenerator", "-p:ImmutableContractVersion=" + version);

        async Task Run(params string[] arguments)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = scope.RootPath, UseShellExecute = false,
            };
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            await using var process = CapturedProcess.Start(start);
            await process.EnsureSuccessAsync(TimeSpan.FromMinutes(2));
        }

        Version ImmutableVersion()
        {
            using var stream = File.OpenRead(output);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            return Assert.Single(metadata.AssemblyReferences.Select(metadata.GetAssemblyReference),
                reference => metadata.GetString(reference.Name) == "System.Collections.Immutable").Version;
        }
    }

    private static string EditorProjectPath([CallerFilePath] string sourcePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!,
            "../../packages/Sunder.Package.Build/Sunder.Package.Build.csproj"));
}
