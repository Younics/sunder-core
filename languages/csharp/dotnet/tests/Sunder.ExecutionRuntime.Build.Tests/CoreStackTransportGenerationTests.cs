using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Xunit;
using TaskItem = Microsoft.Build.Utilities.TaskItem;

namespace Sunder.ExecutionRuntime.Build.Tests;

public sealed partial class CoreStackTransportGenerationTests
{
    private const string ProjectRelativePath = "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks";
    private static readonly Lazy<Type> RequestWriter = new(CompileRequestWriter);
    [Fact]
    public void ProjectHook_UsesCscInputsPrivateAtomicWriterAndReturnedImmutableCompilePath()
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures/PlatformCatalog/SdkStacks.csproj"));
        var target = Assert.Single(project.Descendants("Target"), target => (string?)target.Attribute("Name") == "GenerateCoreStackTransport");
        Assert.Contains("FindReferenceAssembliesForReferences", (string)target.Attribute("DependsOnTargets")!, StringComparison.Ordinal);
        var writer = Assert.Single(target.Elements("_SunderWriteCoreStackTransportRequest"));
        Assert.Equal("@(Compile)", (string?)writer.Attribute("Sources"));
        Assert.Equal("@(ReferencePathWithRefAssemblies)", (string?)writer.Attribute("References"));
        Assert.Equal("$(IntermediateOutputPath)", (string?)writer.Attribute("IntermediateOutputDirectory"));
        Assert.Equal("$(ArtifactsPath)", (string?)writer.Attribute("ArtifactsPath"));
        Assert.Equal("$(GeneratedGlobalUsingsFile);$(GeneratedAssemblyInfoFile);$(TargetFrameworkMonikerAssemblyAttributesPath)",
            (string?)writer.Attribute("GeneratedSources"));
        Assert.Equal("@(_CoreStackGeneratedCompile)", (string?)Assert.Single(target.Descendants("Compile"), item => item.Attribute("Remove") is not null).Attribute("Remove"));
        Assert.Equal("@(_CoreStackGeneratedCompile)", (string?)Assert.Single(target.Descendants("Compile"), item => item.Attribute("Include") is not null).Attribute("Include"));
        Assert.Empty(target.Elements("WriteLinesToFile"));
        Assert.Empty(target.Elements("Delete"));
        Assert.Equal("true", (string?)Assert.Single(target.Elements("Exec")).Attribute("ConsoleToMSBuild"));
        var bootstrap = Assert.Single(target.Elements("MSBuild"));
        Assert.Equal("Configuration=$(Configuration)-sunder-core-transport;BuildingSolutionFile=false", (string?)bootstrap.Attribute("Properties"));
        Assert.Contains("TargetFramework", ((string)bootstrap.Attribute("RemoveProperties")!).Split(';'));
        Assert.Contains("BuildProjectReferences", ((string)bootstrap.Attribute("RemoveProperties")!).Split(';'));
        Assert.Contains("CurrentSolutionConfigurationContents", ((string)bootstrap.Attribute("RemoveProperties")!).Split(';'));
        Assert.Contains("GeneratePackageOnBuild", ((string)bootstrap.Attribute("RemoveProperties")!).Split(';'));
        Assert.Contains("PackageOutputPath", ((string)bootstrap.Attribute("RemoveProperties")!).Split(';'));
        Assert.Contains("FileMode.CreateNew", Assert.Single(project.Descendants("Code")).Value, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", Assert.Single(project.Descendants("Code")).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_RemovesTheUnownedPrepareRequestSurface()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await ExecutionRuntimeBuildCli.RunAsync(["core-stack-transport", "prepare-request"], output, error));
    }

