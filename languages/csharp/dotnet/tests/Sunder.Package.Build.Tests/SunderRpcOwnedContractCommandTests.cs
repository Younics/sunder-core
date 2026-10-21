using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;
using TaskItem = Microsoft.Build.Utilities.TaskItem;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcOwnedContractCommandTests : IDisposable
{
    private readonly string _root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
        Path.Combine(Path.GetTempPath(), "sunder-owned-command-tests", Guid.NewGuid().ToString("N")));
    private static readonly string[] References = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable."))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(static path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll"
            or "System.Collections.dll" or "System.Linq.dll" or "System.ObjectModel.dll" or "System.Console.dll"
            or "System.Threading.dll" or "System.Threading.Tasks.dll" or "System.Runtime.Extensions.dll"
            or "System.Text.Json.dll" or "System.Memory.dll" or "System.Runtime.Numerics.dll"
            or "System.ComponentModel.Primitives.dll")
        .Concat([typeof(SunderServiceAttribute).Assembly.Location, typeof(SunderWorker).Assembly.Location,
            typeof(IServiceProvider).Assembly.Location, typeof(IServiceCollection).Assembly.Location, typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public SunderRpcOwnedContractCommandTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SourcePath, Source);
        File.Copy(Fixture("specs/rpc/v1/authoring/conformance/valid/minimal-contract-lock.json"), LockPath);
    }

    private string SourcePath => Path.Combine(_root, "Program.cs");
    private string BaselinePath => Path.Combine(_root, SunderRpcAuthoringDocuments.OwnedContractsFileName);
    private string LockPath => Path.Combine(_root, SunderRpcAuthoringDocuments.ContractLockFileName);
    private string Intermediate => Path.Combine(_root, "obj");
    private string Generated => Path.Combine(Intermediate, "sunder-rpc");

    [Fact]
    public void Accept_BootstrapsFromAssemblyIdentity_ThenNoopsAndFreezesWithoutPublishingRpcTree()
    {
        var accept = Command("Accept");
        Assert.Equal(string.Empty, accept.PackageId);
        Success(accept);
        var accepted = Baseline();
        Assert.Equal("example.generated", accepted.PackageId);
        var current = Assert.Single(accepted.Contracts).Value.Current!;
        Assert.Equal("draft", current.State);
        Assert.Equal("1.0.0", current.Descriptor.Version);
        Assert.False(Directory.Exists(Generated));
        Assert.Empty(accept.FileWrites);
        Assert.Empty(accept.CompileItems);
        Assert.Empty(accept.AuthoringReceipts);
        Assert.Empty(accept.PackageProjectExports);

        var oldTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(BaselinePath, oldTime);
        Success(Command("Accept"));
        Success(Command("Check"));
        Assert.Equal(accepted.ExactBytes, File.ReadAllBytes(BaselinePath));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(BaselinePath));
        Success(Command("Freeze"));
        var frozen = Baseline();
        Assert.Equal("release-frozen", frozen.Contracts["greeting"].Current!.State);
        Assert.Equal("7.6.5", frozen.Contracts["greeting"].Current!.SnapshotJson.GetProperty("packageRelease").GetProperty("packageVersion").GetString());
        Assert.False(frozen.Contracts["greeting"].Current!.SnapshotJson.TryGetProperty("publication", out _));
        File.SetLastWriteTimeUtc(BaselinePath, oldTime);
        Success(Command("Freeze"));
        Success(Command("Accept"));
        Assert.Equal(frozen.ExactBytes, File.ReadAllBytes(BaselinePath));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(BaselinePath));
        Assert.False(Directory.Exists(Generated));
    }

    [Fact]
    public void CheckAndFreeze_AreExactAndReadOnly_AcceptInspectsChangedDraftSource()
    {
        Success(Command("Accept"));
        var oldTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var before = Baseline().ExactBytes;
        File.SetLastWriteTimeUtc(BaselinePath, oldTime);
        Success(Command());
        Assert.Equal(before, File.ReadAllBytes(BaselinePath));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(BaselinePath));
        var outputs = Snapshot(Generated);
        File.WriteAllText(SourcePath, AddMethod(Source));

        Failure(Command("Check"), "baseline");
        Failure(Command("Freeze"), "baseline");
        Assert.Equal(before, File.ReadAllBytes(BaselinePath));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(BaselinePath));
        Assert.Equal(outputs, Snapshot(Generated));
        Failure(Command("Accept"), "acknowledgement");
        Success(BreakingAcceptance("1.0.0")); // An unissued draft can retain its version after explicit source-Major review.
        Assert.NotEqual(before, File.ReadAllBytes(BaselinePath));
        Assert.Equal("1.0.0", Baseline().Contracts["greeting"].Current!.Descriptor.Version);
        Assert.Equal(outputs, Snapshot(Generated));
        Success(Command("Check"));
        Success(Command());
        Assert.NotEqual(outputs, Snapshot(Generated));
    }

    [Theory]
    [InlineData("Check")]
    [InlineData("Freeze")]
    public void Commands_RequireExistingBaseline(string operation)
    {
        Failure(Command(operation), "rpc-authoring.baseline-drift");
        Assert.False(File.Exists(BaselinePath));
        Assert.False(Directory.Exists(Generated));
    }

    [Fact]
    public void OfflineCommand_DoesNotInventPublicationEvidence()
    {
        Failure(Command("ImportPublication"), "cannot be manufactured offline");
        Assert.False(File.Exists(BaselinePath));
        Assert.False(Directory.Exists(Generated));
    }

    [Fact]
    public void Accept_DoesNotBootstrapOverEvidenceOfAnExistingGeneration()
    {
        Success(Command("Accept"));
        Success(Command());
        var before = Snapshot(Generated);
        File.Delete(BaselinePath);
        Failure(Command("Accept"), "baseline is required");
        Assert.False(File.Exists(BaselinePath));
        Assert.Equal(before, Snapshot(Generated));
    }

    [Theory]
    [InlineData("", "", "", false)]
    [InlineData("greeting", "1.0.0", "2.0.0", false)]
    [InlineData("greeting", "0.9.0", "2.0.0", true)]
    [InlineData("greeting", "1.0.0", "1.1.0", true)]
    [InlineData("unknown", "1.0.0", "2.0.0", true)]
    public void Accept_ImmutableBreakingChangeRequiresExactKeyOldNewAndAcknowledgement(string key, string current, string next, bool ack)
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var before = Baseline().ExactBytes;
        File.WriteAllText(SourcePath, Source.Replace("GreetAsync", "ReadAsync", StringComparison.Ordinal));
        var command = Command("Accept");
        command.ContractKey = key;
        command.ContractCurrentVersion = current;
        command.ContractNextVersion = next;
        command.AcknowledgeBreakingChange = ack;
        Assert.False(command.Execute());
        Assert.NotEmpty(((BuildEngine)command.BuildEngine).Errors);
        Assert.Equal(before, File.ReadAllBytes(BaselinePath));
        Assert.False(Directory.Exists(Generated));
    }

    [Fact]
    public void Accept_AllocatesReviewedVersionAndPreservesImmutableHistoryAndWireIdentity()
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var previous = Baseline().Contracts["greeting"].Current!;
        File.WriteAllText(SourcePath, Source.Replace("GreetAsync", "ReadAsync", StringComparison.Ordinal));
        var accept = BreakingAcceptance();
        Success(accept);
        var contract = Baseline().Contracts["greeting"];
        Assert.Equal("draft", contract.Current!.State);
        Assert.Equal("2.0.0", contract.Current.Descriptor.Version);
        Assert.Equal(previous.Descriptor.ContractId, contract.ContractId);
        Assert.True(JsonElement.DeepEquals(previous.SnapshotJson, Assert.Single(contract.History).SnapshotJson));
        Success(Command("Check"));
        var generation = Command();
        Success(generation);
        Assert.Equal("2.0.0", Assert.Single(generation.BindingSnapshots).GetMetadata("ContractVersion"));
        Assert.Equal("2.0.0", Assert.Single(generation.RpcProviders).GetMetadata("ContractVersion"));
    }

    [Fact]
    public void Rename_IsSeparateRemovalAndAddition_NotAContractIdentityAlias()
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var renamed = Source.Replace("GreetingService", "WelcomeService", StringComparison.Ordinal);
        File.WriteAllText(SourcePath, renamed.Replace("SunderServiceExposure.Public)",
            "SunderServiceExposure.Public, ContractId = \"example.generated.greeting\")", StringComparison.Ordinal));
        Failure(Command("Accept"), "aliased or renamed");

        File.WriteAllText(SourcePath, "using System.Threading.Tasks; using Sunder.Sdk.Packaging;\n"
            + "[assembly: SunderPackage(Id = \"example.generated\", Name = \"Commands\")]\n"
            + "public static class Program { public static Task Main() => Task.CompletedTask; }\n");
        Success(BreakingAcceptance());
        Assert.Null(Baseline().Contracts["greeting"].Current);
        Assert.Single(Baseline().Contracts["greeting"].History);
        File.WriteAllText(SourcePath, renamed);
        Success(Command("Accept"));
        Assert.Null(Baseline().Contracts["greeting"].Current);
        Assert.Equal("example.generated.welcome", Baseline().Contracts["welcome"].Current!.Descriptor.ContractId);
    }

    [Theory]
    [InlineData("Check")]
    [InlineData("Accept")]
    [InlineData("Freeze")]
    public void TamperedBaseline_FailsStrictReadBeforeAnyWrite(string operation)
    {
        Success(Command("Accept"));
        Success(Command());
        var output = Snapshot(Generated);
        var bytes = Baseline().ExactBytes;
        var hash = Baseline().Contracts["greeting"].Current!.Descriptor.Sha256;
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(hash, new string('0', 64), StringComparison.Ordinal));
        File.WriteAllBytes(BaselinePath, tampered);
        Assert.False(Command(operation).Execute());
        Assert.Equal(tampered, File.ReadAllBytes(BaselinePath));
        Assert.Equal(output, Snapshot(Generated));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheFailure_PreservesBaselineAndGeneratedOutputs(bool corrupt)
    {
        Success(Command("Accept"));
        var descriptor = EnableCachedImport();
        var cache = Command().ContractCacheRoot;
        var descriptorPath = SunderRpcContractCache.GetDescriptorPath(cache, descriptor.Sha256);
        Success(Command());
        var before = Baseline().ExactBytes;
        var outputs = Snapshot(Generated);
        File.WriteAllText(SourcePath, AddMethod(Source));
        if (corrupt) File.AppendAllText(descriptorPath, "tampered");
        else File.Delete(descriptorPath);
        var cacheBefore = Snapshot(cache);
        Failure(Command("Accept"), "contract-cache");
        Assert.Equal(before, File.ReadAllBytes(BaselinePath));
        Assert.Equal(outputs, Snapshot(Generated));
        Assert.Equal(cacheBefore, Snapshot(cache));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accept_FinalPassMustAgreeOnRegistrationsAndCompleteProviderProjection(bool audienceOnly)
    {
        Success(Command("Accept"));
        EnableCachedImport();
        Success(Command());
        var before = Baseline().ExactBytes;
        var output = Snapshot(Generated);
        var contractLock = JsonNode.Parse(File.ReadAllBytes(LockPath))!.AsObject();
        contractLock["providerImports"]!["minimalProvider"] = new JsonObject
        { ["packageId"] = "example.dependency", ["contractId"] = "example.rpc.v1.minimal" };
        File.WriteAllText(LockPath, contractLock.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
        var providerMetadata = audienceOnly
            ? "ProviderId = \"example.before\", Audience = ExampleAfterRpcError.Code == \"example.after\" ? SunderServiceAudience.Owner : SunderServiceAudience.Public"
            : "ProviderId = ExampleAfterRpcError.Code";
        var source = Path.Combine(_root, "Shadowing.cs");
        File.WriteAllText(source, $$"""
            using Legacy;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            namespace Sunder.Generated.Packages.Example_Generated_Internal.CExample_Generated_Imports { }
            namespace Legacy
            {
                public static class ExampleAfterRpcError { public const string Code = "example.before"; }
            }
            namespace Owned
            {
                [SunderService(SunderServiceExposure.Public)]
                [SunderServiceError("example.after")]
                public sealed partial class ImportsService
                {
                    [SunderMethod(Errors = new[] { "example.after" })]
                    public ValueTask<Reply> ReadAsync() => ValueTask.FromResult(new Reply { Value = "ok" });
                }
                public sealed record Reply { public required string Value { get; init; } }
            }
            namespace ImportedImplementation
            {
                using Sunder.Generated.Packages.Example_Generated_Internal;
                using Sunder.Generated.Packages.Example_Generated_Internal.CExample_Generated_Imports;
                using Sunder.Generated.Packages.Example_Dependency_Services;
                [SunderServiceProvider(typeof(IMinimalProviderService), {{providerMetadata}})]
                public sealed partial class LocalProvider : IMinimalProviderService
                {
                    public ValueTask PingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
                }
            }
            """);
        var command = Command("Accept");
        command.SourceFiles = [.. command.SourceFiles, new TaskItem(source)];
        Failure(command, "rpc-authoring.evidence-mismatch");
        Assert.Equal(before, File.ReadAllBytes(BaselinePath));
        Assert.Equal(output, Snapshot(Generated));
        Assert.Empty(command.FileWrites);
    }

    [Fact]
    public void Accept_UnchangedImmutablePrereleaseIsReused_ButChangedAllocationFailsClosed()
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var baseline = Baseline();
        var contract = baseline.Contracts["greeting"];
        var snapshot = contract.Current!;
        var descriptorJson = JsonNode.Parse(snapshot.Descriptor.GetCanonicalUtf8())!;
        descriptorJson["version"] = "1.0.0-rc.1";
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(descriptorJson.ToJsonString()));
        var snapshotJson = JsonNode.Parse(snapshot.SnapshotJson.GetRawText())!;
        snapshotJson["contractVersion"] = descriptor.Version;
        snapshotJson["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length;
        snapshotJson["canonicalDescriptor"] = Encoding.UTF8.GetString(descriptor.GetCanonicalUtf8());
        snapshotJson["descriptorSha256"] = descriptor.Sha256;
        snapshotJson["abiProjectionBytes"] = descriptor.GetCanonicalAbiProjectionUtf8().Length;
        snapshotJson["canonicalAbiProjection"] = Encoding.UTF8.GetString(descriptor.GetCanonicalAbiProjectionUtf8());
        snapshotJson["abiSha256"] = descriptor.AbiSha256;
        var contracts = baseline.Contracts.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        contracts["greeting"] = contract with
        {
            Current = snapshot with
            {
                Descriptor = descriptor, SnapshotJson = JsonSerializer.SerializeToElement(snapshotJson),
                ProjectionReview = SunderRpcAuthoringDocuments.CreateProjectionReview(descriptor,
                    snapshot.ProjectionReview!.ModelProjection, snapshot.ProjectionReview.MethodPolicies),
            },
        };
        Assert.Contains("immutable-evidence-mutated", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(baseline with { Contracts = contracts })).Message, StringComparison.Ordinal);
        // Construct a separate synthetic before-image of an already-issued prerelease.
        // This is not an allowed mutation of the frozen stable tuple above.
        var expected = SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(new(baseline.PackageId, contracts, [], string.Empty));
        File.WriteAllBytes(BaselinePath, expected);
        Success(Command("Accept"));
        Success(Command("Check"));
        Assert.Equal(expected, File.ReadAllBytes(BaselinePath));
        File.WriteAllText(SourcePath, AddMethod(Source));
        var changed = Command("Accept");
        changed.ContractKey = "greeting";
        changed.ContractCurrentVersion = "1.0.0-rc.1";
        changed.ContractNextVersion = "1.0.0";
        Failure(changed, "prerelease");
        Assert.Equal(expected, File.ReadAllBytes(BaselinePath));
    }

    [Theory]
    [InlineData("#error broken source\n")]
    [InlineData("public class NotValid {\n")]
    public void SourceFailure_PreservesBaselineAndGeneratedOutputs(string invalidSource)
    {
        Success(Command("Accept"));
        Success(Command());
        var before = Baseline().ExactBytes;
        var output = Snapshot(Generated);
        File.AppendAllText(SourcePath, "\n" + invalidSource);
        Assert.False(Command("Accept").Execute());
        Assert.Equal(before, File.ReadAllBytes(BaselinePath));
        Assert.Equal(output, Snapshot(Generated));
    }

    [Fact]
    public void AutoGenSourcesAreExcluded_AndProjectOutputKindAndDefinesAreUsed()
    {
        File.WriteAllText(SourcePath, "#if !COMMAND_TEST\n#error Missing project define\n#endif\n" + Source);
        var ignored = new TaskItem(Path.Combine(_root, "generated.cs"));
        ignored.SetMetadata("AutoGen", "true");
        File.WriteAllText(ignored.ItemSpec, "#error Do not compile AutoGen inputs\n");
        var command = Command("Accept");
        command.DefineConstants = "COMMAND_TEST";
        command.SourceFiles = [.. command.SourceFiles, ignored];
        Success(command);
        var badKind = Command("Check");
        badKind.DefineConstants = "COMMAND_TEST";
        badKind.OutputType = "Module";
        Failure(badKind, "output type");
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("parent")]
    [InlineData("root")]
    [InlineData("source")]
    public void Symlinks_AreRejectedWithoutChangingTheirTargets(string kind)
    {
        Success(Command("Accept"));
        var baseline = Baseline().ExactBytes;
        var command = Command("Accept");
        try
        {
            if (kind == "baseline")
            {
                var original = Path.Combine(_root, "original.json");
                File.Move(BaselinePath, original);
                File.CreateSymbolicLink(BaselinePath, original);
            }
            else if (kind == "source")
            {
                var original = Path.Combine(_root, "original.cs");
                File.Move(SourcePath, original);
                File.CreateSymbolicLink(SourcePath, original);
            }
            else
            {
                var link = Path.Combine(_root, "linked");
                Directory.CreateSymbolicLink(link, _root);
                if (kind == "root") command.ProjectDirectory = link;
                else command.OwnedContractsPath = Path.Combine(link, Path.GetFileName(BaselinePath));
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { return; }
        Failure(command, "symbolic link");
        Assert.Equal(baseline, File.ReadAllBytes(BaselinePath));
        Assert.False(Directory.Exists(Generated));
    }

    [Fact]
    public void BaselineOutsideProjectRoot_IsRejected()
    {
        var command = Command("Accept");
        command.OwnedContractsPath = Path.Combine(_root, "..", Guid.NewGuid().ToString("N") + ".json");
        Failure(command, "canonical project root");
        Assert.False(File.Exists(command.OwnedContractsPath));
    }

    [Fact]
    public void NormalCommitCannotRelabelOwnerEvenWithAnEmptyRoot()
    {
        File.WriteAllText(SourcePath, EmptySource);
        var before = SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(new("example.generated", new Dictionary<string, SunderRpcOwnedContractBaseline>(), [], ""));
        var after = SunderRpcAuthoringDocuments.WriteOwnedPackageBaseline(new("example.successor", new Dictionary<string, SunderRpcOwnedContractBaseline>(), [], ""));
        File.WriteAllBytes(BaselinePath, before);
        Assert.Throws<InvalidDataException>(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(_root, BaselinePath, before, after, new()));
        Assert.Equal(before, File.ReadAllBytes(BaselinePath));
        Assert.Empty(Directory.GetFiles(_root, ".sunder-contract-stage-*"));
    }

    [Fact]
    public void Transaction_LostBaselineLeaseNeverPublishes()
    {
        Success(Command("Accept"));
        var baseline = Baseline();
        var candidate = Command().PrepareOwnedContractCandidate(baseline)!;
        var proposal = SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(baseline, candidate.Contracts, "7.6.5", null);
        Assert.Throws<IOException>(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(_root, BaselinePath,
            baseline.ExactBytes, proposal, candidate.Inputs, () => Directory.Delete(BaselinePath + GeneratedOutputLock.LockSuffix, true)));
        Assert.Equal(baseline.ExactBytes, File.ReadAllBytes(BaselinePath));
        Assert.Empty(Directory.GetFiles(_root, ".sunder-contract-stage-*"));
    }

    [Fact]
    public void Transaction_RechecksExpectedBytesImmediatelyBeforeRename_AndNeverCleansUserStages()
    {
        Success(Command("Accept"));
        Success(Command());
        var baseline = Baseline();
        var candidate = Command().PrepareOwnedContractCandidate(baseline)!;
        var proposal = SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(baseline, candidate.Contracts, "7.6.5", null);
        var competing = SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(baseline, candidate.Contracts, "7.6.6", null);
        var userDirectory = BaselinePath + ".stage-user-owned";
        Directory.CreateDirectory(userDirectory);
        File.WriteAllText(Path.Combine(userDirectory, "precious.txt"), "user data");
        var output = Snapshot(Generated);
        Assert.Throws<IOException>(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(_root, BaselinePath,
            baseline.ExactBytes, proposal, candidate.Inputs, () => File.WriteAllBytes(BaselinePath, competing)));
        Assert.Equal(competing, File.ReadAllBytes(BaselinePath));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(userDirectory, "precious.txt")));
        Assert.Empty(Directory.GetFiles(_root, ".sunder-contract-stage-*"));
        Assert.Equal(output, Snapshot(Generated));
    }

    [Fact]
    public async Task Transaction_SerializesExistingWriters_AndRejectsTheStalePlan()
    {
        Success(Command("Accept"));
        var baseline = Baseline();
        var candidate = Command().PrepareOwnedContractCandidate(baseline)!;
        var first = SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(baseline, candidate.Contracts, "7.6.5", null);
        var second = SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(baseline, candidate.Contracts, "7.6.6", null);
        using var staged = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(_root, BaselinePath, baseline.ExactBytes, first, candidate.Inputs, () =>
        {
            staged.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        }));
        OutputLockContenderBarrier<Exception?>? contender = null;
        try
        {
            Assert.True(staged.Wait(TimeSpan.FromSeconds(30)));
            contender = OutputLockContenderBarrier<Exception?>.Start(BaselinePath,
                () => Record.Exception(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(_root, BaselinePath, baseline.ExactBytes, second, candidate.Inputs)),
                () => BaselinePath);
            await contender.WaitForAttemptAsync(TimeSpan.FromSeconds(30));
            await contender.AssertContendedAsync();
        }
        finally
        {
            release.Set();
            await writer;
            if (contender is not null)
            {
                await contender.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                contender.Dispose();
            }
        }
        Assert.True(await writer);
        Assert.IsType<IOException>(await contender!.Completion);
        Assert.Equal(first, File.ReadAllBytes(BaselinePath));
    }

    [Fact]
    public void Transaction_ValidatesTheStagedProposal_AndDoesNotRemoveAReplacedStage()
    {
        Success(Command("Accept"));
        var baseline = Baseline();
        var candidate = Command().PrepareOwnedContractCandidate(baseline)!;
        var proposal = SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(baseline, candidate.Contracts, "7.6.5", null);
        Assert.ThrowsAny<Exception>(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(_root, BaselinePath, baseline.ExactBytes, proposal, candidate.Inputs,
            () => File.WriteAllText(Assert.Single(Directory.GetFiles(_root, ".sunder-contract-stage-*")), "user replacement")));
        Assert.Equal(baseline.ExactBytes, File.ReadAllBytes(BaselinePath));
        Assert.Equal("user replacement", File.ReadAllText(Assert.Single(Directory.GetFiles(_root, ".sunder-contract-stage-*"))));
    }

    [Theory]
    [InlineData("Accept", "source")]
    [InlineData("Accept", "reference")]
    [InlineData("Accept", "lock")]
    [InlineData("Freeze", "source")]
    [InlineData("Freeze", "reference")]
    [InlineData("Freeze", "lock")]
    [InlineData("NoOp", "source")]
    [InlineData("NoOp", "reference")]
    [InlineData("NoOp", "lock")]
    public async Task Command_RechecksAllInputsAfterWaitingForBaselineLease(string operation, string input)
    {
        Success(Command("Accept"));
        Success(Command());
        if (operation == "NoOp") Success(Command("Freeze"));
        if (operation == "Accept") File.WriteAllText(SourcePath, AddMethod(Source));
        var command = operation == "Accept" ? BreakingAcceptance("1.0.0") : Command("Freeze");
        var (references, referencePath) = PrivateReferences();
        command.ReferencePaths = references;
        var path = input == "source" ? SourcePath : input == "lock" ? LockPath : referencePath;
        var before = File.ReadAllBytes(BaselinePath);
        var timestamp = File.GetLastWriteTimeUtc(BaselinePath);
        var output = Snapshot(Generated);
        using var lease = GeneratedOutputLock.Acquire([BaselinePath]);
        using var contender = OutputLockContenderBarrier<bool>.Start(BaselinePath, command.Execute, () => operation + ": " + path);
        try
        {
            await contender.WaitForAttemptAsync(TimeSpan.FromSeconds(30));
            await contender.AssertContendedAsync();
            ReplaceInput(path, input == "reference");
        }
        finally
        {
            lease.Dispose();
            await contender.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.False(await contender.Completion);
        Assert.Contains(((BuildEngine)command.BuildEngine).Errors, message => message.Contains("changed after it was read", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(BaselinePath));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(BaselinePath));
        Assert.Equal(output, Snapshot(Generated));
        Assert.Empty(command.FileWrites);
        Assert.Empty(Directory.GetFiles(_root, ".sunder-contract-stage-*"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("reference")]
    [InlineData("lock")]
    public void Transaction_RechecksInputsAgainImmediatelyBeforeRename(string input)
    {
        Success(Command("Accept"));
        var baseline = Baseline();
        var command = Command();
        var (references, referencePath) = PrivateReferences();
        command.ReferencePaths = references;
        var candidate = command.PrepareOwnedContractCandidate(baseline)!;
        var proposal = SunderRpcAuthoringDocuments.PlanOwnedBaselineFreeze(baseline, candidate.Contracts, "7.6.5", null);
        var path = input == "source" ? SourcePath : input == "lock" ? LockPath : referencePath;
        Assert.Throws<IOException>(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(_root, BaselinePath, baseline.ExactBytes, proposal,
            candidate.Inputs, () => ReplaceInput(path, input == "reference")));
        Assert.Equal(baseline.ExactBytes, File.ReadAllBytes(BaselinePath));
        Assert.Empty(Directory.GetFiles(_root, ".sunder-contract-stage-*"));
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("reference", false)]
    [InlineData("lock", false)]
    [InlineData("source", true)]
    [InlineData("source-add", true)]
    [InlineData("source-remove", true)]
    [InlineData("reference", true)]
    [InlineData("lock", true)]
    public async Task Pack_RechecksInputsAfterArchiveConstructionAndImmediatelyBeforePublication(string input, bool afterLease)
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var generation = Command();
        Success(generation);
        var dev = BuildDevPackage(generation);
        var pack = Pack(dev);
        Success(pack);
        var archive = File.ReadAllBytes(pack.PackageOutputPath);
        var baseline = File.ReadAllBytes(BaselinePath);
        var output = Snapshot(Generated);
        pack = Pack(dev);
        var (references, referencePath) = PrivateReferences();
        pack.ReferencePaths = references;
        var path = input == "source" ? SourcePath : input == "lock" ? LockPath : referencePath;
        if (afterLease)
        {
            pack.BeforeArchivePublication = () =>
            {
                if (input == "source-add") File.WriteAllText(Path.Combine(_root, "Added.cs"), "internal sealed class AddedSource;\n");
                else if (input == "source-remove") File.Delete(SourcePath);
                else ReplaceInput(path, input == "reference");
            };
            Failure(pack, "changed after it was read");
        }
        else
        {
            using var lease = GeneratedOutputLock.Acquire([BaselinePath]);
            using var contender = OutputLockContenderBarrier<bool>.Start(BaselinePath, pack.Execute, () => path);
            try
            {
                await contender.WaitForAttemptAsync(TimeSpan.FromSeconds(30));
                await contender.AssertContendedAsync();
                Assert.Single(Directory.GetFiles(Path.GetDirectoryName(pack.PackageOutputPath)!, "commands.sunderpkg.stage-*"));
                ReplaceInput(path, input == "reference");
            }
            finally
            {
                lease.Dispose();
                await contender.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            }
            Assert.False(await contender.Completion);
            Assert.Contains(((BuildEngine)pack.BuildEngine).Errors, message => message.Contains("changed after it was read", StringComparison.Ordinal));
        }
        Assert.Equal(archive, File.ReadAllBytes(pack.PackageOutputPath));
        Assert.Equal(baseline, File.ReadAllBytes(BaselinePath));
        Assert.Equal(output, Snapshot(Generated));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(pack.PackageOutputPath)!, "commands.sunderpkg.stage-*"));
    }

    [Fact]
    public void Pack_RejectsDiscardedDraftInOldLeaf_EvenWhenAcceptanceLeavesNoHistory()
    {
        Success(Command("Accept"));
        var generation = Command();
        Success(generation);
        var dev = BuildDevPackage(generation);
        File.WriteAllText(SourcePath, EmptySource);
        Success(BreakingAcceptance());
        Assert.Empty(Baseline().Contracts);
        Failure(Pack(dev), "contract bundles");
        Assert.False(File.Exists(Pack(dev).PackageOutputPath));
    }

    [Fact]
    public void Pack_ArtifactOnlyRpcInputFailsClosed()
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var generation = Command();
        Success(generation);
        var dev = BuildDevPackage(generation);
        var pack = Pack(dev);
        pack.ProjectDirectory = Path.Combine(_root, "artifact-only-project");
        Directory.CreateDirectory(pack.ProjectDirectory);
        pack.IntermediateOutputPath = Path.Combine(pack.ProjectDirectory, "obj");
        pack.SourceFiles = [];
        pack.ReferencePaths = [];
        Failure(pack, "Artifact-only RPC packing is disabled");
        Assert.False(File.Exists(pack.PackageOutputPath));
    }

    [Fact]
    public void Pack_DescriptorInputsCannotBypassImplementationSourceOrExpectedBaseline()
    {
        Success(Command("Accept"));
        var generation = Command();
        Success(generation);
        var dev = BuildDevPackage(generation);
        var descriptor = Baseline().Contracts["greeting"].Current!.Descriptor;
        var path = Path.Combine(_root, "authored.rpc.json");
        File.WriteAllBytes(path, descriptor.GetCanonicalUtf8());
        var bundle = new TaskItem(path);
        bundle.SetMetadata("ContractId", descriptor.ContractId);
        bundle.SetMetadata("Version", descriptor.Version);
        bundle.SetMetadata("DescriptorPath", "contracts/greeting.rpc.json");
        bundle.SetMetadata("Exposure", "public");
        Failure(CreatePack(), "draft");
        File.WriteAllText(SourcePath, Source.Replace("using Sunder.Sdk.Rpc;",
            "using Sunder.Sdk.Rpc;\nusing SvcAttribute = global::Sunder.Sdk.Services.@SunderServiceAttribute;", StringComparison.Ordinal)
            .Replace("[SunderService(", "[SvcAttribute(", StringComparison.Ordinal));
        Success(Command());
        File.Delete(BaselinePath);
        File.Delete(LockPath);
        var sourceOnly = CreatePack();
        sourceOnly.IntermediateOutputPath = Path.Combine(_root, "other-obj");
        Failure(sourceOnly, "deleting the baseline");
        var expectedBaseline = CreatePack();
        expectedBaseline.SourceFiles = [];
        expectedBaseline.IntermediateOutputPath = Path.Combine(_root, "other-obj");
        expectedBaseline.OwnedContractsPath = BaselinePath;
        Failure(expectedBaseline, "deleting the baseline");
        var receipt = CreatePack();
        receipt.SourceFiles = [];
        receipt.IntermediateOutputPath = Path.Combine(_root, "other-obj");
        receipt.AuthoringReceipts = generation.AuthoringReceipts;
        Failure(receipt, "deleting the baseline");

        PackSunderPackageTask CreatePack()
        {
            var pack = Pack(dev);
            pack.ContractBundles = [bundle];
            pack.RpcProviders = generation.RpcProviders;
            return pack;
        }
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("runtime-use")]
    [InlineData("bundle")]
    public async Task Pack_RejectsCompleteProjectionExtrasThatAreIndividuallyValid(string extra)
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var generation = Command();
        Success(generation);
        var dev = BuildDevPackage(generation);
        var descriptor = Baseline().Contracts["greeting"].Current!.Descriptor;
        RewriteManifest(dev, document =>
        {
            if (extra == "provider")
            {
                var provider = document["provides"]![0]!.DeepClone();
                provider["providerId"] = "example.generated.extra.provider";
                document["provides"]!.AsArray().Add(provider);
            }
            else if (extra == "runtime-use")
            {
                document["usesContracts"]!.AsArray().Add(JsonSerializer.SerializeToNode(new SunderPackageContractUseManifest
                {
                    Role = "runtime", ContractId = descriptor.ContractId, ContractVersion = descriptor.Version,
                    AbiSha256 = descriptor.AbiSha256, Required = true, Actions = ["discover", "invoke"],
                }));
            }
            else
            {
                var json = JsonNode.Parse(descriptor.GetCanonicalUtf8())!;
                json["contractId"] = "example.generated.extra";
                var extraDescriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
                File.WriteAllBytes(Path.Combine(dev, "payload/shared/contracts/extra.rpc.json"), extraDescriptor.GetCanonicalUtf8());
                var bundle = document["contractBundles"]![0]!.DeepClone();
                bundle["contractId"] = extraDescriptor.ContractId;
                bundle["descriptorPath"] = "contracts/extra.rpc.json";
                bundle["descriptorSha256"] = extraDescriptor.Sha256;
                bundle["abiSha256"] = extraDescriptor.AbiSha256;
                document["contractBundles"]!.AsArray().Add(bundle);
                var projectionPath = Path.Combine(dev, SunderPackageFormat.RpcModelProjectionPath);
                var projection = SunderRpcModelProjectionDocument.Parse(File.ReadAllBytes(projectionPath));
                File.WriteAllBytes(projectionPath, new SunderRpcModelProjectionDocument(1, projection.PackageId, projection.PackageVersion,
                    projection.Contracts.Append(new SunderRpcModelProjectionContract(extraDescriptor.ContractId, extraDescriptor.Version,
                        extraDescriptor.Sha256, extraDescriptor.AbiSha256, Assert.Single(projection.Contracts).ModelProjection)).ToArray()).ToCanonicalUtf8());
            }
        });
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(dev);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Failure(Pack(dev), "do not match");
    }

    [Theory]
    [InlineData("Package", "", true, "discover,invoke", true)]
    [InlineData("Package", "", false, "discover,invoke", false)]
    [InlineData("Package", "", null, "", false)]
    [InlineData("Public", "", null, "", false)]
    [InlineData("Public", "", false, "discover,invoke", true)]
    [InlineData("Public", "", true, "discover,invoke", false)]
    [InlineData("Public", "[SunderAppUse]", false, "discover,invoke", true)]
    [InlineData("Public", "[SunderAppUse]", true, "discover,invoke", false)]
    [InlineData("Public", "[SunderAppUse]", null, "", false)]
    [InlineData("Public", "[SunderAppUse(Actions = new[] { \"discover\" })]", false, "discover", true)]
    [InlineData("Public", "[SunderAppUse(Actions = new[] { \"discover\" })]", false, "discover,invoke", false)]
    public async Task Pack_AllowsOnlyTheExactAutomaticOwnedAppUses(string exposure, string attribute, bool? required, string actions, bool allowed)
    {
        File.WriteAllText(SourcePath, Source.Replace("Exposure.Public", "Exposure." + exposure, StringComparison.Ordinal)
            .Replace("[SunderService(", attribute + "\n[SunderService(", StringComparison.Ordinal));
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var generation = Command();
        Success(generation);
        var dev = BuildDevPackage(generation);
        var appRoot = Path.Combine(dev, "payload/app/linux-x64/web");
        Directory.CreateDirectory(appRoot);
        File.WriteAllText(Path.Combine(appRoot, "index.html"), "<!doctype html><title>App</title>");
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Assert.Single(generation.ContractBundles).ItemSpec));
        Assert.Equal(exposure == "Public", File.Exists(BaselinePath));
        RewriteManifest(dev, document =>
        {
            document["targets"]!.AsArray().Add(new JsonObject
            {
                ["role"] = "app", ["rid"] = "linux-x64", ["kind"] = "web", ["entryPoint"] = "web/index.html",
                ["requiredHostCapabilities"] = new JsonArray("rpc.v1"),
                ["views"] = new JsonArray(new JsonObject
                {
                    ["viewId"] = "example.generated.main", ["displayName"] = "App", ["route"] = "/", ["defaultPlacement"] = "middle", ["showInHotbar"] = true,
                }),
            });
            if (required is not null) document["usesContracts"]!.AsArray().Add(JsonSerializer.SerializeToNode(new SunderPackageContractUseManifest
            {
                Role = "app", ContractId = descriptor.ContractId, ContractVersion = descriptor.Version,
                AbiSha256 = descriptor.AbiSha256, Required = required.Value, Actions = actions.Split(','),
            }));
        });
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(dev);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var inspection = SunderPackagePublicApiInspection.ReadValidated(dev, validation.Manifest!, includeDescriptors: true);
        Assert.Equal(exposure == "Public" ? 1 : 0, inspection.ModelProjection.Contracts.Count);
        if (exposure == "Package")
        {
            Assert.Empty(inspection.Descriptors);
            Assert.DoesNotContain(descriptor.ContractId, Encoding.UTF8.GetString(inspection.ModelProjectionUtf8), StringComparison.Ordinal);
            Assert.DoesNotContain("GreetingRequest", Encoding.UTF8.GetString(inspection.ModelProjectionUtf8), StringComparison.Ordinal);
        }
        if (allowed) Success(Pack(dev));
        else Failure(Pack(dev), "App contract uses");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("required")]
    [InlineData("actions")]
    [InlineData("source-policy")]
    public async Task Pack_RuntimeConsumerUsesMustMatchCurrentSourcePolicy(string mutation)
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var frozen = File.ReadAllBytes(BaselinePath);
        var source = Source.Replace("[SunderService(", "[SunderRuntimeUse(Actions = new[] { \"discover\" })]\n[SunderService(", StringComparison.Ordinal);
        File.WriteAllText(SourcePath, source);
        Success(Command("Check"));
        var generation = Command();
        Success(generation);
        var use = Assert.Single(generation.RpcUses);
        Assert.Equal("runtime", use.GetMetadata("Role"));
        Assert.Equal("false", use.GetMetadata("Required"));
        Assert.Equal("discover", use.GetMetadata("Actions"));
        var dev = BuildDevPackage(generation);
        var pack = Pack(dev, generation.AuthoringReceipts);
        Success(pack);
        var archive = File.ReadAllBytes(pack.PackageOutputPath);
        if (mutation == "source-policy")
            File.WriteAllText(SourcePath, source.Replace("[SunderRuntimeUse(Actions = new[] { \"discover\" })]", "[SunderRuntimeUse(Required = true)]", StringComparison.Ordinal));
        else if (mutation != "none") RewriteManifest(dev, manifest =>
        {
            var uses = manifest["usesContracts"]!.AsArray();
            if (mutation == "missing") uses.Clear();
            if (mutation == "required") uses[0]!["required"] = true;
            if (mutation == "actions") uses[0]!["actions"] = new JsonArray("discover", "invoke");
        });
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(dev);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        if (mutation == "none") Success(Pack(dev));
        else Failure(Pack(dev), "runtime contract uses");
        Assert.Equal(frozen, File.ReadAllBytes(BaselinePath));
        Assert.Equal(archive, File.ReadAllBytes(pack.PackageOutputPath));
    }

    [Fact]
    public void FreezeAndPack_ReuseUnchangedIssuedContractInALaterPackageRelease()
    {
        Success(Command("Accept"));
        Success(Command("Freeze"));
        var frozen = File.ReadAllBytes(BaselinePath);
        var freeze = Command("Freeze");
        freeze.PackageVersion = "7.6.6";
        Success(freeze);
        Assert.Equal(frozen, File.ReadAllBytes(BaselinePath));
        var generation = Command();
        generation.PackageVersion = "7.6.6";
        Success(generation);
        Success(Pack(BuildDevPackage(generation), generation.AuthoringReceipts));
        Assert.Equal(frozen, File.ReadAllBytes(BaselinePath));
    }

    [Fact]
    public void Pack_PreBuiltSourceRequiresFrozenBaselineAndExactCurrentProjection()
    {
        Success(Command("Accept"));
        var generation = Command();
        Success(generation);
        var dev = BuildDevPackage(generation);
        var pack = Pack(dev, generation.AuthoringReceipts);
        Failure(pack, "draft");
        Assert.False(File.Exists(pack.PackageOutputPath));
        var generated = Snapshot(Generated);
        var leaf = Snapshot(dev);
        Success(Command("Freeze"));
        Success(Pack(dev, generation.AuthoringReceipts));
        Assert.Equal(generated, Snapshot(Generated));
        Assert.Equal(leaf, Snapshot(dev));
        var archive = File.ReadAllBytes(pack.PackageOutputPath);
        var frozen = File.ReadAllBytes(BaselinePath);
        File.WriteAllText(SourcePath, AddMethod(Source));
        Failure(Pack(dev), "baseline");
        Assert.Equal(archive, File.ReadAllBytes(pack.PackageOutputPath));
        Assert.Equal(frozen, File.ReadAllBytes(BaselinePath));

        File.WriteAllText(SourcePath, "#if COMMAND_TEST\n" + Source.Replace("using Sunder.Sdk.Rpc;", "using Sunder.Sdk.Rpc;\nusing Service = Sunder.Sdk.Services.SunderServiceAttribute;", StringComparison.Ordinal)
            .Replace("[SunderService(", "[Service(", StringComparison.Ordinal) + "\n#endif\n");
        File.Delete(BaselinePath);
        File.Delete(LockPath);
        var sourceOnly = Pack(dev);
        sourceOnly.DefineConstants = "COMMAND_TEST";
        Failure(sourceOnly, "deleting the baseline");
        var receiptOnly = Pack(dev, generation.AuthoringReceipts);
        receiptOnly.SourceFiles = [];
        Failure(receiptOnly, "deleting the baseline");
        Assert.Equal(archive, File.ReadAllBytes(pack.PackageOutputPath));
    }

    [Fact]
    public void PublicTargets_UseExplicitOperationsAndOrdinaryProjectCompilerInputs()
    {
        var document = XDocument.Load(Fixture("languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"));
        foreach (var operation in new[] { "Check", "Accept", "Freeze" })
        {
            var target = document.Root!.Elements("Target").Single(element => (string?)element.Attribute("Name") == operation + "SunderContracts");
            var task = Assert.Single(target.Elements("GenerateSunderRpcBindingsTask"));
            Assert.Equal(operation, (string?)task.Attribute("ContractOperation"));
            Assert.Equal("@(ReferencePath)", (string?)task.Attribute("ReferencePaths"));
            Assert.Equal("$(DefineConstants)", (string?)task.Attribute("DefineConstants"));
            Assert.Equal("$(OutputType)", (string?)task.Attribute("OutputType"));
            Assert.Equal("$(SunderContractCacheRoot)", (string?)task.Attribute("ContractCacheRoot"));
            Assert.DoesNotContain(target.Descendants(), element => element.Name.LocalName is "Exec" or "MSBuild");
        }
        var prepare = document.Root!.Elements("Target").Single(element => (string?)element.Attribute("Name") == "_PrepareSunderContractCommand");
        var sources = prepare.Descendants("_SunderRpcCommandSource").Single(element => element.Attribute("Include") is not null);
        Assert.Equal("@(Compile)", (string?)sources.Attribute("Include"));
        Assert.Equal("'%(Compile.AutoGen)' != 'true'", (string?)sources.Attribute("Condition"));
        var guard = document.Root!.Elements("Target").Single(element => (string?)element.Attribute("Name") == "_ValidateSunderContractCommandProject").ToString();
        Assert.Contains("SunderPackageInnerBuild", guard, StringComparison.Ordinal);
        Assert.Contains("_SunderRpcProjectImportEnabled", guard, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Root!.Elements("Target"), element => ((string?)element.Attribute("BeforeTargets")) is "Build" or "Rebuild"
            && element.Descendants("GenerateSunderRpcBindingsTask").Any(task => task.Attribute("ContractOperation") is not null));
    }

    private GenerateSunderRpcBindingsTask Command(string operation = "") => new()
    {
        BuildEngine = new BuildEngine(), ContractOperation = operation, ProjectDirectory = _root, IntermediateOutputPath = Intermediate,
        PackageVersion = "7.6.5", ProjectIdentity = "Commands", Configuration = "Debug", TargetFramework = "net10.0",
        OutputType = "Exe", NullableEnabled = true, ContractCacheRoot = Path.Combine(_root, "cache"),
        SourceFiles = [new TaskItem(SourcePath)], ReferencePaths = References.Select(static path => (ITaskItem)new TaskItem(path)).ToArray(),
    };

    private GenerateSunderRpcBindingsTask BreakingAcceptance(string nextVersion = "2.0.0")
    {
        var task = Command("Accept");
        task.ContractKey = "greeting";
        task.ContractCurrentVersion = "1.0.0";
        task.ContractNextVersion = nextVersion;
        task.AcknowledgeBreakingChange = true;
        return task;
    }

    private SunderRpcContractDescriptor EnableCachedImport()
    {
        File.Copy(Fixture("specs/rpc/v1/authoring/conformance/valid/package-api-contract-lock.json"), LockPath, overwrite: true);
        var cache = Command().ContractCacheRoot;
        Directory.CreateDirectory(cache);
        SunderRpcContractCacheStore.InitializeRoot(cache);
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Fixture("specs/rpc/v1/conformance/valid/minimal.rpc.json")));
        File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(cache, descriptor.Sha256), descriptor.GetCanonicalUtf8());
        File.Copy(Fixture("specs/rpc/v1/authoring/conformance/valid/cache-entry-receipt.json"), SunderRpcContractCache.GetReceiptPath(cache, descriptor.Sha256));
        foreach (var artifact in SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts.Values)
            ModelProjectionTestEvidence.Seed(cache, artifact);
        return descriptor;
    }

    private PackSunderPackageTask Pack(string dev, ITaskItem[]? receipts = null) => new()
    {
        BuildEngine = new BuildEngine(), ProjectDirectory = _root, DevPackagePath = dev, DevPackageDiscoveryRoot = Path.GetDirectoryName(dev)!,
        PackageOutputPath = Path.Combine(_root, "bin", "commands.sunderpkg"), SourceFiles = [new TaskItem(SourcePath)],
        ReferencePaths = References.Select(static path => (ITaskItem)new TaskItem(path)).ToArray(), AuthoringReceipts = receipts ?? [],
        ProjectIdentity = "Commands", Configuration = "Debug", TargetFramework = "net10.0", OutputType = "Exe", NullableEnabled = true,
        IntermediateOutputPath = Intermediate, ContractCacheRoot = Path.Combine(_root, "cache"),
    };

    private string BuildDevPackage(GenerateSunderRpcBindingsTask generation)
    {
        var targetDirectory = Path.Combine(_root, "bin", "linux-x64");
        Directory.CreateDirectory(targetDirectory);
        var assemblyPath = Path.Combine(targetDirectory, "Commands.dll");
        var compilation = CSharpCompilation.Create("Commands",
            generation.SourceFiles.Concat(generation.CompileItems).Select(item => CSharpSyntaxTree.ParseText(File.ReadAllText(item.ItemSpec),
                new CSharpParseOptions(LanguageVersion.CSharp14), item.ItemSpec)),
            References.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
        using (var stream = File.Create(assemblyPath))
        {
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        }
        var target = new TaskItem("runtime/linux-x64");
        target.SetMetadata("Role", "runtime");
        target.SetMetadata("Rid", "linux-x64");
        target.SetMetadata("Kind", "worker");
        var manifest = new GenerateSunderPackageManifestTask
        {
            BuildEngine = new BuildEngine(), TargetAssemblyPath = assemblyPath, EntryAssembly = Path.GetFileName(assemblyPath),
            ManifestOutputPath = Path.Combine(Intermediate, "sunder-package.json"), ProjectDirectory = _root,
            PackageVersion = generation.PackageVersion, TargetFramework = "net10.0", SdkPackageVersion = "1.1.0", ReferencePaths = generation.ReferencePaths,
            AuthoredAssemblyPaths = [new TaskItem(assemblyPath)], PackageTargets = [target], OutputType = "Exe", RuntimeIdentifier = "linux-x64",
            ContractBundles = generation.ContractBundles, BindingSnapshots = generation.BindingSnapshots,
            RpcProviders = generation.RpcProviders, UsesContracts = generation.RpcUses, AuthoringReceipts = generation.AuthoringReceipts,
        };
        Success(manifest);
        var runtimeConfig = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        File.WriteAllText(runtimeConfig, """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
        var deps = Path.ChangeExtension(assemblyPath, ".deps.json");
        var paths = new[] { assemblyPath, typeof(SunderWorker).Assembly.Location, typeof(SunderRpcContractDescriptor).Assembly.Location,
            typeof(IServiceCollection).Assembly.Location, typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location };
        var assets = new JsonObject();
        foreach (var path in paths) assets[Path.GetFileName(path)] = new JsonObject();
        File.WriteAllText(deps, new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = ".NETCoreApp,Version=v10.0/linux-x64" },
            ["targets"] = new JsonObject { [".NETCoreApp,Version=v10.0/linux-x64"] = new JsonObject
            { ["Commands/" + generation.PackageVersion] = new JsonObject { ["runtime"] = assets } } },
        }.ToJsonString());
        var dev = Path.Combine(targetDirectory, "sunder-dev");
        Success(new EmitSunderDevOutputTask
        {
            BuildEngine = new BuildEngine(), ManifestPath = manifest.ManifestOutputPath, DevPackagePath = dev,
            DiscoveryRoot = targetDirectory, TargetDirectory = targetDirectory, ContractFiles = manifest.ResolvedContractFiles,
            WorkerFiles = paths.Concat([runtimeConfig, deps]).Select(path =>
            {
                var item = new TaskItem(path);
                item.SetMetadata("RelativePath", Path.GetFileName(path));
                return (ITaskItem)item;
            }).ToArray(),
        });
        return dev;
    }

    private SunderRpcOwnedPackageBaseline Baseline() => SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(BaselinePath);

    private (ITaskItem[] Items, string PrivatePath) PrivateReferences()
    {
        var original = typeof(SunderWorker).Assembly.Location;
        var path = Path.Combine(_root, "PrivateWorkerReference.dll");
        File.Copy(original, path);
        return (References.Where(reference => !string.Equals(reference, original, StringComparison.OrdinalIgnoreCase))
            .Append(path).Select(static reference => (ITaskItem)new TaskItem(reference)).ToArray(), path);
    }

    private static void ReplaceInput(string path, bool binary)
    {
        var bytes = File.ReadAllBytes(path);
        if (binary) bytes[^1] ^= 1;
        else bytes = [.. bytes, (byte)'\n'];
        var replacement = path + ".replacement";
        File.WriteAllBytes(replacement, bytes);
        File.Move(replacement, path, overwrite: true);
    }

    private static void RewriteManifest(string dev, Action<JsonObject> edit)
    {
        var path = Path.Combine(dev, "manifest/sunder-package.json");
        var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        edit(document);
        File.WriteAllText(path, document.ToJsonString());
        PackageContentIndexer.Write(dev);
    }
    private static string AddMethod(string source) => source.Replace("    public ValueTask<GreetingResult> GreetAsync",
        "    public ValueTask<GreetingResult> EchoAsync(GreetingRequest request) => ValueTask.FromResult(new GreetingResult { Message = request.Name });\n"
        + "    public ValueTask<GreetingResult> GreetAsync", StringComparison.Ordinal);

    private static void Success(Microsoft.Build.Utilities.Task task)
        => Assert.True(task.Execute(), string.Join(Environment.NewLine, ((BuildEngine)task.BuildEngine).Errors));

    private static void Failure(Microsoft.Build.Utilities.Task task, string diagnostic)
    {
        Assert.False(task.Execute());
        Assert.Contains(((BuildEngine)task.BuildEngine).Errors, message => message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] Snapshot(string path) => Directory.Exists(path)
        ? Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => Path.GetRelativePath(path, file) + "|" + File.GetLastWriteTimeUtc(file).Ticks + "|"
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray()
        : [];

    private static string Fixture(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Sunder Core."), relativePath);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class BuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? string.Empty);
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
    }

    private const string EmptySource = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Packaging;
        [assembly: SunderPackage(Id = "example.generated", Name = "Commands")]
        public static class Program { public static Task Main() => Task.CompletedTask; }
        """;

    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        using Sunder.Sdk.Worker;
        [assembly: SunderPackage(Id = "example.generated", Name = "Commands")]
        namespace Example.Commands;
        public static class Program
        {
            public static Task Main() => SunderWorker.RunAsync(_ => new SunderWorkerOptions([new GreetingService().CreateSunderRpcProviderRegistration()]));
        }
        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class GreetingService
        {
            public ValueTask<GreetingResult> GreetAsync(GreetingRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken)
                => ValueTask.FromResult(new GreetingResult { Message = request.Name });
        }
        public sealed record GreetingRequest { public required string Name { get; init; } }
        public sealed record GreetingResult { public required string Message { get; init; } }
        """;
}
