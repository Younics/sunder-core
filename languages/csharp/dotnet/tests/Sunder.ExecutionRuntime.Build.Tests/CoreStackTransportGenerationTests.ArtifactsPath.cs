using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;
using TaskItem = Microsoft.Build.Utilities.TaskItem;

namespace Sunder.ExecutionRuntime.Build.Tests;

public sealed partial class CoreStackTransportGenerationTests
{
    [Theory]
    [InlineData("source-outside")]
    [InlineData("source-sibling")]
    [InlineData("source-unlisted")]
    [InlineData("source-injection")]
    [InlineData("sdk-no-provenance")]
    [InlineData("sdk-foreign-project")]
    [InlineData("sdk-outside")]
    [InlineData("sdk-foreign-shape")]
    [InlineData("sdk-foreign-version")]
    [InlineData("artifacts-source-root")]
    [InlineData("artifacts-ancestor")]
    [InlineData("intermediate-other-project")]
    public void ExternalArtifacts_RejectsForeignSourceAndSdkInputs(string mutation)
    {
        using var tree = new CoreTree();
        var artifacts = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(tree.Temp.CreateDirectory("external artifacts"));
        if (mutation == "artifacts-source-root") artifacts = tree.Sources;
        if (mutation == "artifacts-ancestor") artifacts = tree.Root;
        var intermediate = Path.Combine(artifacts, "obj", mutation == "intermediate-other-project" ? "ForeignProject" : "Sunder.Sdk.Stacks", "debug");
        Directory.CreateDirectory(intermediate);
        var request = tree.Request();
        var lines = File.ReadAllLines(request.Path).Where(line => !line.StartsWith("intermediate-output=", StringComparison.Ordinal)).ToList();
        lines.Add(Line("intermediate-output", intermediate));
        lines.Add(Line("artifacts-path", artifacts));
        if (mutation.StartsWith("source-", StringComparison.Ordinal))
        {
            var source = mutation switch
            {
                "source-outside" => tree.Temp.GetPath("Foreign.cs"),
                "source-sibling" => Path.Combine(artifacts, "obj", "OtherProject", "debug", "Sunder.Sdk.Stacks.GlobalUsings.g.cs"),
                _ => Path.Combine(intermediate, "Sunder.Sdk.Stacks.GlobalUsings.g.cs"),
            };
            source = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(source);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, mutation == "source-injection" ? "#error relocated evaluated source must compile" : "// foreign input");
            lines.Add(Line("source", source));
            if (mutation != "source-unlisted") lines.Add(Line("generated-source", source));
        }
        else if (mutation.StartsWith("sdk-", StringComparison.Ordinal))
        {
            var sdkProject = Path.GetFullPath(Path.Combine(tree.Sources, "../Sunder.Sdk/Sunder.Sdk.csproj"));
            Directory.CreateDirectory(Path.GetDirectoryName(sdkProject)!);
            File.WriteAllText(sdkProject, "<Project />");
            var reference = Path.Combine(artifacts, "obj", "Sunder.Sdk", "debug", "ref", "Sunder.Sdk.dll");
            if (mutation == "sdk-outside") reference = tree.Temp.GetPath("foreign", "Sunder.Sdk.dll");
            reference = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(reference);
            Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
            if (mutation is "sdk-foreign-shape" or "sdk-foreign-version")
            {
                var version = mutation == "sdk-foreign-version" ? "9.0.0.0" : typeof(SunderServiceAttribute).Assembly.GetName().Version!.ToString();
                var compilation = CSharpCompilation.Create("Sunder.Sdk",
                    [CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] public sealed class ForeignSdk {{ }}")],
                    PlatformContractSourceOperations.ReferencePaths().Where(path => Path.GetFileName(path) != "Sunder.Sdk.dll")
                        .Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                Assert.True(compilation.Emit(reference).Success);
            }
            else File.Copy(typeof(SunderServiceAttribute).Assembly.Location, reference);
            lines = lines.Where(line => !line.StartsWith("reference=", StringComparison.Ordinal) || Path.GetFileName(DecodeLine(line)) != "Sunder.Sdk.dll").ToList();
            lines.Add(Line("reference", reference));
            if (mutation != "sdk-no-provenance")
            {
                lines.Add(Line("sdk-project", mutation == "sdk-foreign-project" ? Path.Combine(tree.Sources, "Foreign.csproj") : sdkProject));
                lines.Add(Line("sdk-reference", reference));
            }
        }
        WriteLines(request.Path, lines);
        var error = Assert.Throws<InvalidDataException>(() => CoreStackTransportGeneration.Generate(tree.Root, Proof(request.Path)));
        Assert.Contains(mutation switch
        {
            "source-injection" => "source compilation failed",
            "source-unlisted" => "source inputs must belong",
            "sdk-no-provenance" => "requires evaluated SDK project provenance",
            "sdk-foreign-shape" or "sdk-foreign-version" => "current Sunder.Sdk identity or public shape",
            "sdk-foreign-project" or "sdk-outside" => "current Core SDK project provenance",
            "artifacts-source-root" or "artifacts-ancestor" => "outside Core language source trees",
            "intermediate-other-project" => "evaluated Stack project obj layout",
            _ => "exact evaluated SDK-generated Compile inputs",
        }, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(tree.Output()));
    }