    [Fact]
    public void Producer_UsesRealSourceAndFixedBaselineAndPreservesUnchangedBytesAndMtimes()
    {
        using var tree = new CoreTree();
        var baseline = File.ReadAllBytes(tree.Baseline);
        var source = tree.SourceSnapshot();
        var request = tree.Request();
        var path = CoreStackTransportGeneration.Generate(tree.Root, request);
        Assert.False(File.Exists(request.Path));
        var generated = File.ReadAllBytes(path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(generated)), Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Contains("CreateSunderRpcServiceHandler", Encoding.UTF8.GetString(generated), StringComparison.Ordinal);
        Assert.DoesNotContain("Sunder.Sdk.Worker", Encoding.UTF8.GetString(generated), StringComparison.Ordinal);
        var marker = Path.Combine(Path.GetDirectoryName(path)!, CoreStackTransportGeneration.MarkerName);
        Assert.EndsWith(Convert.ToHexStringLower(SHA256.HashData(generated)) + "\n", File.ReadAllText(marker), StringComparison.Ordinal);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var stamp = File.GetLastWriteTimeUtc(path);
        var markerStamp = File.GetLastWriteTimeUtc(marker);
        Assert.Equal(path, CoreStackTransportGeneration.Generate(tree.Root, tree.Request()));
        Assert.Equal(generated, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(markerStamp, File.GetLastWriteTimeUtc(marker));
        Assert.Equal(generated, File.ReadAllBytes(CoreStackTransportGeneration.Generate(tree.Root, tree.Request("Release"))));
        Assert.Equal(source, tree.SourceSnapshot());
        Assert.Equal(baseline, File.ReadAllBytes(tree.Baseline));
    }

    [Fact]
    public void HostProducer_UsesCheckedSourceAndKeepsRoleOutputSeparateAndImmutable()
    {
        using var tree = new CoreTree();
        var baseline = File.ReadAllBytes(tree.Baseline); var source = tree.SourceSnapshot();
        // This oracle/distribution file is not a source-generation input.
        Directory.CreateDirectory(Path.Combine(tree.Sources, "Contracts"));
        File.WriteAllText(Path.Combine(tree.Sources, "Contracts", "sunder.stack.contributor.rpc.json"), "not a descriptor");
        var sdk = CoreStackTransportGeneration.Generate(tree.Root, tree.Request());
        var request = tree.Request(); var host = CoreStackTransportGeneration.Generate(tree.Root, request, hostClient: true);
        Assert.False(File.Exists(request.Path)); Assert.NotEqual(Path.GetDirectoryName(sdk), Path.GetDirectoryName(host));
        Assert.Equal(CoreStackTransportGeneration.HostSourceName, Path.GetFileName(host));
        var bytes = File.ReadAllBytes(host); var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("namespace Sunder.Runtime.Host.StackTransport", text, StringComparison.Ordinal);
        Assert.Contains("internal sealed class StackDomainClient", text, StringComparison.Ordinal);
        Assert.Contains("38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898", text, StringComparison.Ordinal);
        Assert.Contains("6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderStackContributorRpc.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StackContributorRpcClient", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sunder.Sdk.Worker", text, StringComparison.Ordinal);
        File.SetLastWriteTimeUtc(host, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var stamp = EngineeringFileOperations.ObserveFile(host);
        Assert.Equal(host, CoreStackTransportGeneration.Generate(tree.Root, tree.Request(), hostClient: true));
        Assert.Equal(bytes, File.ReadAllBytes(host)); Assert.Equal(stamp, EngineeringFileOperations.ObserveFile(host));
        Assert.Equal(bytes, File.ReadAllBytes(CoreStackTransportGeneration.Generate(tree.Root, tree.Request("Release"), hostClient: true)));
        Assert.Equal(source, tree.SourceSnapshot()); Assert.Equal(baseline, File.ReadAllBytes(tree.Baseline));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("membership")]
    [InlineData("stage")]
    [InlineData("destination")]
    public void HostProducer_FencesInputsAndPreservesChangedPublicationBytes(string failure)
    {
        using var tree = new CoreTree();
        string? retained = null;
        var error = Record.Exception(() => CoreStackTransportGeneration.Generate(tree.Root, tree.Request(), hostClient: true,
            beforePublication: stage =>
            {
                if (failure == "source") File.AppendAllText(Path.Combine(tree.Sources, "StackContributorServiceAdapter.cs"), "\n// edited during publication\n");
                if (failure == "membership") File.WriteAllText(Path.Combine(tree.Sources, "Added.cs"), "// new ordinary source");
                if (failure == "stage") { retained = stage; File.WriteAllText(stage, "preserve altered source"); }
            }, beforeMove: destination =>
            {
                if (failure != "destination") return;
                Directory.CreateDirectory(destination); retained = Path.Combine(destination, "foreign.txt"); File.WriteAllText(retained, "preserve destination");
            }));
        Assert.NotNull(error);
        if (retained is not null) Assert.Contains("preserve", File.ReadAllText(retained), StringComparison.Ordinal);
    }

    [Fact]
    public void HostProducer_GenerationTracksDomainSourceNamesWithoutChangingWireIdentity()
    {
        using var tree = new CoreTree();
        var first = CoreStackTransportGeneration.Generate(tree.Root, tree.Request(), hostClient: true);
        var before = File.ReadAllBytes(first);
        foreach (var file in new[] { "StackContributorServiceAdapter.cs", "StackContributorRpcServiceAdapter.cs" })
        {
            var path = Path.Combine(tree.Sources, file); var text = File.ReadAllText(path);
            File.WriteAllText(path, file == "StackContributorServiceAdapter.cs"
                ? text.Replace("GetMetadataAsync()", "ReadMetadataAsync()", StringComparison.Ordinal)
                : text.Replace("_adapter.GetMetadataAsync()", "_adapter.ReadMetadataAsync()", StringComparison.Ordinal));
        }
        var second = CoreStackTransportGeneration.Generate(tree.Root, tree.Request(), hostClient: true);
        Assert.NotEqual(first, second); Assert.Equal(before, File.ReadAllBytes(first));
        Assert.Contains(" ReadMetadataAsync(", File.ReadAllText(second), StringComparison.Ordinal);
        Assert.Contains("38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898", File.ReadAllText(second), StringComparison.Ordinal);
    }

    [Fact]
    public void HostProjectHook_ConsumesOnlyTheCoreSourceExportWithoutSdkClientDistribution()
    {
        var root = FindRepositoryRoot();
        var sdk = XDocument.Load(Path.Combine(root, ProjectRelativePath, "Sunder.Sdk.Stacks.csproj"));
        var export = Assert.Single(sdk.Descendants("Target"), target => (string?)target.Attribute("Name") == "GetCoreStackHostTransport");
        Assert.Empty(export.Descendants("Compile"));
        Assert.Equal("@(_CoreStackHostGeneratedCompile)", (string?)export.Attribute("Returns"));
        Assert.Contains("generate-host", Assert.Single(export.Descendants("Exec")).Attribute("Command")!.Value, StringComparison.Ordinal);
        var host = XDocument.Load(Path.Combine(root, "languages/csharp/dotnet/host/Sunder.Runtime.Host/Sunder.Runtime.Host.csproj"));
        var target = Assert.Single(host.Descendants("Target"), item => (string?)item.Attribute("Name") == "GenerateCoreStackHostTransport");
        Assert.Equal("GetCoreStackHostTransport", Assert.Single(target.Descendants("MSBuild")).Attribute("Targets")!.Value);
        Assert.Equal("@(_CoreStackHostCompile)", Assert.Single(target.Descendants("Compile"), item => item.Attribute("Include") is not null).Attribute("Include")!.Value);
        Assert.Empty(target.Descendants("FileWrites"));
    }

    [Fact]
    public void Producer_RequiresEveryOrdinarySourceEvenWhenOmissionWouldNotChangeTheContract()
    {
        using var tree = new CoreTree();
        var omitted = Path.Combine(tree.Sources, "NotRpc.cs");
        File.WriteAllText(omitted, "// ordinary source with no contract contribution");
        var request = tree.Request();
        WriteLines(request.Path, File.ReadAllLines(request.Path).Where(line => line != Line("source", omitted)).ToArray());
        var error = Assert.Throws<InvalidDataException>(() => CoreStackTransportGeneration.Generate(tree.Root, Proof(request.Path)));
        Assert.Contains("entire ordinary Stack source inventory", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(tree.Output()));
    }

    [Theory]
    [InlineData("regular")]
    [InlineData("symbolic")]
    [InlineData("hard")]
    public void PrivateWriter_CreateNewNeverChangesAnExistingFileOrLinkTarget(string kind)
    {
        if (OperatingSystem.IsWindows() && kind == "symbolic") return;
        using var tree = new CoreTree();
        var writer = tree.Writer();
        var path = (string)RequestWriter.Value.GetProperty("RequestPath")!.GetValue(writer)!;
        var source = Path.Combine(tree.Sources, "StackContributorRpc.cs");
        var bytes = File.ReadAllBytes(source);
        var stamp = EngineeringFileOperations.ObserveFile(source);
        if (kind == "regular") File.Copy(source, path);
        else Link(source, path, kind);
        Assert.False(writer.Execute());
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(stamp, EngineeringFileOperations.ObserveFile(source));
        if (kind == "symbolic") Assert.NotNull(new FileInfo(path).LinkTarget);
    }

    [Theory]
    [InlineData("regular", false)]
    [InlineData("regular", true)]
    [InlineData("symbolic", false)]
    [InlineData("symbolic", true)]
    [InlineData("hard", false)]
    [InlineData("hard", true)]
    public void RequestProofAndCleanup_PreserveReplacementsBeforeReadingOrDuringGeneration(string kind, bool duringGeneration)
    {
        if (OperatingSystem.IsWindows() && kind == "symbolic") return;
        using var tree = new CoreTree();
        var request = tree.Request();
        var target = tree.Temp.WriteAllText("foreign request replacement", "not the request writer's bytes");
        var stamp = EngineeringFileOperations.ObserveFile(target);
        void Replace()
        {
            File.Delete(request.Path);
            if (kind == "regular") File.Copy(target, request.Path);
            else Link(target, request.Path, kind);
        }
        if (!duringGeneration) Replace();
        var error = Record.Exception(() => CoreStackTransportGeneration.Generate(tree.Root, request,
            beforePublication: _ => Replace()));
        Assert.True(error is IOException or InvalidDataException, error?.ToString());
        Assert.Equal("not the request writer's bytes", File.ReadAllText(request.Path));
        Assert.Equal(stamp, EngineeringFileOperations.ObserveFile(target));
        if (kind == "symbolic") Assert.NotNull(new FileInfo(request.Path).LinkTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestProofAndCleanup_RejectSameBytesWithAReplacementStamp(bool duringGeneration)
    {
        using var tree = new CoreTree();
        var request = tree.Request();
        var bytes = File.ReadAllBytes(request.Path);
        void Replace()
        {
            File.Delete(request.Path);
            File.WriteAllBytes(request.Path, bytes);
            File.SetLastWriteTimeUtc(request.Path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        }
        if (!duringGeneration) Replace();
        Assert.Throws<IOException>(() => CoreStackTransportGeneration.Generate(tree.Root, request, beforePublication: _ => Replace()));
        Assert.Equal(bytes, File.ReadAllBytes(request.Path));
    }

    [Fact]
    public void PrivateWriter_EncodesPathsAndCapturesAutoGenItemsWithoutHidingThem()
    {
        using var tree = new CoreTree();
        var ordinary = Path.Combine(tree.Sources, "Equals=Sign.cs");
        File.WriteAllText(ordinary, "// an equals sign is data, not a request field separator");
        var writer = tree.Writer();
        var autoGen = Path.Combine(tree.Sources, "obj", "AutoGen.cs");
        File.WriteAllText(autoGen, DebugDrift);
        var inputs = (TaskItem[])RequestWriter.Value.GetProperty("Sources")!.GetValue(writer)!;
        var item = new TaskItem(autoGen);
        item.SetMetadata("AutoGen", "true");
        RequestWriter.Value.GetProperty("Sources")!.SetValue(writer, inputs.Append(item).ToArray());
        Assert.True(writer.Execute());
        var path = (string)RequestWriter.Value.GetProperty("RequestPath")!.GetValue(writer)!;
        Assert.Contains(Line("source", ordinary), File.ReadAllLines(path));
        Assert.Contains(Line("source", autoGen), File.ReadAllLines(path));
        var error = Assert.Throws<InvalidDataException>(() => CoreStackTransportGeneration.Generate(tree.Root, Proof(path)));
        Assert.Contains("baseline-drift", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BAD\nsource=other.cs")]
    [InlineData("not-a-define")]
    [InlineData("1INVALID")]
    public void PrivateWriter_RejectsInvalidDefineIdentifiersBeforeCreatingARequest(string value)
    {
        using var tree = new CoreTree();
        var writer = tree.Writer();
        RequestWriter.Value.GetProperty("DefineConstants")!.SetValue(writer, value);
        Assert.False(writer.Execute());
        Assert.False(File.Exists((string)RequestWriter.Value.GetProperty("RequestPath")!.GetValue(writer)!));
    }

    [Fact]
    public void ImmutableOutputs_ReuseReadOnlyContentAndPublishNewHashesWithoutTouchingOldTrees()
    {
        using var tree = new CoreTree();
        var first = CoreStackTransportGeneration.Generate(tree.Root, tree.Request());
        var oldDirectory = Path.GetDirectoryName(first)!;
        var bytes = File.ReadAllBytes(first);
        var stamp = EngineeringFileOperations.ObserveFile(first);
        foreach (var path in Directory.GetFiles(oldDirectory)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(oldDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.Equal(first, CoreStackTransportGeneration.Generate(tree.Root, tree.Request()));
        var adapter = Path.Combine(tree.Sources, "StackContributorRpcServiceAdapter.cs");
        var source = File.ReadAllText(adapter);
        Assert.Contains("GetMetadataAsync(StackRpcEmpty", source, StringComparison.Ordinal);
        File.WriteAllText(adapter, source.Replace("GetMetadataAsync(StackRpcEmpty", "ReadMetadataAsync(StackRpcEmpty", StringComparison.Ordinal));
        var second = CoreStackTransportGeneration.Generate(tree.Root, tree.Request());
        Assert.NotEqual(first, second);
        Assert.Equal(second, CoreStackTransportGeneration.Generate(tree.Root, tree.Request()));
        Assert.Equal(bytes, File.ReadAllBytes(first));
        Assert.Equal(stamp, EngineeringFileOperations.ObserveFile(first));
        Assert.Equal(2, Directory.GetDirectories(tree.Output()).Length);
    }

    [Fact]
    public void ImmutableOutputs_DoNotCleanUpForeignOrCrashLeftTrees()
    {
        using var tree = new CoreTree();
        Directory.CreateDirectory(tree.Output());
        var orphan = Directory.CreateDirectory(Path.Combine(tree.Output(), ".stage-foreign")).FullName;
        var partial = Directory.CreateDirectory(Path.Combine(tree.Output(), new string('0', 64))).FullName;
        File.WriteAllText(Path.Combine(orphan, "foreign"), "keep orphan");
        File.WriteAllText(Path.Combine(partial, CoreStackTransportGeneration.SourceName), "keep partial");
        _ = CoreStackTransportGeneration.Generate(tree.Root, tree.Request());
        Assert.Equal("keep orphan", File.ReadAllText(Path.Combine(orphan, "foreign")));
        Assert.Equal("keep partial", File.ReadAllText(Path.Combine(partial, CoreStackTransportGeneration.SourceName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmutablePublication_NeverRollsBackOrDeletesAForeignDestinationAppearingAtMove(bool symbolic)
    {
        if (OperatingSystem.IsWindows() && symbolic) return;
        using var tree = new CoreTree();
        var foreign = tree.Temp.CreateDirectory("foreign destination");
        File.WriteAllText(Path.Combine(foreign, "sentinel"), "preserve winner");
        string? destination = null, stage = null;
        Assert.Throws<IOException>(() => CoreStackTransportGeneration.Generate(tree.Root, tree.Request(),
            beforePublication: path => stage = Path.GetDirectoryName(path), beforeMove: path =>
            {
                destination = path;
                if (symbolic) Directory.CreateSymbolicLink(path, foreign);
                else
                {
                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, "sentinel"), "preserve winner");
                }
            }));
        Assert.NotNull(destination);
        Assert.Equal("preserve winner", File.ReadAllText(Path.Combine(destination, "sentinel")));
        Assert.Equal("preserve winner", File.ReadAllText(Path.Combine(foreign, "sentinel")));
        Assert.False(Directory.Exists(stage));
        if (symbolic) Assert.NotNull(new DirectoryInfo(destination).LinkTarget);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("drift")]
    [InlineData("non-rpc")]
    [InlineData("factory")]
    public void Producer_NeverFallsBackToPreviousGeneratedTransport(string mutation)
    {
        using var tree = new CoreTree();
        var path = CoreStackTransportGeneration.Generate(tree.Root, tree.Request());
        var previous = File.ReadAllBytes(path);
        if (mutation == "baseline") File.Delete(tree.Baseline);
        else if (mutation == "drift") File.WriteAllText(Path.Combine(tree.Sources, "DebugOnly.cs"), DebugDrift);
        else if (mutation == "non-rpc") File.WriteAllText(Path.Combine(tree.Sources, "Broken.cs"), "#error Actual evaluated non-RPC source must compile");
        else
        {
            var adapter = Path.Combine(tree.Sources, "StackContributorRpcServiceAdapter.cs");
            File.WriteAllText(adapter, File.ReadAllText(adapter).Replace(
                "internal partial ISunderRpcServiceHandler CreateSunderRpcServiceHandler();",
                "internal ISunderRpcServiceHandler CreateSunderRpcServiceHandler() => null!;", StringComparison.Ordinal));
        }
        Assert.Throws<InvalidDataException>(() => CoreStackTransportGeneration.Generate(tree.Root, tree.Request()));
        Assert.Equal(previous, File.ReadAllBytes(path));
    }

    [Fact]
    public void Producer_ValidatesActualDebugDefinesRatherThanApprovingFixedReleaseSource()
    {
        using var tree = new CoreTree();
        File.WriteAllText(Path.Combine(tree.Sources, "DebugOnly.cs"), DebugDrift);
        new PlatformContractSourceOperations(tree.Root).Check();
        _ = CoreStackTransportGeneration.Generate(tree.Root, tree.Request("Release"));
        var error = Assert.Throws<InvalidDataException>(() => CoreStackTransportGeneration.Generate(tree.Root, tree.Request()));
        Assert.Contains("baseline-drift", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(tree.Output()));
    }

    [Theory]
    [InlineData("unmarked")]
    [InlineData("extra")]
    [InlineData("modified")]
    [InlineData("link")]
    public void Producer_RejectsForeignOrUnsafeOutputsWithoutDeletingThem(string mutation)
    {
        if (OperatingSystem.IsWindows() && mutation == "link") return;
        using var tree = new CoreTree();
        var path = CoreStackTransportGeneration.Generate(tree.Root, tree.Request());
        var output = Path.GetDirectoryName(path)!;
        var previous = File.ReadAllBytes(path);
        var sentinel = tree.Temp.WriteAllText("sentinel", "foreign bytes");
        switch (mutation)
        {
            case "unmarked": File.Delete(Path.Combine(output, CoreStackTransportGeneration.MarkerName)); break;
            case "extra": File.WriteAllText(Path.Combine(output, "foreign.txt"), "foreign bytes"); break;
            case "modified": File.WriteAllText(path, "foreign bytes"); break;
            case "link": File.Delete(path); File.CreateSymbolicLink(path, sentinel); break;
        }
        var before = tree.OutputSnapshot();
        var error = Record.Exception(() => CoreStackTransportGeneration.Generate(tree.Root, tree.Request()));
        Assert.True(error is IOException or InvalidDataException, error?.ToString());
        Assert.Equal(before, tree.OutputSnapshot());
        Assert.Equal("foreign bytes", File.ReadAllText(sentinel));
        if (mutation is not ("modified" or "link")) Assert.Equal(previous, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("membership")]
    [InlineData("baseline")]
    [InlineData("request")]
    [InlineData("reference")]
    [InlineData("stage")]
    [InlineData("cancel")]
    public void Producer_RechecksInputsAndStageBeforePublication(string mutation)
    {
        using var tree = new CoreTree();
        using var cancellation = new CancellationTokenSource();
        var request = tree.Request();
        var sdkProject = Path.GetFullPath(Path.Combine(tree.Sources, "../Sunder.Sdk/Sunder.Sdk.csproj"));
        var reference = Path.Combine(Path.GetDirectoryName(sdkProject)!, "obj", "references", "Sunder.Sdk.dll");
        if (mutation == "reference")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
            File.WriteAllText(sdkProject, "<Project />");
            var original = typeof(Sunder.Sdk.Services.SunderServiceAttribute).Assembly.Location;
            File.Copy(original, reference);
            var lines = File.ReadAllLines(request.Path).Select(line => line.StartsWith("reference=", StringComparison.Ordinal)
                && Path.GetFileName(DecodeLine(line)) == "Sunder.Sdk.dll" ? Line("reference", reference) : line).ToArray();
            WriteLines(request.Path, lines.Concat([Line("sdk-project", sdkProject), Line("sdk-reference", reference)]));
            request = Proof(request.Path);
        }
        string? stage = null;
        var error = Record.Exception(() => CoreStackTransportGeneration.Generate(tree.Root, request, cancellation.Token, path =>
        {
            stage = path;
            if (mutation == "cancel") cancellation.Cancel();
            else if (mutation == "membership") File.WriteAllText(Path.Combine(tree.Sources, "Added.cs"), "// concurrent source");
            else
            {
                var changed = mutation switch
                {
                    "source" => Path.Combine(tree.Sources, "StackContributorRpc.cs"),
                    "baseline" => tree.Baseline, "request" => request.Path, "reference" => reference, _ => path,
                };
                var stamp = File.GetLastWriteTimeUtc(changed);
                var bytes = File.ReadAllBytes(changed);
                bytes[^1] ^= 1;
                File.WriteAllBytes(changed, bytes);
                File.SetLastWriteTimeUtc(changed, stamp);
            }
        }));
        Assert.NotNull(stage);
        Assert.True(error is IOException or OperationCanceledException, error?.ToString());
        Assert.DoesNotContain(Directory.GetDirectories(tree.Output()), path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal));
        Assert.Equal(mutation == "stage", File.Exists(stage));
        Assert.Equal(mutation == "request", File.Exists(request.Path));
    }

    [Theory]
    [InlineData("target-framework=net9.0")]
    [InlineData("nullable=unknown")]
    [InlineData("configuration=../outside")]
    [InlineData("authority=package")]
    [InlineData("source=relative.cs")]
    public void Request_RejectsUnsupportedOrUnboundedContext(string changed)
    {
        using var tree = new CoreTree();
        var request = tree.Request();
        var name = changed[..changed.IndexOf('=')];
        var lines = File.ReadAllLines(request.Path).Where(line => !line.StartsWith(name + "=", StringComparison.Ordinal))
            .Append(Line(name, changed[(changed.IndexOf('=') + 1)..]));
        WriteLines(request.Path, lines.ToArray());
        Assert.Throws<InvalidDataException>(() => CoreStackTransportGeneration.Generate(tree.Root, Proof(request.Path)));
        Assert.False(Directory.Exists(tree.Output()));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("source-count")]
    [InlineData("duplicate-setting")]
    [InlineData("tool-reference")]
    public void Request_RejectsOversizedAmbiguousAndNonSdkInputs(string invalid)
    {
        using var tree = new CoreTree();
        var request = tree.Request();
        if (invalid == "bytes") File.WriteAllText(request.Path, new string('x', 1_048_577));
        else WriteLines(request.Path, File.ReadAllLines(request.Path).Concat(invalid switch
        {
            "source-count" => Enumerable.Repeat(Line("source", Path.Combine(tree.Sources, "StackContributorRpc.cs")), 65),
            "duplicate-setting" => [Line("nullable", "enable")],
            _ => [Line("reference", typeof(ExecutionRuntimeBuildCli).Assembly.Location)],
        }));
        Assert.Throws<InvalidDataException>(() => CoreStackTransportGeneration.Generate(tree.Root, Proof(request.Path)));
        Assert.False(Directory.Exists(tree.Output()));
    }

    [Theory]
    [InlineData("--baseline")]
    [InlineData("--output")]
    [InlineData("--authority")]
    [InlineData("--signer-adapter")]
    public async Task Cli_RejectsAuthoritySigningAndOutputOverrides(string option)
    {
        using var tree = new CoreTree();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var request = tree.Request();
        Assert.Equal(2, await ExecutionRuntimeBuildCli.RunAsync(
            ["core-stack-transport", "generate", "--core-root", tree.Root, "--request", request.Path,
                "--request-sha256", request.Sha256, "--request-stamp", request.Stamp, option, "untrusted"], output, error));
        Assert.False(Directory.Exists(tree.Output()));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task Clean_TraversesRuntimeSdkButNeverTheBuildOnlyProducer()
    {
        using var tree = new CoreTree();
        var project = CopyBuildProjects(tree);
        var targets = Path.Combine(tree.Root, "languages/csharp/dotnet/Directory.Build.targets");
        new XDocument(new XElement("Project", new XElement("Target",
            new XAttribute("Name", "ObserveActualCoreClean"), new XAttribute("BeforeTargets", "CoreClean"),
            new XElement("MakeDir", new XAttribute("Directories", "$(MSBuildProjectDirectory)/obj")),
            new XElement("Touch", new XAttribute("Files", "$(MSBuildProjectDirectory)/obj/core-stack-clean.trace"),
                new XAttribute("AlwaysCreate", "true")))))
            .Save(targets);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = tree.Root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in new[] { "msbuild", project, "-t:Clean", "-nologo", "-nodeReuse:false", "-verbosity:normal",
                     "-getItem:ProjectReference,_MSBuildProjectReferenceExistent" }) start.ArgumentList.Add(argument);
        await using var process = CapturedProcess.Start(start);
        var result = await process.EnsureSuccessAsync(TimeSpan.FromMinutes(2));
        Assert.True(File.Exists(Path.Combine(tree.Root, "languages/csharp/dotnet/packages/Sunder.Sdk/obj/core-stack-clean.trace")));
        Assert.False(File.Exists(Path.Combine(tree.Root, "languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/obj/core-stack-clean.trace")),
            "CoreClean reached the build-only producer. Evaluated references: " + result.StandardOutput);
        using var captured = JsonDocument.Parse(result.StandardOutput[result.StandardOutput.IndexOf('{')..]);
        var items = captured.RootElement.GetProperty("Items");
        Assert.Equal("Sunder.Sdk.csproj", Path.GetFileName(Assert.Single(items.GetProperty("_MSBuildProjectReferenceExistent").EnumerateArray())
            .GetProperty("FullPath").GetString()));
        var producer = Assert.Single(items.GetProperty("ProjectReference").EnumerateArray(), item =>
            Path.GetFileName(item.GetProperty("FullPath").GetString()) == "Sunder.ExecutionRuntime.Build.csproj");
        Assert.Equal("false", producer.GetProperty("BuildReference").GetString());
        Assert.Equal("false", producer.GetProperty("ReferenceOutputAssembly").GetString());
        Assert.Equal("false", producer.GetProperty("Private").GetString());
        Assert.Equal("all", producer.GetProperty("PrivateAssets").GetString());
        Assert.Equal("all", producer.GetProperty("ExcludeAssets").GetString());
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Large")]
    public async Task ExistingProject_CleanBuildIncrementalRebuildDebugReleaseAndFailuresUseTheRealProducer()
    {
        using var tree = new CoreTree();
        var project = CopyBuildProjects(tree);
        var inspectedProject = XDocument.Load(project);
        Assert.Single(inspectedProject.Descendants("Target"), target => (string?)target.Attribute("Name") == "GenerateCoreStackTransport")
            .Element("_SunderWriteCoreStackTransportRequest")!.AddAfterSelf(new XElement("ReadLinesFromFile",
                new XAttribute("File", "$(_CoreStackRequest)"), new XElement("Output", new XAttribute("TaskParameter", "Lines"),
                    new XAttribute("ItemName", "_CapturedCoreRequest"))));
        inspectedProject.Save(project);
        await Run(["restore", project, "--nologo", "-p:RestoreLockedMode=false"]);
        var baseline = File.ReadAllBytes(tree.Baseline);
        var sourceSnapshot = tree.SourceSnapshot();
        byte[]? debugBytes = null;
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var capture = await Build(configuration, inspect: true);
            var toolRoot = Path.Combine(tree.Root, "languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build");
            var bootstrapTool = Path.Combine(toolRoot, "bin", configuration + "-sunder-core-transport", "net10.0", "Sunder.ExecutionRuntime.Build.dll");
            Assert.True(File.Exists(bootstrapTool));
            Assert.False(File.Exists(Path.Combine(toolRoot, "bin", configuration, "net10.0", "Sunder.ExecutionRuntime.Build.dll")));
            var bootstrapReference = Path.Combine(tree.Root, "languages/csharp/dotnet/packages/Sunder.Package.Build/obj",
                configuration + "-sunder-core-transport", "net10.0/ref/Sunder.Package.Build.dll");
            var referenceHash = SHA256.HashData(File.ReadAllBytes(bootstrapReference));
            using var captured = JsonDocument.Parse(capture.StandardOutput[capture.StandardOutput.IndexOf('{')..]);
            var items = captured.RootElement.GetProperty("Items");
            var actualReferences = items.GetProperty("ReferencePathWithRefAssemblies").EnumerateArray()
                .Select(item => item.GetProperty("FullPath").GetString()!).ToArray();
            Assert.Contains(actualReferences, path => Path.GetFileName(path) == "Microsoft.Win32.Primitives.dll");
            Assert.Contains(actualReferences, path => Path.GetFileName(path) == "Microsoft.Win32.Registry.dll");
            var sdkReference = Assert.Single(actualReferences, path => Path.GetFileName(path) == "Sunder.Sdk.dll");
            Assert.Contains("/ref/", sdkReference.Replace('\\', '/'), StringComparison.Ordinal);
            // Query-result reserved metadata can be reevaluated after MSBuild restores its process cwd.
            var actualCompile = items.GetProperty("Compile").EnumerateArray()
                .Select(item => Path.GetFullPath(item.GetProperty("Identity").GetString()!, tree.Sources)).ToArray();
            var path = Assert.Single(actualCompile, path => Path.GetFileName(path) == CoreStackTransportGeneration.SourceName);
            var properties = captured.RootElement.GetProperty("Properties");
            var defines = properties.GetProperty("DefineConstants").GetString()!.Split([';', ','], StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim());
            var expectedRequest = new[] { "sunder.core.stack-transport.request.v1", Line("configuration", configuration),
                    Line("target-framework", "net10.0"), Line("nullable", "enable"),
                    Line("intermediate-output", GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
                        Path.GetFullPath(properties.GetProperty("IntermediateOutputPath").GetString()!.Replace('\\', Path.DirectorySeparatorChar), tree.Sources))) }
                .Concat(new[] { "GeneratedGlobalUsingsFile", "GeneratedAssemblyInfoFile", "TargetFrameworkMonikerAssemblyAttributesPath" }
                    .Select(name => Path.GetFullPath(properties.GetProperty(name).GetString()!.Replace('\\', Path.DirectorySeparatorChar), tree.Sources))
                    .Where(source => actualCompile.Contains(source, StringComparer.Ordinal))
                    .Select(source => Line("generated-source", GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(source))))
                .Concat(actualCompile.Where(source => source != path).Select(source => Line("source", GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(source))))
                .Concat(items.GetProperty("ReferencePathWithRefAssemblies").EnumerateArray().SelectMany(item =>
                {
                    var reference = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(item.GetProperty("FullPath").GetString()!);
                    return Path.GetFileName(reference) == "Sunder.Sdk.dll"
                        ? new[] { Line("reference", reference), Line("sdk-project", GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(item.GetProperty("MSBuildSourceProjectFile").GetString()!)), Line("sdk-reference", reference) }
                        : new[] { Line("reference", reference) };
                }))
                .Concat(defines.Select(define => Line("define", define)));
            var capturedRequest = items.GetProperty("_CapturedCoreRequest").EnumerateArray().Select(item => item.GetProperty("Identity").GetString()!).ToArray();
            Assert.True(expectedRequest.SequenceEqual(capturedRequest), string.Join('\n',
                expectedRequest.Except(capturedRequest).Select(line => "expected: " + line.Split('=')[0] + "=" + DecodeLine(line))
                    .Concat(capturedRequest.Except(expectedRequest).Select(line => "actual: " + line.Split('=')[0] + "=" + DecodeLine(line)))));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', expectedRequest) + "\n"))),
                properties.GetProperty("_CoreStackRequestSha256").GetString());
            var generated = File.ReadAllBytes(path);
            if (debugBytes is null) debugBytes = generated;
            else Assert.Equal(debugBytes, generated);
            var stamp = File.GetLastWriteTimeUtc(path);
            var assembly = Path.Combine(tree.Sources, "bin", configuration, "net10.0", "Sunder.Sdk.Stacks.dll");
            var assemblyStamp = File.GetLastWriteTimeUtc(assembly);
            Assert.DoesNotContain(Directory.GetFileSystemEntries(Path.GetDirectoryName(assembly)!),
                file => Path.GetFileName(file).StartsWith("Sunder.ExecutionRuntime.Build", StringComparison.Ordinal));
            await Build(configuration);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
            Assert.Equal(assemblyStamp, File.GetLastWriteTimeUtc(assembly));
            await Build(configuration, "Rebuild");
            Assert.Equal(generated, File.ReadAllBytes(path));
            using var pe = new PEReader(File.OpenRead(assembly));
            var metadata = pe.GetMetadataReader();
            Assert.All(metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)), name =>
                Assert.True(name == "Sunder.Sdk" || name.StartsWith("System.", StringComparison.Ordinal), name));
            await Run(["build", Path.Combine(toolRoot, "Sunder.ExecutionRuntime.Build.csproj"), "--no-restore", "-t:Clean", $"-p:Configuration={configuration}"]);
            Assert.Equal(referenceHash, SHA256.HashData(File.ReadAllBytes(bootstrapReference)));
        }
        var debugPath = Assert.Single(Directory.GetFiles(tree.Output(), CoreStackTransportGeneration.SourceName, SearchOption.AllDirectories));
        var sentinel = Path.Combine(Path.GetDirectoryName(debugPath)!, "foreign.txt");
        File.WriteAllText(sentinel, "preserve");
        await Build("Debug", success: false);
        Assert.Equal("preserve", File.ReadAllText(sentinel));
        File.Delete(sentinel);
        // AutoGen metadata is not an exclusion: Csc consumes this source, so the producer must too.
        var probe = Path.Combine(tree.Sources, "obj", "AutoGenProbe.cs");
        File.WriteAllText(probe, DebugDrift);
        var document = XDocument.Load(project);
        document.Root!.Add(new XElement("ItemGroup", new XElement("Compile",
            new XAttribute("Include", probe), new XAttribute("AutoGen", "true"))));
        document.Save(project);
        await Build("Release");
        await Build("Debug", success: false);
        document.Root.Elements("ItemGroup").Last().Remove();
        document.Save(project);
        File.Delete(probe);
        document.Root.Add(new XElement("ItemGroup", new XElement("Compile", new XAttribute("Remove", "StackPayloadHandles.cs"))));
        document.Save(project);
        await Build("Debug", success: false);
        document.Root.Elements("ItemGroup").Last().Remove();
        document.Save(project);
        File.Delete(tree.Baseline);
        await Build("Debug", success: false);
        Assert.Equal(debugBytes, File.ReadAllBytes(debugPath));
        Assert.Equal(sourceSnapshot, tree.SourceSnapshot());
        File.WriteAllBytes(tree.Baseline, baseline);

        Task<CapturedProcessResult> Build(string configuration, string target = "Build", bool success = true, bool inspect = false)
            => Run(["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-p:GeneratePackageOnBuild=false",
                "-p:UseSharedCompilation=false", $"-p:Configuration={configuration}", $"-t:{target}",
                .. (inspect ? new[] { "-getItem:Compile,ReferencePathWithRefAssemblies,_CapturedCoreRequest", "-getProperty:DefineConstants,_CoreStackRequestSha256,IntermediateOutputPath,GeneratedGlobalUsingsFile,GeneratedAssemblyInfoFile,TargetFrameworkMonikerAssemblyAttributesPath" } : [])], success);
        async Task<CapturedProcessResult> Run(string[] arguments, bool success = true)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = tree.Root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            };
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            await using var process = CapturedProcess.Start(start);
            var result = await process.WaitForExitAsync(TimeSpan.FromMinutes(5));
            Assert.True(success == (result.ExitCode == 0), result.CombinedOutput);
            return result;
        }
    }

    // Keep source-fixture type declarations after the tests so the source-based lane inventory
    // does not mistake a class inside a raw string for the containing xUnit class.
    private const string DebugDrift = """
        namespace Sunder.Sdk.Stacks;
        internal sealed partial class StackContributorRpcServiceAdapter
        {
        #if DEBUG
            [Sunder.Sdk.Services.SunderMethod(MethodId = "debug-only")]
            public ValueTask<StackRpcEmpty> DebugOnlyAsync() => ValueTask.FromResult(StackRpcEmpty.Instance);
        #endif
        }
        """;

    private static string CopyBuildProjects(CoreTree tree)
    {
        var repository = FindRepositoryRoot();
        foreach (var directory in new[] { "packages/Sunder.Sdk.Stacks", "packages/Sunder.Sdk", "packages/Sunder.Package.Build",
                     "libraries/Sunder.Package.Format", "libraries/Sunder.Registry.Contracts", "tools/Sunder.ExecutionRuntime.Build" })
            Copy(Path.Combine(repository, "languages/csharp/dotnet", directory), Path.Combine(tree.Root, "languages/csharp/dotnet", directory));
        foreach (var file in new[] { "Directory.Build.props", "Directory.Packages.props" })
            File.Copy(Path.Combine(repository, "languages/csharp/dotnet", file), Path.Combine(tree.Root, "languages/csharp/dotnet", file), true);
        File.Copy(Path.Combine(repository, "global.json"), Path.Combine(tree.Root, "global.json"));
        return Path.Combine(tree.Sources, "Sunder.Sdk.Stacks.csproj");
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source).Where(path => Path.GetFileName(path) is not ("bin" or "obj")))
            Copy(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, Path.GetDirectoryName(sourcePath)! })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate Core source.");
    }

    private sealed class CoreTree : IDisposable
    {
        internal TemporaryDirectoryScope Temp { get; } = TemporaryDirectoryScope.Create(nameof(CoreStackTransportGenerationTests));
        internal string Root { get; }
        internal string Sources => Path.Combine(Root, ProjectRelativePath);
        internal string Baseline => Path.Combine(Root, "eng/platform-contracts/sunder-contracts.owned.json");
        internal string Output(string configuration = "Debug") => Path.Combine(Sources, "obj", $"sunder-core-transport-{configuration}-net10.0");

        internal CoreTree()
        {
            Root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(Temp.CreateDirectory("Core with spaces"));
            Directory.CreateDirectory(Sources);
            Directory.CreateDirectory(Path.GetDirectoryName(Baseline)!);
            File.WriteAllText(Path.Combine(Root, "Sunder.Core.slnx"), "<Solution />");
            foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures/PlatformSources"), "*.cs"))
                File.Copy(path, Path.Combine(Sources, Path.GetFileName(path)));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/PlatformCatalog/sunder-contracts.owned.json"), Baseline);
        }

        internal CoreStackTransportRequestFile Request(string configuration = "Debug")
        {
            var writer = Writer(configuration);
            Assert.True(writer.Execute(), string.Join(Environment.NewLine, ((WriterBuildEngine)writer.BuildEngine).Errors));
            return new((string)RequestWriter.Value.GetProperty("RequestPath")!.GetValue(writer)!,
                (string)RequestWriter.Value.GetProperty("RequestSha256")!.GetValue(writer)!,
                (string)RequestWriter.Value.GetProperty("RequestStamp")!.GetValue(writer)!);
        }

        internal Microsoft.Build.Utilities.Task Writer(string configuration = "Debug", string? path = null)
        {
            Directory.CreateDirectory(Path.Combine(Sources, "obj"));
            path ??= Path.Combine(Sources, "obj", $".sunder-core-transport-{Guid.NewGuid():N}.request");
            var usings = Path.Combine(Sources, "obj", "GlobalUsings.g.cs");
            if (!File.Exists(usings)) File.WriteAllText(usings, "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;");
            var writer = (Microsoft.Build.Utilities.Task)Activator.CreateInstance(RequestWriter.Value)!;
            writer.BuildEngine = new WriterBuildEngine();
            foreach (var (name, value) in new Dictionary<string, object>
            {
                ["ProjectDirectory"] = Sources, ["RequestPath"] = path, ["Configuration"] = configuration,
                ["IntermediateOutputDirectory"] = Path.Combine(Sources, "obj"),
                ["TargetFramework"] = "net10.0", ["Nullable"] = "enable", ["DefineConstants"] = configuration == "Debug" ? "NET10_0;DEBUG" : "NET10_0",
                ["Sources"] = Directory.GetFiles(Sources, "*.cs").Append(usings).Select(source => new TaskItem(source)).ToArray(),
                ["References"] = PlatformContractSourceOperations.ReferencePaths().Select(reference => new TaskItem(reference)).ToArray(),
            }) RequestWriter.Value.GetProperty(name)!.SetValue(writer, value);
            return writer;
        }

        internal string[] SourceSnapshot() => Directory.GetFiles(Sources, "*.cs").Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        internal string[] OutputSnapshot() => Directory.GetFiles(Path.Combine(Sources, "obj"), "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".request", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Sources, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => Temp.Dispose();
    }

    private static string Line(string name, string value) => name + "=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string DecodeLine(string line) => Encoding.UTF8.GetString(Convert.FromBase64String(line[(line.IndexOf('=') + 1)..]));
    private static void WriteLines(string path, IEnumerable<string> lines) => File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    private static CoreStackTransportRequestFile Proof(string path)
    {
        var stamp = EngineeringFileOperations.ObserveFile(path);
        return new(path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
            FormattableString.Invariant($"{stamp.Length}:{stamp.CreatedUtc.Ticks}:{stamp.WrittenUtc.Ticks}"));
    }

    private static void Link(string source, string destination, string kind)
    {
        if (kind == "symbolic") File.CreateSymbolicLink(destination, source);
        else Assert.True(OperatingSystem.IsWindows() ? CreateHardLinkWindows(destination, source, IntPtr.Zero)
            : CreateHardLinkUnix(source, destination) == 0, $"Hard link failed: {Marshal.GetLastPInvokeError()}");
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newPath, string existingPath, IntPtr securityAttributes);

    private static Type CompileRequestWriter()
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures/PlatformCatalog/SdkStacks.csproj"));
        var source = Assert.Single(project.Descendants("Code")).Value;
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = PlatformContractSourceOperations.ReferencePaths()
            .Append(Path.Combine(runtime, "System.Security.Cryptography.dll"))
            .Append(Path.Combine(runtime, "System.Runtime.InteropServices.dll"))
            .Append(Path.Combine(runtime, "System.Text.RegularExpressions.dll"))
            .Where(path => Path.GetFileName(path) != "Sunder.Sdk.dll")
            .Append(typeof(IBuildEngine).Assembly.Location).Append(typeof(Microsoft.Build.Utilities.Task).Assembly.Location);
        var compilation = CSharpCompilation.Create("CorePrivateRequestWriter",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14))],
            references.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Sunder.", StringComparison.Ordinal)
            || reference.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
        return assembly.GetType("_SunderWriteCoreStackTransportRequest")!;
    }

    private sealed class WriterBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "SdkStacks.csproj";
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => throw new NotSupportedException();
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? "");
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
    }
}
