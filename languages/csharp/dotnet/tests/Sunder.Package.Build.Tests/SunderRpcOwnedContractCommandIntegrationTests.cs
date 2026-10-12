using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

[Collection(nameof(TaskShadowStagingTests))]
public sealed class SunderRpcOwnedContractCommandIntegrationTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task Msbuild_CanonicalRoot_BootstrapsChecksFreezesAndPacksWithoutImplicitContractWrites()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-owned-contract-msbuild");
        var root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(scope.RootPath);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Sunder.Core.slnx"))) repository = repository.Parent;
        var repositoryRoot = repository?.FullName ?? throw new DirectoryNotFoundException("Could not locate Sunder Core.");
        var dotnetRoot = Path.Combine(repositoryRoot, "languages/csharp/dotnet");
        var buildDirectory = Path.Combine(dotnetRoot, "packages/Sunder.Package.Build/build");
        var project = Path.Combine(root, "OwnedContractCommands.csproj");
        var source = Path.Combine(root, "Program.cs");
        var baselinePath = Path.Combine(root, SunderRpcAuthoringDocuments.OwnedContractsFileName);
        var lockPath = Path.Combine(root, SunderRpcAuthoringDocuments.ContractLockFileName);
        var cache = Path.Combine(root, "contract-cache");
        var emptyFeed = Path.Combine(root, "empty-feed");
        Directory.CreateDirectory(emptyFeed);
        File.Copy(Path.Combine(dotnetRoot, "tests/Sunder.Package.Build.TestFixtures/ImplementationFirstRpc/Program.cs"), source);
        // Exercise the actual targets (including neutral export and Pack revalidation)
        // with a domain dependency that grants access only to this project assembly.
        await using var dependency = TemporaryDirectoryScope.Create("sunder-owned-contract-domain");
        var domainProject = dependency.GetPath("OwnedLifecycleDomain.csproj");
        File.WriteAllText(domainProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(dependency.GetPath("Domain.cs"), """
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OwnedContractCommands")]
            internal static class OwnedLifecycleDomain { internal static string Greet(string value) => value; }
            """);
        AssertSuccess(await RunDotnet(dependency.RootPath, "build", domainProject, "--disable-build-servers", "--nologo",
            "-p:RestoreSources=" + emptyFeed, "-p:NuGetAudit=false"));
        var domainReference = dependency.GetPath("bin", "Debug", "net10.0", "OwnedLifecycleDomain.dll");
        // This fixture owns the public Accept/Freeze lifecycle; normal private fixtures are baseline-free.
        File.WriteAllText(source, File.ReadAllText(source)
            .Replace("[SunderService]", "[SunderService(SunderServiceExposure.Public)]", StringComparison.Ordinal)
            .Replace("Message = request.Name", "Message = OwnedLifecycleDomain.Greet(request.Name)", StringComparison.Ordinal));
        File.Copy(Path.Combine(repositoryRoot, "specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json"), lockPath);
        var references = new[] { typeof(SunderServiceAttribute).Assembly.Location, typeof(SunderWorker).Assembly.Location,
            typeof(IServiceCollection).Assembly.Location, typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location, domainReference };
        var document = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("Import", new XAttribute("Project", Path.Combine(buildDirectory, "Sunder.Package.Build.props"))),
            new XElement("PropertyGroup",
                new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net10.0"),
                new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"), new XElement("Version", "7.6.5"),
                new XElement("CopyLocalLockFileAssemblies", "true"),
                new XElement("RuntimeIdentifier", new XAttribute("Condition", "'$(RuntimeIdentifier)' == ''"), "$(NETCoreSdkRuntimeIdentifier)"),
                new XElement("RuntimeIdentifiers", string.Join(';', SunderPackageFormat.SupportedRuntimeIdentifiers)),
                new XElement("SunderPackageProject", "true"), new XElement("SunderPackagePrimaryProject", "true"),
                new XElement("SunderSdkPackageVersion", "1.1.0"), new XElement("SunderContractCacheRoot", cache),
                new XElement("NuGetAudit", "false"), new XElement("RestoreSources", emptyFeed),
                new XElement("SunderPackageBuildTaskAssembly", typeof(GenerateSunderRpcBindingsTask).Assembly.Location),
                new XElement("SunderPackageBuildTaskSourceIsImmutable", "true"),
                new XElement("SunderPackageBuildProject", Path.Combine(root, "no-task-source.csproj"))),
            new XElement("ItemGroup", references.Select(path => new XElement("Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XAttribute("HintPath", path), new XAttribute("Private", "true"))),
                new XElement("SunderPackageTarget", new XAttribute("Include", "runtime/$(RuntimeIdentifier)"),
                    new XAttribute("Role", "runtime"), new XAttribute("Rid", "$(RuntimeIdentifier)"), new XAttribute("Kind", "worker"))),
            new XElement("Import", new XAttribute("Project", Path.Combine(buildDirectory, "Sunder.Package.Build.targets")))));
        document.Save(project);
        var nugetConfig = Path.Combine(root, "NuGet.Config");
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "offline"), new XAttribute("value", emptyFeed))))).Save(nugetConfig);

        AssertSuccess(await RunDotnet(root, "restore", project, "--configfile", nugetConfig, "--disable-parallel", "--nologo"));
        document.Root!.Add(new XElement("Target", new XAttribute("Name", "RejectImplicitContractTestRestore"),
            new XAttribute("BeforeTargets", "Restore"),
            new XElement("Error", new XAttribute("Text", "This offline contract lifecycle must not invoke Restore after explicit test setup."))));
        document.Save(project);
        var projectBytes = File.ReadAllBytes(project);
        var lockBytes = File.ReadAllBytes(lockPath);
        var sourceBytes = File.ReadAllBytes(source);
        var assetsPath = Path.Combine(root, "obj/project.assets.json");
        var assetsBytes = File.ReadAllBytes(assetsPath);
        var assetsTimestamp = File.GetLastWriteTimeUtc(assetsPath);
        var properties = await RunDotnet(root, "msbuild", project, "-nologo", "-nr:false",
            "-getProperty:RuntimeIdentifier,NETCoreSdkRuntimeIdentifier,SunderPackagePrimaryProject,SunderPackageInnerBuild,SunderPackageId,SunderPackageComposeRuntimeIdentifiers");
        AssertSuccess(properties);
        using var evaluated = JsonDocument.Parse(properties.Output);
        var values = evaluated.RootElement.GetProperty("Properties");
        var rid = values.GetProperty("RuntimeIdentifier").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(rid));
        Assert.Equal(values.GetProperty("NETCoreSdkRuntimeIdentifier").GetString(), rid);
        Assert.Equal("true", values.GetProperty("SunderPackagePrimaryProject").GetString());
        Assert.Equal("false", values.GetProperty("SunderPackageInnerBuild").GetString());
        Assert.Equal(string.Empty, values.GetProperty("SunderPackageId").GetString());
        Assert.Equal(SunderPackageFormat.SupportedRuntimeIdentifiers.Order(StringComparer.Ordinal),
            values.GetProperty("SunderPackageComposeRuntimeIdentifiers").GetString()!.Split(';').Order(StringComparer.Ordinal));
        var outputRoot = Path.Combine(root, "bin/Debug/net10.0");
        var implementationRpc = Path.Combine(root, "obj/Debug/net10.0", rid, "sunder-rpc");
        var neutralRpc = Path.Combine(root, "obj/Debug/net10.0/sunder-rpc");
        AssertNoRpcOutput();
        Assert.False(File.Exists(baselinePath));

        AssertFailure(await Target("CheckSunderContracts"), "baseline");
        AssertFailure(await Target("Build"), "baseline");
        Assert.False(File.Exists(baselinePath));
        AssertNoRpcOutput();
        AssertSuccess(await Target("AcceptSunderContracts"));
        var accepted = Baseline();
        Assert.Equal("example.generated", accepted.PackageId);
        Assert.Equal("1.0.0", accepted.Contracts["greeting"].Current!.Descriptor.Version);
        Assert.Equal("draft", accepted.Contracts["greeting"].Current!.State);
        AssertNoRpcOutput();
        Assert.False(File.Exists(Path.Combine(outputRoot, rid, "OwnedContractCommands.dll")));
        var oldTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(baselinePath, oldTimestamp);
        AssertSuccess(await Target("CheckSunderContracts"));
        AssertFailure(await Target("CheckSunderContracts", "-p:AssemblyName=UnrelatedAssembly"), "CS0122");
        AssertSuccess(await Target("AcceptSunderContracts"));
        AssertBaselineUnchanged(accepted.ExactBytes);
        AssertNoRpcOutput();

        AssertSuccess(await Target("Build"));
        AssertBaselineUnchanged(accepted.ExactBytes);
        Assert.True(File.Exists(Path.Combine(implementationRpc, "authoring.g.cs")));
        Assert.True(File.Exists(Path.Combine(neutralRpc, SunderRpcAuthoringDocuments.PackageExportFileName)));
        AssertSuccess(await Target("Rebuild"));
        AssertBaselineUnchanged(accepted.ExactBytes);
        AssertFailure(await Target("PackSunderPackage"), "draft");
        AssertBaselineUnchanged(accepted.ExactBytes);
        Assert.Empty(Directory.GetFiles(outputRoot, "*.sunderpkg"));
        var rpcBeforeFreeze = RpcSnapshot();
        AssertSuccess(await Target("FreezeSunderContracts"));
        var firstFrozen = Baseline();
        var originalEvidence = firstFrozen.Contracts["greeting"].Current!.SnapshotJson;
        Assert.Equal("release-frozen", firstFrozen.Contracts["greeting"].Current!.State);
        Assert.Equal("7.6.5", originalEvidence.GetProperty("packageRelease").GetProperty("packageVersion").GetString());
        Assert.False(originalEvidence.TryGetProperty("publication", out _));
        Assert.Equal(rpcBeforeFreeze, RpcSnapshot());
        File.SetLastWriteTimeUtc(baselinePath, oldTimestamp);
        AssertSuccess(await Target("FreezeSunderContracts"));
        AssertSuccess(await Target("PackSunderPackage"));
        AssertBaselineUnchanged(firstFrozen.ExactBytes);
        await AssertArchive("7.6.5", "1.0.0");

        var rpcBeforeChange = RpcSnapshot();
        var changedSource = Encoding.UTF8.GetString(sourceBytes).Replace("    public ValueTask<GreetingResult> GreetAsync",
            "    public ValueTask<GreetingResult> EchoAsync(GreetingRequest request)\n"
            + "        => ValueTask.FromResult(new GreetingResult { Message = request.Name });\n\n    public ValueTask<GreetingResult> GreetAsync", StringComparison.Ordinal);
        Assert.NotEqual(Encoding.UTF8.GetString(sourceBytes), changedSource);
        File.WriteAllText(source, changedSource);
        sourceBytes = File.ReadAllBytes(source);
        var drift = await Target("CheckSunderContracts");
        AssertFailure(drift, "baseline");
        Assert.Contains("Minor", drift.Output, StringComparison.Ordinal);
        AssertFailure(await Target("FreezeSunderContracts"), "baseline");
        AssertFailure(await Target("Build"), "baseline");
        AssertBaselineUnchanged(firstFrozen.ExactBytes);
        Assert.Equal(rpcBeforeChange, RpcSnapshot());

        // No old/new version properties: the shared planner must allocate the compatible bump.
        AssertSuccess(await Target("AcceptSunderContracts"));
        var evolved = Baseline();
        var contract = evolved.Contracts["greeting"];
        Assert.Equal("1.1.0", contract.Current!.Descriptor.Version);
        Assert.Equal("draft", contract.Current.State);
        Assert.True(JsonElement.DeepEquals(originalEvidence, Assert.Single(contract.History).SnapshotJson));
        AssertSuccess(await Target("CheckSunderContracts"));
        Assert.Equal(rpcBeforeChange, RpcSnapshot());
        AssertSuccess(await Target("FreezeSunderContracts", "-p:Version=7.6.6"));
        var nextFrozen = Baseline();
        Assert.Equal("release-frozen", nextFrozen.Contracts["greeting"].Current!.State);
        Assert.Equal("7.6.6", nextFrozen.Contracts["greeting"].Current!.SnapshotJson.GetProperty("packageRelease").GetProperty("packageVersion").GetString());
        Assert.True(JsonElement.DeepEquals(originalEvidence, Assert.Single(nextFrozen.Contracts["greeting"].History).SnapshotJson));
        Assert.Equal(rpcBeforeChange, RpcSnapshot());
        File.SetLastWriteTimeUtc(baselinePath, oldTimestamp);
        AssertSuccess(await Target("Build", "-p:Version=7.6.6"));
        AssertSuccess(await Target("PackSunderPackage", "-p:Version=7.6.6"));
        AssertBaselineUnchanged(nextFrozen.ExactBytes);
        await AssertArchive("7.6.6", "1.1.0");
        var rpcBeforeReuse = RpcSnapshot();
        AssertSuccess(await Target("FreezeSunderContracts", "-p:Version=7.6.7"));
        AssertBaselineUnchanged(nextFrozen.ExactBytes);
        Assert.Equal(rpcBeforeReuse, RpcSnapshot());

        Task<(int ExitCode, string Output)> Target(string name, params string[] additionalArguments)
            => RunDotnet(root, ["msbuild", project, "-t:" + name, "-m:1", "-nr:false", "-nologo", "-p:UseSharedCompilation=false", .. additionalArguments]);

        SunderRpcOwnedPackageBaseline Baseline() => SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(baselinePath);

        void AssertInputsUnchanged()
        {
            Assert.Equal(projectBytes, File.ReadAllBytes(project));
            Assert.Equal(sourceBytes, File.ReadAllBytes(source));
            Assert.Equal(lockBytes, File.ReadAllBytes(lockPath));
            Assert.Equal(assetsBytes, File.ReadAllBytes(assetsPath));
            Assert.Equal(assetsTimestamp, File.GetLastWriteTimeUtc(assetsPath));
            Assert.False(Directory.Exists(cache));
        }

        void AssertBaselineUnchanged(byte[] expected)
        {
            Assert.Equal(expected, File.ReadAllBytes(baselinePath));
            Assert.Equal(oldTimestamp, File.GetLastWriteTimeUtc(baselinePath));
            AssertInputsUnchanged();
        }

        void AssertNoRpcOutput()
        {
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, "obj"), "sunder-rpc", SearchOption.AllDirectories));
            Assert.Empty(RpcSnapshot());
            AssertInputsUnchanged();
        }

        string[] RpcSnapshot()
        {
            var markerName = Path.GetFileName(GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(neutralRpc));
            return Directory.GetFiles(Path.Combine(root, "obj"), "*", SearchOption.AllDirectories)
                .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("sunder-rpc", StringComparer.Ordinal) || Path.GetFileName(path) == markerName)
                .Order(StringComparer.Ordinal)
                .Select(path => Path.GetRelativePath(root, path) + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|"
                    + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        }

        async Task AssertArchive(string packageVersion, string contractVersion)
        {
            var archive = Path.Combine(outputRoot, $"OwnedContractCommands.{packageVersion}.sunderpkg");
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, Path.Combine(root, "verified", packageVersion));
            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
            Assert.Equal(packageVersion, validation.Manifest!.Version);
            Assert.Equal(SunderPackageFormat.SupportedRuntimeIdentifiers.Order(StringComparer.Ordinal),
                validation.Manifest.Targets!.Select(static target => target!.Rid!).Order(StringComparer.Ordinal));
            var bundle = Assert.Single(validation.Manifest.ContractBundles!)!;
            Assert.Equal("example.generated.greeting", bundle.ContractId);
            Assert.Equal(contractVersion, bundle.Version);
            Assert.Equal(contractVersion, Assert.Single(validation.Manifest.Provides!)!.ContractVersion);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDotnet(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} timed out.\n{await stdout}\n{await stderr}");
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    private static void AssertSuccess((int ExitCode, string Output) result)
        => Assert.True(result.ExitCode == 0, result.Output);

    private static void AssertFailure((int ExitCode, string Output) result, string diagnostic)
    {
        Assert.True(result.ExitCode != 0, result.Output);
        Assert.Contains(diagnostic, result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
