using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
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
    public async Task SingleProject_OrdinaryBuildCompilesIsolatedRolesAndPreservesOutputsOnInvalidSource(bool immutableTasks)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-single-project-build");
        var root = ResolvePhysicalTemporaryPath(scope.GetPath("fixture"));
        var project = await CreateSingleProjectFixtureAsync(root, immutableTasks);
        var sourcePath = Path.Combine(root, "Mixed.cs");
        AssertSuccess(await Build());
        var worker = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/worker", RuntimeInformation.RuntimeIdentifier, "SingleProject.dll");
        var app = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/app", RuntimeInformation.RuntimeIdentifier, "SingleProject.App.dll");
        var analysis = Path.Combine(root, "obj/Debug/net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc");
        Assert.True(File.Exists(worker), worker); Assert.True(File.Exists(app), app);
        Assert.True(File.Exists(Path.Combine(analysis, "package-role-plan.v1.json")));
        Assert.False(File.Exists(Path.Combine(root, "bin/Debug/net10.0/SingleProject.dll")));
        foreach (var path in new[] { worker, app })
        {
            using var pdb = File.OpenRead(Path.ChangeExtension(path, ".pdb"));
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
            var metadata = provider.GetMetadataReader();
            Assert.Contains(sourcePath, metadata.Documents.Select(handle => metadata.GetString(metadata.GetDocument(handle).Name)));
        }
        AssertRole(worker, ["GreetingService", "WorkerEntryPoint", "BackendFormatter", "GreetingLifecycle"], ["GreetingView", "GreetingViewModel", "AppModule"], ["Avalonia.Base", "Avalonia.Controls", "Sunder.Sdk.Avalonia"]);
        AssertRole(app, ["GreetingView", "GreetingViewModel", "AppModule"], ["GreetingService", "WorkerEntryPoint", "BackendFormatter", "GreetingLifecycle"], ["Sunder.Sdk.Worker", "SingleProject"]);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(app)!, "Sunder.Sdk.Worker.dll")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(worker)!, "Avalonia.Controls.dll")));
        Assert.DoesNotContain("Sunder.Sdk.Worker", await File.ReadAllTextAsync(Path.ChangeExtension(app, ".deps.json")), StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", await File.ReadAllTextAsync(Path.ChangeExtension(worker, ".deps.json")), StringComparison.Ordinal);
        var dev = Path.Combine(root, "bin/Debug/net10.0/sunder-dev");
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(dev);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(2, validation.Manifest!.Targets!.Count);
        Assert.Equal("runtime", Assert.Single(validation.Manifest.Provides!)!.Role);
        var use = Assert.Single(validation.Manifest.UsesContracts!);
        Assert.Equal("app", use!.Role); Assert.True(use.Required);
        var before = new[] { File.GetLastWriteTimeUtc(worker), File.GetLastWriteTimeUtc(app) };
        var beforeDev = HashTree(dev);
        AssertSuccess(await Build());
        Assert.Equal(before, new[] { File.GetLastWriteTimeUtc(worker), File.GetLastWriteTimeUtc(app) });
        foreach (var concurrent in await Task.WhenAll(Build(), Build())) AssertSuccess(concurrent);
        Assert.Equal(before, new[] { File.GetLastWriteTimeUtc(worker), File.GetLastWriteTimeUtc(app) });
        await File.WriteAllTextAsync(sourcePath, SingleProjectSource.Replace("IGreetingService service", "GreetingService service", StringComparison.Ordinal));
        var invalid = await Build();
        Assert.NotEqual(0, invalid.ExitCode); Assert.Contains("role-conflict", invalid.StandardOutput + invalid.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, new[] { File.GetLastWriteTimeUtc(worker), File.GetLastWriteTimeUtc(app) });
        Assert.Equal(beforeDev, HashTree(dev));
        await File.WriteAllTextAsync(sourcePath, SingleProjectSource.Replace("BackendFormatter : IBackendFormatter", "BackendFormatter", StringComparison.Ordinal));
        var missingBinding = await Build();
        Assert.NotEqual(0, missingBinding.ExitCode); Assert.Contains("package-authoring.di-unresolved", missingBinding.StandardOutput + missingBinding.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, new[] { File.GetLastWriteTimeUtc(worker), File.GetLastWriteTimeUtc(app) });
        Assert.Equal(beforeDev, HashTree(dev));
        await File.WriteAllTextAsync(sourcePath, SingleProjectSource);
        var projectText = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(Path.Combine(root, "obj/role-only.cs"), "internal sealed class UnexpectedRoleOnlySource;");
        await File.WriteAllTextAsync(project, projectText.Replace("</Project>", "<ItemGroup Condition=\"'$(SunderPackageCompilationRole)' == 'worker'\"><Compile Include=\"obj/role-only.cs\" /></ItemGroup></Project>", StringComparison.Ordinal));
        var changedEvaluation = await Build();
        Assert.NotEqual(0, changedEvaluation.ExitCode);
        Assert.Contains("evaluated source set changed", changedEvaluation.StandardOutput + changedEvaluation.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, new[] { File.GetLastWriteTimeUtc(worker), File.GetLastWriteTimeUtc(app) });
        await File.WriteAllTextAsync(project, projectText);
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "-t:Rebuild", "--nologo", "-v:quiet"]));
        Assert.True(File.Exists(worker)); Assert.True(File.Exists(app));
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:PackSunderPackage", "-nodeReuse:false", "-nologo", "-v:quiet"]));
        Assert.False(File.Exists(Path.Combine(root, "sunder-contracts.owned.json")));
        var archive = Path.Combine(root, "bin/Debug/net10.0/SingleProject.7.6.5.sunderpkg");
        Assert.True(File.Exists(archive));
        var packed = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, Path.Combine(root, "extracted"));
        Assert.True(packed.Success, string.Join(Environment.NewLine, packed.Errors));
        Assert.Equal(2, packed.Manifest!.Targets!.Count);
        await File.WriteAllTextAsync(sourcePath, SingleProjectSource[..SingleProjectSource.IndexOf("[SunderPackageView", StringComparison.Ordinal)]);
        AssertSuccess(await Build());
        Assert.False(File.Exists(app));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(app)!, "sunder-dev")));
        var headless = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(dev);
        Assert.True(headless.Success, string.Join(Environment.NewLine, headless.Errors));
        Assert.Equal("runtime", Assert.Single(headless.Manifest!.Targets!)!.Role);
        AssertSuccess(await RunDotnetAsync(root, ["clean", project, "--disable-build-servers", "--nologo", "-v:quiet"]));
        Assert.False(File.Exists(worker)); Assert.False(File.Exists(app));
        Assert.False(Directory.Exists(Path.Combine(root, "obj/Debug/net10.0/sunder-rpc")));
        Assert.False(Directory.Exists(analysis));
        Assert.False(Directory.Exists(Path.Combine(root, "bin/Debug/net10.0/sunder-targets", RuntimeInformation.RuntimeIdentifier, "sunder-dev")));
        Assert.False(Directory.Exists(dev));

        Task<ProcessResult> Build() => RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]);
    }

    private static async Task<string> CreateSingleProjectFixtureAsync(string root, bool immutableTasks, bool legacyBaseline = false)
    {
        Directory.CreateDirectory(root);
        var repository = FindRepositoryRoot();
        var existing = Path.Combine(repository, "languages/csharp/dotnet/tests/Sunder.Package.Build.TestFixtures/ImplementationFirstRpc");
        File.Copy(Path.Combine(existing, "sunder-contracts.lock.json"), Path.Combine(root, "sunder-contracts.lock.json"));
        if (legacyBaseline) File.Copy(Path.Combine(existing, "History/implementation-first-private.owned.json"), Path.Combine(root, "sunder-contracts.owned.json"));
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), SingleProjectSource);
        // Repository generated-file policy must retain the authored nullable context.
        await File.WriteAllTextAsync(Path.Combine(root, ".editorconfig"), "root = true\n\n[*.cs]\ngenerated_code = false\n\n[**/obj/**/*.cs]\ngenerated_code = true\n");
        var taskAssembly = typeof(GenerateSunderRpcBindingsTask).Assembly.Location;
        var references = new[]
        {
            "Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia", "Avalonia.Base", "Avalonia.Controls",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions",
        };
        var project = Path.Combine(root, "SingleProject.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <Version>7.6.5</Version><SunderPackageProject>true</SunderPackageProject>
                <RuntimeIdentifiers>{{RuntimeInformation.RuntimeIdentifier}}</RuntimeIdentifiers>
                <SelfContained>false</SelfContained><UseAppHost>false</UseAppHost>
                <SunderPackageBuildTaskAssembly>{{SecurityElement.Escape(taskAssembly)}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildTaskSourceIsImmutable>{{immutableTasks.ToString().ToLowerInvariant()}}</SunderPackageBuildTaskSourceIsImmutable>
              </PropertyGroup>
              <ItemGroup>
                {{string.Join(Environment.NewLine, references.Select(name => $"<Reference Include=\"{name}\" HintPath=\"{SecurityElement.Escape(Path.Combine(Path.GetDirectoryName(taskAssembly)!, name + ".dll"))}\" Private=\"true\" />"))}}
              </ItemGroup>
              <Import Project="{{SecurityElement.Escape(Path.Combine(repository, "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"))}}" />
            </Project>
            """);
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--disable-parallel", "--nologo"]));
        return project;
    }

    private static void AssertRole(string path, string[] present, string[] absent, string[] forbiddenReferences)
    {
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        Assert.Equal(new Version(7, 6, 5, 0), metadata.GetAssemblyDefinition().Version);
        var types = metadata.TypeDefinitions.Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name)).ToArray();
        var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
        Assert.All(present, name => Assert.Contains(name, types)); Assert.All(absent, name => Assert.DoesNotContain(name, types));
        Assert.All(forbiddenReferences, name => Assert.DoesNotContain(name, references));
    }

    private const string SingleProjectSource = """
        using Sunder.Generated.Packages.Example_Generated_Services;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Services;
        [assembly: SunderPackage(Id = "example.generated", Name = "Single Project")]
        namespace Example.Generated;
        [SunderService]
        public sealed partial class GreetingService(IBackendFormatter formatter)
        {
            public ValueTask<GreetingResult> GreetAsync(GreetingRequest request, CancellationToken cancellationToken)
                => ValueTask.FromResult(new GreetingResult { Message = formatter.Format(request.Name) });
        }
        public sealed record GreetingRequest { public required string Name { get; init; } }
        public sealed record GreetingResult { public required string Message { get; init; } }
        public interface IBackendFormatter { string Format(string name); }
        public sealed class BackendFormatter : IBackendFormatter { public string? OptionalName { get; set; } public string Format(string name) => name; }
        public sealed class GreetingLifecycle(IGreetingService greeting) : Sunder.Sdk.Worker.ISunderWorkerLifecycleService
        {
            public ValueTask PrepareCandidateAsync(CancellationToken token = default) => ValueTask.CompletedTask;
            public ValueTask CommitGenerationAsync(Sunder.Sdk.Worker.SunderWorkerGenerationContext context, CancellationToken token = default) => ValueTask.CompletedTask;
            public async ValueTask ActivateAsync(Sunder.Sdk.Worker.SunderWorkerContext context, CancellationToken token = default)
                => _ = await greeting.GreetAsync(new GreetingRequest { Name = "activated" }, token);
            public Task Completion => Task.CompletedTask;
            public ValueTask ShutdownAsync(Sunder.Sdk.Worker.SunderWorkerShutdownContext context, CancellationToken token = default) => ValueTask.CompletedTask;
        }
        [SunderPackageView("example.generated.view", "Greeting", ViewModel = typeof(GreetingViewModel))]
        public sealed class GreetingView : Avalonia.Controls.UserControl;
        public sealed class GreetingViewModel(IGreetingService service)
        {
            public ValueTask<GreetingResult> ReadAsync() => service.GreetAsync(new() { Name = "UI" }, default);
        }
        """;
}
