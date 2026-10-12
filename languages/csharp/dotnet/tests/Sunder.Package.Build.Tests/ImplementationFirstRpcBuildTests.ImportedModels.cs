using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task ImportedModelDataTemplate_UpdateAndCompiledBuildRetainRuntimeOnlyIntent()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-imported-model-xaml");
        var root = ResolvePhysicalTemporaryPath(scope.CreateDirectory("consumer"));
        var cache = ResolvePhysicalTemporaryPath(scope.CreateDirectory("cache"));
        const string declaration = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public interface RecordsService { ValueTask<WorkspaceRecord> ReadAsync(); }
            public sealed record WorkspaceRecord(string DisplayName);
            """;
        var candidate = await WriteProjectedCandidateAsync(scope.CreateDirectory("candidate-v1"), "1.0.0", declaration);
        var project = await CreateProjectedConsumerAsync(root, cache, """
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Services;
            [assembly: SunderPackage(Id = "example.consumer", Name = "Imported model consumer")]
            namespace Example.Consumer;
            [SunderService] public sealed partial class OwnerService
            { public ValueTask<bool> ReadyAsync() => ValueTask.FromResult(true); }
            [SunderPackageView("records", "Records")]
            public sealed partial class RecordsView : Avalonia.Controls.UserControl
            { public RecordsView() { InitializeComponent(); } }
            """, app: true);
        const string xaml = """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:m="using:Sunder.Generated.Packages.Example_Models_Models"
                         x:Class="Example.Consumer.RecordsView" x:CompileBindings="True">
              <UserControl.DataTemplates>
                <DataTemplate x:DataType="m:WorkspaceRecord">
                  <TextBlock Text="{Binding DisplayName}" />
                </DataTemplate>
              </UserControl.DataTemplates>
            </UserControl>
            """;
        var xamlPath = Path.Combine(root, "RecordsView.axaml");
        await File.WriteAllTextAsync(xamlPath, xaml);
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--nologo"]));
        AssertSuccess(await Select("Add", candidate, "-p:SunderPackageVersionRange=>=1.0.0 <3.0.0", "-p:SunderPackageApiConsumerRoles=runtime"));
        AssertSuccess(await Build());
        var lockPath = Path.Combine(root, SunderRpcAuthoringDocuments.ContractLockFileName);

        // Reproduce an existing consumer's explicit sidecar-upgrade path. This is
        // legacy fixture evidence, never a manufactured current hash or grant.
        var legacy = JsonNode.Parse(await File.ReadAllBytesAsync(lockPath))!;
        var artifact = legacy["packageArtifacts"]!["example.models"]!.AsObject();
        artifact.Remove("modelProjectionBytes"); artifact.Remove("modelProjectionSha256");
        await File.WriteAllBytesAsync(lockPath, SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(legacy)));
        var next = await WriteProjectedCandidateAsync(scope.CreateDirectory("candidate-v2"), "2.0.0", declaration);
        AssertSuccess(await Select("Update", next));
        var selected = SunderRpcAuthoringDocuments.ReadContractLock(lockPath);
        Assert.Equal("2.0.0", selected.PackageArtifacts["example.models"].SelectedPackageVersion);
        Assert.True(selected.PackageArtifacts["example.models"].ModelProjectionBytes > 0);
        Assert.Equal(["runtime"], selected.PackageImports["example.models"].ConsumerRoles);
        Assert.Empty(selected.ProviderImports);
        var lockBytes = await File.ReadAllBytesAsync(lockPath);
        AssertSuccess(await Build());
        var dev = Path.Combine(root, "bin/Debug/net10.0/sunder-dev");
        var valid = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(dev);
        Assert.True(valid.Success, string.Join(Environment.NewLine, valid.Errors));
        Assert.Equal("runtime", Assert.Single(valid.Manifest!.UsesContracts!, use => use!.ContractId == "example.models.records")!.Role);
        Assert.DoesNotContain(valid.Manifest.UsesContracts!, use => use!.Role == "app" && use.ContractId!.StartsWith("example.models.", StringComparison.Ordinal));
        var app = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/app", RuntimeInformation.RuntimeIdentifier, "ProjectedConsumer.App.dll");
        Assert.Contains("!AvaloniaResources", ResourceNames(app));
        var devHash = HashTree(dev);

        // A missing member must still fail Avalonia's compiled-binding compiler.
        await File.WriteAllTextAsync(xamlPath, xaml.Replace("{Binding DisplayName}", "{Binding MissingProperty}", StringComparison.Ordinal));
        var invalidBinding = await Build();
        Assert.NotEqual(0, invalidBinding.ExitCode);
        Assert.Contains("MissingProperty", invalidBinding.StandardOutput + invalidBinding.StandardError, StringComparison.Ordinal);
        Assert.Equal(devHash, HashTree(dev));

        // An authored Worker type does not acquire model provenance merely by
        // occupying the canonical generated namespace.
        await File.WriteAllTextAsync(Path.Combine(root, "Counterfeit.cs"), """
            namespace Sunder.Generated.Packages.Example_Models_Models;
            [Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Worker)]
            public sealed class CounterfeitRecord { public string DisplayName => "untrusted"; }
            """);
        await File.WriteAllTextAsync(xamlPath, xaml.Replace("m:WorkspaceRecord", "m:CounterfeitRecord", StringComparison.Ordinal));
        var counterfeit = await Build();
        Assert.NotEqual(0, counterfeit.ExitCode);
        Assert.Contains("package-authoring.role-conflict", counterfeit.StandardOutput + counterfeit.StandardError, StringComparison.Ordinal);
        Assert.Equal(devHash, HashTree(dev));
        Assert.Equal(lockBytes, await File.ReadAllBytesAsync(lockPath));

        Task<ProcessResult> Build() => RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]);
        Task<ProcessResult> Select(string operation, string archive, params string[] options) => RunDotnetAsync(root,
            ["msbuild", project, "-t:" + operation + "SunderPackageApi", "-nodeReuse:false", "-nologo", "-v:minimal",
                "-p:SunderPackageId=example.models", "-p:SunderPackageCandidatePath=" + archive, .. options]);
    }

    private static async Task<string> CreateProjectedConsumerAsync(string root, string cache, string source, bool app)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Consumer.cs"), source);
        using var emptyLock = JsonDocument.Parse("{\"lockVersion\":1,\"packageArtifacts\":{},\"packageImports\":{},\"providerImports\":{},\"platformImports\":{}}");
        await File.WriteAllBytesAsync(Path.Combine(root, SunderRpcAuthoringDocuments.ContractLockFileName),
            SunderRpcAuthoringDocuments.WriteDeterministicDocument(emptyLock.RootElement));
        var taskAssembly = typeof(GenerateSunderRpcBindingsTask).Assembly.Location;
        var references = app ? new[] { "Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia" } : ["Sunder.Sdk", "Sunder.Sdk.Worker"];
        var project = Path.Combine(root, "ProjectedConsumer.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
                <Version>1.0.0</Version><SunderPackageProject>true</SunderPackageProject><NuGetAudit>false</NuGetAudit>
                <RuntimeIdentifiers>{{RuntimeInformation.RuntimeIdentifier}}</RuntimeIdentifiers><SelfContained>false</SelfContained><UseAppHost>false</UseAppHost>
                <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
                <SunderContractCacheRoot>{{SecurityElement.Escape(cache)}}</SunderContractCacheRoot>
                <SunderPackageBuildTaskAssembly>{{SecurityElement.Escape(taskAssembly)}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildTaskSourceIsImmutable>true</SunderPackageBuildTaskSourceIsImmutable>
              </PropertyGroup>
              <ItemGroup>
                {{string.Join(Environment.NewLine, references.Select(name => $"<Reference Include=\"{name}\" HintPath=\"{SecurityElement.Escape(Path.Combine(Path.GetDirectoryName(taskAssembly)!, name + ".dll"))}\" Private=\"true\" />"))}}
                {{(app ? "<PackageReference Include=\"Avalonia\" Version=\"12.0.3\" />" : "")}}
                <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.8" />
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.8" />
              </ItemGroup>
              <Import Project="{{SecurityElement.Escape(Path.Combine(FindRepositoryRoot(), "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"))}}" />
            </Project>
            """);
        return project;
    }

    private static async Task<string> WriteProjectedCandidateAsync(string root, string version, string declaration)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderServiceAttribute).Assembly.Location).Distinct(StringComparer.Ordinal).ToArray();
        var compiled = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.models", version,
            [new("declaration.cs", declaration)], references, GenerateServiceInterfaces: true)).Compilation;
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(package, "manifest"));
        Directory.CreateDirectory(Path.Combine(package, "payload/shared/contracts"));
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = 1, ManifestVersion = 1, Id = "example.models", Name = "Projected Models", Version = version, Targets = [], DependsOn = [],
            ContractBundles = compiled.Contracts.Select(contract => new SunderPackageContractBundleManifest
            {
                Source = "owned", AuthorityKind = "package", AuthorityId = "example.models", ContractId = contract.Descriptor.ContractId,
                Version = contract.Descriptor.Version, DescriptorPath = "contracts/" + contract.ContractKey + ".json",
                DescriptorSha256 = contract.Descriptor.Sha256, AbiSha256 = contract.Descriptor.AbiSha256, Exposure = "public",
            }).ToArray(),
            UsesContracts = [], Provides = [], PackageArtifactImports = [], PackageApiImports = [], ProviderImports = [], PlatformImports = [],
        };
        await File.WriteAllBytesAsync(Path.Combine(package, SunderPackageFormat.ManifestPath), JsonSerializer.SerializeToUtf8Bytes(manifest,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        foreach (var contract in compiled.Contracts)
            await File.WriteAllBytesAsync(Path.Combine(package, SunderPackageFormat.SharedPayloadRoot, "contracts/" + contract.ContractKey + ".json"), contract.Descriptor.GetCanonicalUtf8());
        var projection = new SunderRpcModelProjectionDocument(1, "example.models", version, compiled.Contracts.Select(SunderRpcCSharpModelProjection.Entry).ToArray());
        await File.WriteAllBytesAsync(Path.Combine(package, SunderPackageFormat.RpcModelProjectionPath), projection.ToCanonicalUtf8());
        PackageContentIndexer.Write(package);
        var archive = Path.Combine(root, "candidate.sunderpkg");
        await SunderArchive.WriteDeterministicAsync(package, archive);
        return archive;
    }
}