    [Fact]
    public void ExternalArtifacts_RechecksRelocatedGeneratedSourceBeforePublication()
    {
        using var tree = new CoreTree();
        var artifacts = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(tree.Temp.CreateDirectory("external artifacts"));
        var intermediate = Path.Combine(artifacts, "obj", "Sunder.Sdk.Stacks", "debug");
        Directory.CreateDirectory(intermediate);
        var source = Path.Combine(intermediate, "Sunder.Sdk.Stacks.GlobalUsings.g.cs");
        File.WriteAllText(source, "// evaluated SDK generated input");
        var writer = tree.Writer();
        RequestWriter.Value.GetProperty("ArtifactsPath")!.SetValue(writer, artifacts);
        RequestWriter.Value.GetProperty("IntermediateOutputDirectory")!.SetValue(writer, intermediate);
        RequestWriter.Value.GetProperty("GeneratedSources")!.SetValue(writer, new[] { new TaskItem(source) });
        var sources = (TaskItem[])RequestWriter.Value.GetProperty("Sources")!.GetValue(writer)!;
        RequestWriter.Value.GetProperty("Sources")!.SetValue(writer, sources.Append(new TaskItem(source)).ToArray());
        Assert.True(writer.Execute());
        var requestPath = (string)RequestWriter.Value.GetProperty("RequestPath")!.GetValue(writer)!;
        Assert.Throws<IOException>(() => CoreStackTransportGeneration.Generate(tree.Root, Proof(requestPath), beforePublication: _ =>
        {
            var stamp = File.GetLastWriteTimeUtc(source);
            File.WriteAllText(source, "// changed   SDK generated input");
            File.SetLastWriteTimeUtc(source, stamp);
        }));
        Assert.Empty(Directory.GetDirectories(tree.Output()));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task ExistingProject_ExternalArtifactsCliBuildAndPackUseEvaluatedInputs()
    {
        using var tree = new CoreTree();
        var project = CopyBuildProjects(tree);
        var artifacts = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(tree.Temp.CreateDirectory("external artifacts"));
        var feed = tree.Temp.CreateDirectory("local feed");
        var baseline = File.ReadAllBytes(tree.Baseline);
        var sources = tree.SourceSnapshot();
        var captured = await Run(["build", project, "-c", "Release", "--artifacts-path", artifacts,
            "-p:RestoreLockedMode=false", "-p:GeneratePackageOnBuild=false", "-t:Build", "-getItem:Compile,ReferencePathWithRefAssemblies"]);
        using var document = JsonDocument.Parse(captured.StandardOutput[captured.StandardOutput.IndexOf('{')..]);
        var items = document.RootElement.GetProperty("Items");
        var actualSources = items.GetProperty("Compile").EnumerateArray().Select(item => item.GetProperty("FullPath").GetString()!).ToArray();
        var relocated = actualSources.Where(path => GeneratedOutputPathSafety.IsDescendantOf(path, artifacts)).ToArray();
        Assert.Equal(3, relocated.Length);
        var generated = Assert.Single(actualSources, path => Path.GetFileName(path) == CoreStackTransportGeneration.SourceName);
        var transport = File.ReadAllBytes(generated);
        var sdk = Assert.Single(items.GetProperty("ReferencePathWithRefAssemblies").EnumerateArray(), item => Path.GetFileName(item.GetProperty("FullPath").GetString()) == "Sunder.Sdk.dll");
        Assert.True(GeneratedOutputPathSafety.IsDescendantOf(sdk.GetProperty("FullPath").GetString()!, artifacts));
        Assert.Equal("ProjectReference", sdk.GetProperty("ReferenceSourceTarget").GetString());
        Assert.Equal(Path.GetFullPath(Path.Combine(tree.Sources, "../Sunder.Sdk/Sunder.Sdk.csproj")), sdk.GetProperty("MSBuildSourceProjectFile").GetString());
        await Run(["pack", project, "-c", "Release", "--no-build", "--artifacts-path", artifacts, "-o", feed]);
        var assembly = Path.Combine(artifacts, "bin", "Sunder.Sdk.Stacks", "release", "Sunder.Sdk.Stacks.dll");
        using (var archive = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(feed, "Sunder.Sdk.Stacks.*.nupkg"))))
        {
            using var packed = archive.GetEntry("lib/net10.0/Sunder.Sdk.Stacks.dll")!.Open();
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(assembly)), SHA256.HashData(packed));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("Sunder.ExecutionRuntime.Build", StringComparison.Ordinal));
        }
        var buildFeed = tree.Temp.CreateDirectory("pack with build feed");
        await Run(["pack", project, "-c", "Release", "--no-restore", "--artifacts-path", artifacts, "-o", buildFeed,
            "-p:GeneratePackageOnBuild=false"]);
        // The private bootstrap may pack its own dependencies, but never into the caller's feed.
        Assert.Equal(Path.GetFileName(Assert.Single(Directory.GetFiles(feed, "*.nupkg"))),
            Path.GetFileName(Assert.Single(Directory.GetFiles(buildFeed, "*.nupkg"))));
        await Run(["build", project, "-c", "Release", "--no-restore", "--artifacts-path", artifacts,
            "-p:GeneratePackageOnBuild=false", "-t:GetCoreStackHostTransport"]);
        Assert.Single(Directory.GetFiles(Path.Combine(tree.Sources, "obj"), CoreStackTransportGeneration.HostSourceName, SearchOption.AllDirectories));
        // Removing the actual generator must fail Csc; an earlier immutable file is never a fallback.
        var xml = XDocument.Load(project);
        Assert.Single(xml.Descendants("Target"), target => (string?)target.Attribute("Name") == "GenerateCoreStackTransport").Remove();
        xml.Save(project);
        var missing = await Run(["build", project, "-c", "Release", "--no-restore", "--artifacts-path", artifacts,
            "-p:GeneratePackageOnBuild=false"], success: false);
        Assert.Contains("CS8795", missing.CombinedOutput, StringComparison.Ordinal);
        Assert.Equal(transport, File.ReadAllBytes(generated));
        Assert.Equal(baseline, File.ReadAllBytes(tree.Baseline));
        Assert.Equal(sources, tree.SourceSnapshot());

        async Task<CapturedProcessResult> Run(string[] arguments, bool success = true)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = tree.Root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            };
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            foreach (var argument in arguments.Concat(["--disable-build-servers", "--nologo", "-p:UseSharedCompilation=false"])) start.ArgumentList.Add(argument);
            await using var process = CapturedProcess.Start(start);
            var result = await process.WaitForExitAsync(TimeSpan.FromMinutes(5));
            Assert.True(success == (result.ExitCode == 0), result.CombinedOutput);
            return result;
        }
    }
}
