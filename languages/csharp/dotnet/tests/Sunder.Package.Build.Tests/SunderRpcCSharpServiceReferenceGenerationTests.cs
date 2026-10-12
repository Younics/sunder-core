using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpServiceReferenceGenerationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task CheckAndGenerateResolveOwnReferenceFactoriesBeforeRoleEmission(bool app)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-reference-catalog-phase");
        var root = scope.CreateDirectory("producer");
        var sourcePath = Path.Combine(root, "Producer.cs");
        var lockPath = Path.Combine(root, "sunder-contracts.lock.json");
        const string runtimeUse = "[SunderRuntimeUse(Actions = [\"discover\", \"invoke\"])]";
        var source = Source;
        if (app)
            source = source.Replace("public interface TargetService", "[SunderAppUse(Actions = [\"discover\"])]\npublic interface TargetService", StringComparison.Ordinal)
                + "\n[SunderPackageCodeRole(SunderPackageCodeRole.App)] public sealed class AppState { public string Title => \"Reference consumer\"; }\n";
        await File.WriteAllTextAsync(sourcePath, source);
        await File.WriteAllTextAsync(lockPath, "{\n  \"lockVersion\": 1,\n  \"packageArtifacts\": {},\n  \"packageImports\": {},\n  \"providerImports\": {},\n  \"platformImports\": {}\n}\n");
        var lockBytes = await File.ReadAllBytesAsync(lockPath);
        var projectPath = WriteProducerProject(root, sourcePath);

        var check = await Run("Check");
        Assert.True(check.Exit == 0, check.Output);
        Assert.False(File.Exists(Path.Combine(root, "sunder-contracts.owned.json")));
        Assert.Equal(source, await File.ReadAllTextAsync(sourcePath));
        Assert.Equal(lockBytes, await File.ReadAllBytesAsync(lockPath));

        var generated = await Run("");
        Assert.True(generated.Exit == 0, generated.Output);
        var output = Path.Combine(root, "obj", "sunder-rpc");
        var registration = await File.ReadAllTextAsync(Path.Combine(output, "authoring.g.cs"));
        Assert.DoesNotContain("CExample_Reference_Producer_TargetClientProviders", registration, StringComparison.Ordinal);
        Assert.Contains("global::System.Func<global::Sunder.Sdk.Rpc.SunderRpcProviderSnapshot", registration, StringComparison.Ordinal);
        var worker = await File.ReadAllTextAsync(Path.Combine(output, "roles", "worker", "worker-entry.g.cs"));
        Assert.Contains("CExample_Reference_Producer_TargetClientProviders", worker, StringComparison.Ordinal);
        Assert.Contains(".BindExact", worker, StringComparison.Ordinal);
        using (var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "authoring-receipt.v1.json"))))
            Assert.All(receipt.RootElement.GetProperty("uses").EnumerateArray(), use => Assert.Equal("runtime", use.GetProperty("role").GetString()));
        if (app)
        {
            using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "app-authoring-receipt.v1.json")));
            var targetUse = Assert.Single(receipt.RootElement.GetProperty("uses").EnumerateArray(),
                use => use.GetProperty("contractKey").GetString() == "target");
            Assert.Equal("app", targetUse.GetProperty("role").GetString());
            Assert.Equal(["discover"], targetUse.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        }
        else Assert.False(File.Exists(Path.Combine(output, "app-authoring-receipt.v1.json")));

        // Availability of the neutral factory type must not grant consumption.
        await File.WriteAllTextAsync(sourcePath, source.Replace(runtimeUse + "\npublic interface TargetService", "public interface TargetService", StringComparison.Ordinal)
            .Replace(runtimeUse + "\n[SunderAppUse", "[SunderAppUse", StringComparison.Ordinal));
        var denied = await Run("Check");
        Assert.NotEqual(0, denied.Exit);
        Assert.Contains("rpc-authoring.service-reference-use-missing", denied.Output, StringComparison.Ordinal);

        // The strict generated-member pass still rejects unresolved authored calls.
        await File.WriteAllTextAsync(sourcePath, source.Replace("target.Service.ReadAsync(token)", "target.Service.MissingAsync(token)", StringComparison.Ordinal));
        var invalid = await Run("Check");
        Assert.NotEqual(0, invalid.Exit);
        Assert.Contains("CS1061", invalid.Output, StringComparison.Ordinal);
        Assert.Equal(lockBytes, await File.ReadAllBytesAsync(lockPath));

        Task<(int Exit, string Output)> Run(string operation) => RunProducer(root, projectPath, operation);
    }

    private static string WriteProducerProject(string root, string sourcePath)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(SunderServiceAttribute).Assembly.Location, typeof(SunderWorkerProviderRegistration).Assembly.Location,
                typeof(Sunder.Sdk.Avalonia.IAvaloniaPackageContributionRegistry).Assembly.Location])
            .Distinct(StringComparer.Ordinal).ToArray();
        var project = new XDocument(new XElement("Project", new XAttribute("DefaultTargets", "Run"),
            new XElement("UsingTask", new XAttribute("TaskName", typeof(GenerateSunderRpcBindingsTask).FullName!),
                new XAttribute("AssemblyFile", typeof(GenerateSunderRpcBindingsTask).Assembly.Location)),
            new XElement("ItemGroup", new XElement("Source", new XAttribute("Include", sourcePath)),
                references.Select(path => new XElement("CompilerReference", new XAttribute("Include", path)))),
            new XElement("Target", new XAttribute("Name", "Run"),
                new XElement("GenerateSunderRpcBindingsTask",
                    new XAttribute("ContractOperation", "$(Operation)"), new XAttribute("ProjectDirectory", root),
                    new XAttribute("IntermediateOutputPath", Path.Combine(root, "obj")), new XAttribute("ProjectFile", "$(MSBuildProjectFullPath)"),
                    new XAttribute("ProjectIdentity", "ReferenceProducer"), new XAttribute("PackageId", "example.reference.producer"),
                    new XAttribute("PackageVersion", "1.0.0"), new XAttribute("AssemblyName", "ReferenceProducer"),
                    new XAttribute("Configuration", "Debug"), new XAttribute("TargetFramework", "net10.0"),
                    new XAttribute("DefineConstants", "$(ProbeDefines)"),
                    new XAttribute("OutputType", "Library"), new XAttribute("NullableEnabled", "true"),
                    new XAttribute("GeneratePackageRoles", "true"), new XAttribute("SourceFiles", "@(Source)"),
                    new XAttribute("ReferencePaths", "@(CompilerReference)")))));
        var path = Path.Combine(root, "Producer.proj"); project.Save(path); return path;
    }

    private static async Task<(int Exit, string Output)> RunProducer(string root, string projectPath, string operation, string defines = "")
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "msbuild", projectPath, "-t:Run", "-nodeReuse:false", "-nologo", "-v:minimal", "-p:Operation=" + operation, "-p:ProbeDefines=" + defines })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Generated.Packages.Example_Reference_Producer_Services;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Services;
        [assembly: SunderPackage(Id = "example.reference.producer", Name = "Reference catalog producer")]
        [SunderService]
        [SunderRuntimeUse(Actions = ["discover", "invoke"])]
        public interface TargetService { ValueTask<string> ReadAsync(CancellationToken token = default); }
        [SunderService]
        [SunderRuntimeUse(Actions = ["discover", "invoke"])]
        public interface HolderService { ValueTask<string> UseAsync(ServiceReference<ITargetService> target, CancellationToken token = default); }
        [SunderServiceProvider(typeof(HolderService), ProviderId = "example.reference.holder")]
        public sealed partial class HolderProvider : HolderService
        {
            public ValueTask<string> UseAsync(ServiceReference<ITargetService> target, CancellationToken token = default)
                => target.Service.ReadAsync(token);
        }
        """;
}
