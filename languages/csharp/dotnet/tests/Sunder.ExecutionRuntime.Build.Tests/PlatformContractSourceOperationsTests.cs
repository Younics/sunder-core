using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Net;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.ExecutionRuntime.Build.Tests;

public sealed class PlatformContractSourceOperationsTests
{
    private const string Key = "stack-contributor-rpc-service-adapter";
    private const string DescriptorHash = "38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898";
    private const string AbiHash = "6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationImportVerifiesOriginalCatalogAndFencesSourceBeforeEvidenceCommit(bool mutateSource)
    {
        using var tree = new CoreTree(); await tree.Bootstrap(); await tree.Success("freeze");
        var before = File.ReadAllBytes(tree.Baseline);
        var descriptor = tree.ReadBaseline().Contracts[Key].Current!.Descriptor;
        var catalog = SunderPlatformContractCatalog.Verify(SunderPlatformContractCatalog.Create("1.1.0", [descriptor]));
        var catalogPath = Path.Combine(tree.Output, "retained.json"); File.WriteAllBytes(catalogPath, catalog.GetCanonicalUtf8());
        using var http = new HttpClient(new PublicationHandler(catalog));
        var operation = new PlatformContractSourceOperations(tree.Root);
        if (mutateSource)
        {
            await Assert.ThrowsAsync<IOException>(() => operation.ImportPublicationAsync(catalogPath, "https://registry.example/",
                _ => File.AppendAllText(Path.Combine(tree.Sources, "StackContributorRpcServiceAdapter.cs"), "\n// Changed during evidence commit.\n"), http));
            Assert.Equal(before, File.ReadAllBytes(tree.Baseline)); return;
        }
        Assert.True(await operation.ImportPublicationAsync(catalogPath, "https://registry.example/", registryClient: http));
        var confirmed = tree.ReadBaseline(); Assert.Equal("published", confirmed.Contracts[Key].Current!.State);
        Assert.Equal(catalog.ContentIdentity, confirmed.Contracts[Key].Current!.SnapshotJson.GetProperty("publication").GetProperty("catalogContentIdentity").GetString());
        var time = File.GetLastWriteTimeUtc(tree.Baseline);
        Assert.False(await new PlatformContractSourceOperations(tree.Root).ImportPublicationAsync(catalogPath, "https://registry.example/", registryClient: http));
        Assert.Equal(time, File.GetLastWriteTimeUtc(tree.Baseline));
        await tree.Success("check");
    }

    private sealed class PublicationHandler(SunderVerifiedPlatformContractCatalog catalog) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/catalog", StringComparison.Ordinal) ? catalog.GetCanonicalUtf8() : JsonSerializer.SerializeToUtf8Bytes(new
            {
                authorityId = "sunder.core", releaseVersion = catalog.ReleaseVersion, catalogSha256 = catalog.ContentIdentity, catalogBytes = catalog.GetCanonicalUtf8().Length,
                catalogDownloadUrl = "https://registry.example/api/v1/platform-contracts/sunder.core/releases/1.1.0/catalog",
                lifecycleState = "Supported", publishedAtUtc = "2026-09-17T12:34:56+00:00", contracts = catalog.Contracts.Select(value => new
                { contractId = value.ContractId, contractVersion = value.Version, descriptorBytes = value.GetCanonicalUtf8().Length, descriptorSha256 = value.Sha256, abiSha256 = value.AbiSha256 }),
            });
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new("application/json"); response.Headers.ETag = new('"' + catalog.ContentIdentity + '"');
            return Task.FromResult(response);
        }
    }
    private const string AdditionalServiceSource = """
        using Sunder.Sdk.Services;
        namespace Sunder.Core.Platform;
        [SunderService(SunderServiceExposure.Public, ContractId = "sunder.core.extra", ServiceId = "extra-platform")]
        public sealed partial class ExtraPlatform
        {
            public ValueTask<Reply> PingAsync() => ValueTask.FromResult(new Reply { Ready = true });
        }
        public sealed record Reply
        {
            public required bool Ready { get; init; }
        }
        """;
    private const string NestedPartialSource = """
        using Sunder.Sdk.Services;
        namespace Sunder.Sdk.Stacks;
        internal sealed partial class StackContributorRpcServiceAdapter
        {
            [SunderMethod(MethodId = "nested-probe")]
            public ValueTask<NestedProbeReply> NestedProbeAsync() => ValueTask.FromResult(new NestedProbeReply { Ready = true });
        }
        internal sealed record NestedProbeReply
        {
            public required bool Ready { get; init; }
        }
        """;

    [Fact]
    public async Task RealCli_BootstrapCheckFreezeExportAndLaterReuseAreSourceDerivedWithoutSigning()
    {
        using var tree = new CoreTree();
        var sources = tree.SourceBytes();
        Assert.False(Directory.Exists(Path.Combine(tree.Sources, "Contracts")));
        await tree.Bootstrap();
        var draftBytes = File.ReadAllBytes(tree.Baseline);
        var draft = tree.ReadBaseline().Contracts[Key].Current!;
        Assert.Equal("draft", draft.State);
        Assert.Equal("2.0.0", draft.Descriptor.Version);
        Assert.Equal(DescriptorHash, draft.Descriptor.Sha256);
        Assert.Equal(AbiHash, draft.Descriptor.AbiSha256);
        Assert.Equal(48, draft.Descriptor.Definitions.Count);
        Assert.Equal(6, Assert.Single(draft.Descriptor.Services).Methods.Count);
        Assert.False(draft.SnapshotJson.TryGetProperty("publication", out _));
        await tree.Success("check");
        var rejected = await tree.Run("export");
        Assert.Equal(1, rejected.Code);
        Assert.Contains("never draft", rejected.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(tree.Payload));
        Assert.Equal(draftBytes, File.ReadAllBytes(tree.Baseline));

        await tree.Success("freeze");
        var frozenBytes = File.ReadAllBytes(tree.Baseline);
        var frozen = tree.ReadBaseline().Contracts[Key].Current!;
        Assert.Equal("release-frozen", frozen.State);
        Assert.Equal("1.1.0", frozen.SnapshotJson.GetProperty("catalogRelease").GetProperty("releaseVersion").GetString());
        Assert.Equal(draft.Descriptor.GetCanonicalUtf8(), frozen.Descriptor.GetCanonicalUtf8());
        await tree.Success("export");
        var payload = File.ReadAllBytes(tree.Payload);
        Assert.Equal(SunderPlatformContractCatalog.Create("1.1.0", [frozen.Descriptor]), payload);
        using (var document = JsonDocument.Parse(payload))
        {
            Assert.False(document.RootElement.TryGetProperty("signature", out _));
            Assert.False(document.RootElement.TryGetProperty("payload", out _));
            Assert.Equal(1, document.RootElement.GetProperty("contractCount").GetInt32());
        }
        var existing = await tree.Run("export");
        Assert.Equal(1, existing.Code); // Even byte-identical output is not replaced.
        Assert.Equal(payload, File.ReadAllBytes(tree.Payload));
        await tree.Success("freeze", ["--catalog-release", "1.2.0"]);
        await tree.Success("export", ["--catalog-release", "1.2.0", "--output", tree.LaterPayload]);
        Assert.Equal(SunderPlatformContractCatalog.Create("1.2.0", [frozen.Descriptor]), File.ReadAllBytes(tree.LaterPayload));
        Assert.Equal(frozenBytes, File.ReadAllBytes(tree.Baseline));
        Assert.Equal(sources, tree.SourceBytes());
        Assert.Equal([tree.Baseline], Directory.GetFileSystemEntries(tree.BaselineParent));
        Assert.Equal(2, Directory.GetFiles(tree.Output).Length);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("freeze")]
    [InlineData("export")]
    public async Task MissingBaseline_RequiresExplicitAcceptanceWithoutWriting(string action)
    {
        using var tree = new CoreTree();
        var before = tree.AllCoreBytes();
        Assert.Equal(1, (await tree.Run(action)).Code);
        Assert.Equal(before, tree.AllCoreBytes());
        Assert.Empty(Directory.GetFileSystemEntries(tree.Output));
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(Key, null, null)]
    [InlineData(Key, null, "1.0.0")]
    [InlineData(Key, null, "3.0.0")]
    [InlineData("other-key", null, "2.0.0")]
    [InlineData(Key, "2.0.0", "2.0.0")]
    public async Task Bootstrap_DoesNotInferKeyDefaultVersionOrRenumberReviewedTuple(string? key, string? old, string? next)
    {
        using var tree = new CoreTree();
        var options = new List<string>();
        if (key is not null) options.AddRange(["--key", key]);
        if (old is not null) options.AddRange(["--current-version", old]);
        if (next is not null) options.AddRange(["--next-version", next]);
        Assert.Equal(1, (await tree.Run("accept", options.ToArray())).Code);
        Assert.False(File.Exists(tree.Baseline));
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_RejectsChangedActualSourceEvenWhenShippedHashConstantsRemain(bool abiChange)
    {
        using var tree = new CoreTree();
        tree.ChangeSource(abiChange);
        Assert.Contains(DescriptorHash, File.ReadAllText(Path.Combine(tree.Sources, "StackContributorRpc.cs")), StringComparison.Ordinal);
        var result = await tree.Run("accept", ["--key", Key, "--next-version", "2.0.0"]);
        Assert.Equal(1, result.Code);
        Assert.Contains("reviewed Stack 2.0.0 migration tuple", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecursiveSources_CompileActualNestedInputsIncludingUppercaseExtensions(bool uppercase)
    {
        using var tree = new CoreTree();
        var nested = Directory.CreateDirectory(Path.Combine(tree.Sources, "Nested", "Ordinary")).FullName;
        foreach (var file in Directory.GetFiles(tree.Sources, "*.cs", SearchOption.TopDirectoryOnly))
            File.Move(file, Path.Combine(nested, Path.GetFileNameWithoutExtension(file) + (uppercase ? ".CS" : ".cs")));
        await tree.Bootstrap();
        Assert.Equal(DescriptorHash, tree.ReadBaseline().Contracts[Key].Current!.Descriptor.Sha256);
        await tree.Success("check");
        await tree.Success("freeze");
        await tree.Success("export");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedSource_ChangesBootstrapAndCheckRatherThanBeingSilentlyOmitted(bool partial)
    {
        using var tree = new CoreTree();
        var nested = Directory.CreateDirectory(Path.Combine(tree.Sources, "Nested", "Contracts")).FullName;
        var path = Path.Combine(nested, "Additional.cs");
        var source = partial ? NestedPartialSource : AdditionalServiceSource;
        File.WriteAllText(path, source);
        var bootstrap = await tree.Run("accept", ["--key", Key, "--next-version", "2.0.0"]);
        Assert.Equal(1, bootstrap.Code);
        Assert.Contains("reviewed Stack 2.0.0 migration tuple", bootstrap.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
        File.Delete(path);
        await tree.Bootstrap();
        var baseline = File.ReadAllBytes(tree.Baseline);
        File.WriteAllText(path, source);
        var check = await tree.Run("check");
        Assert.Equal(1, check.Code);
        Assert.Contains("baseline-drift", check.Error, StringComparison.Ordinal);
        Assert.Equal(baseline, File.ReadAllBytes(tree.Baseline));
    }

    [Fact]
    public async Task RecursiveSourcePolicy_ExcludesRootOutputsAndDotDirectoriesAtAnyDepth()
    {
        using var tree = new CoreTree();
        foreach (var relative in new[] { "bin/Deeper", "OBJ/Deeper", ".hidden/Deeper", "Nested/.hidden/Deeper" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(tree.Sources, relative)).FullName;
            File.WriteAllText(Path.Combine(directory, "NotOrdinary.CS"), "#error Must not compile SDK-excluded source");
        }
        await tree.Bootstrap();
        await tree.Success("check");
    }

    [Fact]
    public async Task RecursiveSourcePolicy_HiddenAttributeAloneDoesNotExcludeOrdinarySource()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        using var tree = new CoreTree();
        await tree.Bootstrap();
        var before = File.ReadAllBytes(tree.Baseline);
        var directory = Directory.CreateDirectory(Path.Combine(tree.Sources, "HiddenAttribute")).FullName;
        File.WriteAllText(Path.Combine(directory, "ExtraPlatform.CS"), AdditionalServiceSource);
        File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        Assert.True((File.GetAttributes(directory) & FileAttributes.Hidden) != 0);
        var check = await tree.Run("check");
        Assert.Equal(1, check.Code);
        Assert.Contains("baseline-drift", check.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(tree.Baseline));
        await tree.Success("accept");
        Assert.Equal(2, tree.ReadBaseline().Contracts.Count);
    }

    [Fact]
    public async Task CheckAndPayload_WorkWithReadOnlyCoreInputsAndIgnoreNonSourceDescriptorOracles()
    {
        using var tree = new CoreTree();
        Directory.CreateDirectory(Path.Combine(tree.Sources, "Contracts"));
        File.WriteAllText(Path.Combine(tree.Sources, "Contracts", "sunder.stack.contributor.rpc.json"), "not a descriptor");
        await tree.Bootstrap();
        await tree.Success("freeze");
        var before = tree.AllCoreBytes();
        foreach (var file in Directory.GetFiles(tree.Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows())
        {
            foreach (var directory in Directory.GetDirectories(tree.Root, "*", SearchOption.AllDirectories).Append(tree.Root))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        await tree.Success("check");
        await tree.Success("export");
        Assert.Equal(before, tree.AllCoreBytes());
        Assert.Equal([tree.Baseline], Directory.GetFileSystemEntries(tree.BaselineParent));
    }

    [Fact]
    public async Task AcceptNoop_PreservesExactBytesTimestampAndForeignStages()
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        await tree.Success("freeze");
        File.SetLastWriteTimeUtc(tree.Baseline, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var before = File.ReadAllBytes(tree.Baseline);
        var timestamp = File.GetLastWriteTimeUtc(tree.Baseline);
        var foreign = Path.Combine(tree.BaselineParent, ".sunder-contract-stage-foreign");
        File.WriteAllText(foreign, "not this invocation's stage");
        var invoked = false;
        var result = await tree.Run("accept", beforePublication: _ => invoked = true);
        Assert.Equal(0, result.Code);
        Assert.False(invoked);
        Assert.Contains("unchanged", result.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(tree.Baseline));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(tree.Baseline));
        Assert.Equal("not this invocation's stage", File.ReadAllText(foreign));
    }

    [Theory]
    [InlineData("check")]
    [InlineData("freeze")]
    [InlineData("export")]
    public async Task SourceDrift_RejectsWholeSourceMismatchAndPreservesExistingPayload(string action)
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        await tree.Success("freeze");
        await tree.Success("export");
        tree.ChangeSource();
        var before = tree.AllCoreBytes();
        var payload = File.ReadAllBytes(tree.Payload);
        var result = await tree.Run(action);
        Assert.Equal(1, result.Code);
        Assert.Contains("baseline-drift", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, tree.AllCoreBytes());
        Assert.Equal(payload, File.ReadAllBytes(tree.Payload));
    }

    [Fact]
    public async Task Accept_RecompilesProposedVersionsPreservesFrozenHistoryAndRequiresBreakingPair()
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        tree.ChangeSource();
        await tree.Success("accept");
        Assert.Equal("2.0.0", tree.ReadBaseline().Contracts[Key].Current!.Descriptor.Version);
        await tree.Success("freeze");
        var previous = tree.ReadBaseline().Contracts[Key].Current!;
        tree.ChangeSource(abiChange: true);
        var bytes = File.ReadAllBytes(tree.Baseline);
        Assert.Equal(1, (await tree.Run("accept")).Code);
        Assert.Equal(1, (await tree.Run("accept", ["--key", Key, "--current-version", "1.0.0", "--next-version", "3.0.0", "--acknowledge-breaking-change", "true"])).Code);
        Assert.Equal(bytes, File.ReadAllBytes(tree.Baseline));
        await tree.Success("accept", ["--key", Key, "--current-version", "2.0.0", "--next-version", "3.0.0", "--acknowledge-breaking-change", "true"]);
        var accepted = tree.ReadBaseline().Contracts[Key];
        Assert.Equal("draft", accepted.Current!.State);
        Assert.Equal("3.0.0", accepted.Current.Descriptor.Version);
        Assert.True(JsonElement.DeepEquals(previous.SnapshotJson, Assert.Single(accepted.History).SnapshotJson));
        await tree.Success("check");
        Assert.Equal(1, (await tree.Run("freeze")).Code); // Replaced catalog release cannot be reused.
        await tree.Success("freeze", ["--catalog-release", "1.2.0"]);
        Assert.Equal(1, (await tree.Run("export")).Code);
        await tree.Success("export", ["--catalog-release", "1.2.0", "--output", tree.Payload]);
    }

    [Fact]
    public async Task PersistedPublishedFixture_IsNeverRewrittenOrManufacturedByLifecycleCommands()
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        await tree.Success("freeze");
        var baseline = tree.ReadBaseline();
        var current = baseline.Contracts[Key].Current!;
        var node = JsonNode.Parse(current.SnapshotJson.GetRawText())!.AsObject();
        node["state"] = "published";
        // Synthetic persisted evidence fixture, not a signer or a production authorization bypass.
        node["publication"] = JsonSerializer.SerializeToNode(new
        {
            registryOrigin = "https://registry.example.test/", authorityId = "sunder.core", catalogRelease = "1.1.0",
            catalogContentIdentity = new string('a', 64),
            publishedAtUtc = "2026-01-02T03:04:05Z", etag = $"\"{current.Descriptor.Sha256}\"",
        });
        var contracts = baseline.Contracts.ToDictionary();
        contracts[Key] = contracts[Key] with
        {
            Current = current with { State = "published", SnapshotJson = JsonSerializer.SerializeToElement(node) },
        };
        File.WriteAllBytes(tree.Baseline, SunderRpcAuthoringDocuments.WriteOwnedPlatformBaseline(baseline with { Contracts = contracts }));
        var published = tree.ReadBaseline().Contracts[Key].Current!;
        var before = File.ReadAllBytes(tree.Baseline);
        await tree.Success("check");
        await tree.Success("accept");
        await tree.Success("freeze", ["--catalog-release", "1.2.0"]);
        await tree.Success("export", ["--catalog-release", "1.2.0", "--output", tree.Payload]);
        Assert.Equal(before, File.ReadAllBytes(tree.Baseline));
        tree.ChangeSource();
        await tree.Success("accept");
        var changed = tree.ReadBaseline().Contracts[Key];
        Assert.Equal("2.0.1", changed.Current!.Descriptor.Version);
        Assert.Equal("draft", changed.Current.State);
        Assert.False(changed.Current.SnapshotJson.TryGetProperty("publication", out _));
        Assert.True(JsonElement.DeepEquals(published.SnapshotJson, Assert.Single(changed.History).SnapshotJson));
    }

    [Theory]
    [InlineData("ExtraPlatform.cs")]
    [InlineData("Generated/ExtraPlatform.cs")]
    [InlineData("Nested/Generated/ExtraPlatform.CS")]
    [InlineData("Nested/bin/ExtraPlatform.cs")]
    [InlineData("Nested/obj/ExtraPlatform.CS")]
    [InlineData(".hidden-service.cs")]
    [InlineData("Nested/.hidden-service.CS")]
    public async Task TargetedOperations_StillRequireTheCompleteSourceSetAndPayloadIncludesEveryFrozenContract(string relativeSource)
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        await tree.Success("freeze");
        var source = Path.Combine(tree.Sources, relativeSource);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, AdditionalServiceSource);
        var original = File.ReadAllBytes(tree.Baseline);
        Assert.Equal(1, (await tree.Run("check")).Code);
        Assert.Equal(1, (await tree.Run("accept", ["--key", Key])).Code);
        Assert.Equal(original, File.ReadAllBytes(tree.Baseline));
        await tree.Success("accept");
        Assert.Equal(2, tree.ReadBaseline().Contracts.Count);
        await tree.Success("freeze", ["--key", Key, "--catalog-release", "1.2.0"]);
        Assert.Equal("draft", tree.ReadBaseline().Contracts["extra-platform"].Current!.State);
        string[] payloadOptions = ["--catalog-release", "1.2.0", "--output", tree.Payload];
        Assert.Equal(1, (await tree.Run("export", payloadOptions)).Code);
        await tree.Success("freeze", ["--key", "extra-platform", "--catalog-release", "1.2.0"]);
        await tree.Success("export", payloadOptions);
        var descriptors = tree.ReadBaseline().Contracts.Values.Select(contract => contract.Current!.Descriptor).ToArray();
        Assert.Equal(SunderPlatformContractCatalog.Create("1.2.0", descriptors), File.ReadAllBytes(tree.Payload));
        tree.ChangeSource();
        Assert.Equal(1, (await tree.Run("freeze", ["--key", "extra-platform", "--catalog-release", "1.3.0"])).Code);
    }

    public static IEnumerable<object[]> PublicationMutations()
    {
        foreach (var action in new[] { "accept", "freeze", "export" })
        foreach (var mutation in new[] { "source-bytes", "source-added", "source-removed", "nested-source", "nested-bin-source", "nested-obj-source", "nested-link",
                     "baseline", "stage-failure", "stage-corrupt", "stage-link", "timeout", "cancel" })
            yield return [action, mutation];
    }

    [Theory]
    [MemberData(nameof(PublicationMutations))]
    public async Task Publication_RechecksExactInputsAndCleansOnlyOwnByteProvenStage(string action, string mutation)
    {
        if (OperatingSystem.IsWindows() && mutation.EndsWith("link", StringComparison.Ordinal)) return;
        using var tree = new CoreTree();
        await tree.Bootstrap();
        if (action is "accept" or "export") await tree.Success("freeze");
        if (action == "accept") tree.ChangeSource();
        var original = File.ReadAllBytes(tree.Baseline);
        var expected = original;
        var archive = Path.Combine(tree.Output, "previous.payload.json");
        File.WriteAllText(archive, "previous output must survive");
        using var cancellation = new CancellationTokenSource();
        string? stage = null;
        var result = await tree.Run(action, token: cancellation.Token, beforePublication: path =>
        {
            stage = path;
            switch (mutation)
            {
                case "source-bytes":
                    var file = Path.Combine(tree.Sources, "StackContributorModels.cs");
                    var stamp = File.GetLastWriteTimeUtc(file);
                    var bytes = File.ReadAllBytes(file);
                    bytes[^1] = bytes[^1] == (byte)'\n' ? (byte)' ' : (byte)'\n';
                    File.WriteAllBytes(file, bytes);
                    File.SetLastWriteTimeUtc(file, stamp);
                    break;
                case "source-added": File.WriteAllText(Path.Combine(tree.Sources, "Added.cs"), "// new ordinary input"); break;
                case "source-removed": File.Move(Path.Combine(tree.Sources, "StackPayloadHandles.cs"), Path.Combine(tree.Output, "removed.cs")); break;
                case "nested-source":
                case "nested-bin-source":
                case "nested-obj-source":
                    var folder = mutation switch { "nested-bin-source" => "bin", "nested-obj-source" => "obj", _ => "Generated" };
                    var nested = Directory.CreateDirectory(Path.Combine(tree.Sources, "Nested", folder)).FullName;
                    File.WriteAllText(Path.Combine(nested, "Partial.CS"), NestedPartialSource);
                    break;
                case "nested-link":
                    var parent = Directory.CreateDirectory(Path.Combine(tree.Sources, "Nested")).FullName;
                    Directory.CreateSymbolicLink(Path.Combine(parent, "linked"), tree.Temp.RootPath);
                    break;
                case "baseline":
                    expected = [.. original, (byte)'\n'];
                    File.WriteAllBytes(tree.Baseline, expected);
                    break;
                case "stage-failure": throw new IOException("simulated stage failure");
                case "timeout": throw new TimeoutException("simulated output lease timeout");
                case "stage-corrupt": File.WriteAllText(path, "foreign replacement"); break;
                case "stage-link":
                    File.Delete(path);
                    File.CreateSymbolicLink(path, archive);
                    break;
                case "cancel": cancellation.Cancel(); break;
            }
        });
        Assert.NotNull(stage);
        Assert.Equal(mutation == "cancel" ? 130 : 1, result.Code);
        Assert.Equal(expected, File.ReadAllBytes(tree.Baseline));
        Assert.Equal("previous output must survive", File.ReadAllText(archive));
        Assert.False(File.Exists(tree.Payload));
        if (mutation == "stage-corrupt") Assert.Equal("foreign replacement", File.ReadAllText(stage));
        else if (mutation == "stage-link") Assert.NotNull(new FileInfo(stage).LinkTarget);
        else Assert.False(File.Exists(stage));
        Assert.Empty(Directory.GetDirectories(tree.BaselineParent));
        Assert.Empty(Directory.GetDirectories(tree.Output));
    }

    [Fact]
    public async Task Payload_RechecksExactStageBytesAfterSecondInputVerificationNotJustItsStamp()
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        await tree.Success("freeze");
        await tree.Success("export");
        File.Move(tree.Payload, tree.LaterPayload);
        var archive = File.ReadAllBytes(tree.LaterPayload);
        var baseline = File.ReadAllBytes(tree.Baseline);
        var verifications = 0;
        string? stage = null;
        byte[]? changed = null;
        var operation = new PlatformContractSourceOperations(tree.Root, duringInputVerification: () =>
        {
            if (++verifications == 2) changed = ChangeStageWithoutChangingStamp(stage!);
        });
        var error = await Assert.ThrowsAsync<IOException>(() => operation.WriteCatalogAsync(
            "1.1.0", tree.Payload, path => stage = path));
        Assert.Contains("stage changed during input verification", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, verifications);
        Assert.NotNull(stage);
        Assert.Equal(changed, File.ReadAllBytes(stage));
        Assert.Equal(archive, File.ReadAllBytes(tree.LaterPayload));
        Assert.Equal(baseline, File.ReadAllBytes(tree.Baseline));
        Assert.False(File.Exists(tree.Payload));
    }

    [Fact]
    public async Task Payload_DestinationRacePreservesWinnerAndRejectsSourceTreeOutput()
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        await tree.Success("freeze");
        var before = tree.AllCoreBytes();
        var result = await tree.Run("export", beforePublication: _ => File.WriteAllText(tree.Payload, "winner"));
        Assert.Equal(1, result.Code);
        Assert.Equal("winner", File.ReadAllText(tree.Payload));
        Assert.Single(Directory.GetFileSystemEntries(tree.Output));
        var sourceOutput = Path.Combine(tree.Sources, "Injected.cs");
        Assert.Equal(1, (await tree.Run("export", ["--catalog-release", "1.1.0", "--output", sourceOutput])).Code);
        Assert.Equal(before, tree.AllCoreBytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payload_RejectsCaseAliasedLanguageTreesBeforeAnySourceWrite(bool rootAlias)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        using var tree = new CoreTree();
        await tree.Bootstrap();
        await tree.Success("freeze");
        var before = tree.AllCoreBytes();
        var stamp = Directory.GetLastWriteTimeUtc(tree.Sources);
        var root = rootAlias ? Path.Combine(Path.GetDirectoryName(tree.Root)!, Path.GetFileName(tree.Root).ToUpperInvariant()) : tree.Root;
        var destination = Path.Combine(root, "LANGUAGES", "csharp", "dotnet", "packages", "Sunder.Sdk.Stacks", "Injected.cs");
        var result = await tree.Run("export", ["--catalog-release", "1.1.0", "--output", destination]);
        Assert.Equal(1, result.Code);
        Assert.Contains("outside Core language source trees", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, tree.AllCoreBytes());
        Assert.Equal(stamp, Directory.GetLastWriteTimeUtc(tree.Sources));
        Assert.False(File.Exists(Path.Combine(tree.Sources, "Injected.cs")));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("dangling")]
    public async Task RecursiveSources_RejectNestedLinksWithoutFollowingThem(string kind)
    {
        if (OperatingSystem.IsWindows()) return;
        using var tree = new CoreTree();
        var nested = Directory.CreateDirectory(Path.Combine(tree.Sources, "Nested", "Contracts")).FullName;
        var target = tree.Temp.WriteAllText("outside.cs", "#error Must not read this linked source");
        var sentinel = File.ReadAllBytes(target);
        if (kind == "directory") Directory.CreateSymbolicLink(Path.Combine(nested, "cycle"), tree.Temp.RootPath);
        else File.CreateSymbolicLink(Path.Combine(nested, "Linked.CS"), kind == "file" ? target : tree.Temp.GetPath("absent.cs"));
        var result = await tree.Run("accept", ["--key", Key, "--next-version", "2.0.0"]);
        Assert.Equal(1, result.Code);
        Assert.Contains("symbolic link", result.Error, StringComparison.Ordinal);
        Assert.Equal(sentinel, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
    }

    [Theory]
    [InlineData("core-link")]
    [InlineData("source-link")]
    [InlineData("source-file-link")]
    [InlineData("baseline-link")]
    [InlineData("baseline-dangling-link")]
    [InlineData("parent-link")]
    [InlineData("parent-missing")]
    [InlineData("parent-file")]
    [InlineData("output-link")]
    [InlineData("output-parent-link")]
    [InlineData("output-parent-missing")]
    public async Task UnsafePaths_FailClosedWithoutCreatingParentsOrTouchingLinkTargets(string kind)
    {
        if (OperatingSystem.IsWindows() && kind.Contains("link", StringComparison.Ordinal)) return;
        using var tree = new CoreTree();
        await tree.Bootstrap();
        await tree.Success("freeze");
        var bytes = File.ReadAllBytes(tree.Baseline);
        var retained = Path.Combine(tree.Output, "retained-baseline.json");
        File.Copy(tree.Baseline, retained);
        var root = tree.Root;
        var destination = tree.Payload;
        var moved = Path.Combine(tree.Temp.RootPath, "moved");
        switch (kind)
        {
            case "core-link":
                root = Path.Combine(tree.Temp.RootPath, "core-link");
                Directory.CreateSymbolicLink(root, tree.Root);
                break;
            case "source-link": Directory.Move(tree.Sources, moved); Directory.CreateSymbolicLink(tree.Sources, moved); break;
            case "source-file-link":
                var source = Path.Combine(tree.Sources, "StackPayloadHandles.cs");
                File.Move(source, moved); File.CreateSymbolicLink(source, moved); break;
            case "baseline-link": File.Delete(tree.Baseline); File.CreateSymbolicLink(tree.Baseline, retained); break;
            case "baseline-dangling-link": File.Delete(tree.Baseline); File.CreateSymbolicLink(tree.Baseline, moved); break;
            case "parent-link": Directory.Move(tree.BaselineParent, moved); Directory.CreateSymbolicLink(tree.BaselineParent, moved); break;
            case "parent-missing": Directory.Move(tree.BaselineParent, moved); break;
            case "parent-file": Directory.Move(tree.BaselineParent, moved); File.WriteAllText(tree.BaselineParent, "parent sentinel"); break;
            case "output-link": File.CreateSymbolicLink(destination, retained); break;
            case "output-parent-link": Directory.CreateSymbolicLink(moved, tree.Output); destination = Path.Combine(moved, "payload.json"); break;
            case "output-parent-missing": destination = Path.Combine(moved, "payload.json"); break;
        }
        var result = await Invoke(["platform-contracts", "export", "--core-root", root, "--catalog-release", "1.1.0", "--output", destination]);
        Assert.Equal(1, result.Code);
        Assert.Equal(bytes, File.ReadAllBytes(retained));
        Assert.Empty(Directory.GetFiles(tree.Output, ".*.catalog-*"));
        if (kind == "parent-missing") Assert.False(Directory.Exists(tree.BaselineParent));
        if (kind == "parent-file") Assert.Equal("parent sentinel", File.ReadAllText(tree.BaselineParent));
        if (kind == "output-parent-missing") Assert.False(Directory.Exists(moved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceBudgets_BoundCountAndAggregateBytesBeforeCompilation(bool aggregateBytes)
    {
        using var tree = new CoreTree();
        if (aggregateBytes)
        {
            File.WriteAllText(Path.Combine(tree.Sources, "Large1.cs"), "//" + new string('a', 2_100_000));
            File.WriteAllText(Path.Combine(tree.Sources, "Large2.cs"), "//" + new string('b', 2_100_000));
        }
        else
        {
            for (var index = 0; index <= PlatformContractSourceOperations.MaximumSourceFiles; index++)
                File.WriteAllText(Path.Combine(tree.Sources, $"Extra{index}.cs"), "// empty");
        }
        Assert.Equal(1, (await tree.Run("accept", ["--key", Key, "--next-version", "2.0.0"])).Code);
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
    }

    [Theory]
    [InlineData("directories")]
    [InlineData("depth")]
    [InlineData("total-files")]
    public async Task RecursiveSources_BoundDirectoryCountDepthAndAllVisitedFiles(string limit)
    {
        using var tree = new CoreTree();
        var directory = tree.Sources;
        var count = limit switch
        {
            "directories" => PlatformContractSourceOperations.MaximumSourceDirectories,
            "depth" => PlatformContractSourceOperations.MaximumSourceDepth + 1,
            _ => PlatformContractSourceOperations.MaximumInputFiles + 1,
        };
        for (var index = 0; index < count; index++)
        {
            if (limit == "directories") Directory.CreateDirectory(Path.Combine(directory, $"Dir{index}"));
            else if (limit == "depth") directory = Directory.CreateDirectory(Path.Combine(directory, "d")).FullName;
            else File.WriteAllText(Path.Combine(directory, $"NotSource{index}.txt"), "bounded even though not C#");
        }
        var result = await tree.Run("accept", ["--key", Key, "--next-version", "2.0.0"]);
        Assert.Equal(1, result.Code);
        Assert.Contains("limit", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
    }

    [Fact]
    public async Task CompilationContext_UsesOnlyCuratedRuntimeAndSdkAndValidatesNonRpcSource()
    {
        var references = PlatformContractSourceOperations.ReferencePaths();
        Assert.Contains(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(typeof(SunderServiceAttribute).Assembly.Location), references);
        Assert.All(references, path => Assert.True(Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal)
            || Path.GetFileName(path) == "Sunder.Sdk.dll", path));
        Assert.DoesNotContain(references, path => Path.GetFileName(path) == "Sunder.Sdk.Stacks.dll");
        using var tree = new CoreTree();
        File.WriteAllText(Path.Combine(tree.Sources, "ContextProbe.cs"), """
            #if !NET10_0 || !NETCOREAPP || DEBUG
            #error Incorrect Core compilation context
            #endif
            public class ContextProbe
            {
                public static IEnumerable<string> Names() => Array.Empty<string>();
            }
            """);
        await tree.Bootstrap();
        File.WriteAllText(Path.Combine(tree.Sources, "Unrelated.cs"), "public class Unrelated { public void Fail() => Missing.GeneratedMethod(); }");
        var result = await tree.Run("check");
        Assert.Equal(1, result.Code);
        Assert.Contains("source compilation failed", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedTransaction_RechecksReferenceBytesAfterLeaseAndImmediatelyBeforeCommit(bool duringStage)
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        var expected = File.ReadAllBytes(tree.Baseline);
        await tree.Success("freeze");
        var proposal = File.ReadAllBytes(tree.Baseline);
        File.WriteAllBytes(tree.Baseline, expected);
        var reference = Path.Combine(tree.Output, "reference.dll");
        File.Copy(typeof(SunderServiceAttribute).Assembly.Location, reference);
        var inputs = new SunderRpcInputSnapshot();
        var bytes = inputs.Read(reference, 64 * 1024 * 1024, "copied trusted reference fixture");
        var stamp = File.GetLastWriteTimeUtc(reference);
        void Mutate()
        {
            bytes[^1] ^= 1;
            File.WriteAllBytes(reference, bytes);
            File.SetLastWriteTimeUtc(reference, stamp);
        }
        if (!duringStage) Mutate();
        Assert.Throws<IOException>(() => GenerateSunderRpcBindingsTask.CommitOwnedPlatformBaseline(
            tree.Root, expected, proposal, inputs.Verify, _ => Mutate()));
        Assert.Equal(expected, File.ReadAllBytes(tree.Baseline));
        Assert.Equal([tree.Baseline], Directory.GetFileSystemEntries(tree.BaselineParent));
        Assert.Throws<InvalidDataException>(() => GenerateSunderRpcBindingsTask.VerifyExpectedBaseline(tree.Root, tree.Baseline, expected));
        Assert.Throws<InvalidDataException>(() => GenerateSunderRpcBindingsTask.CommitOwnedBaseline(
            tree.Root, tree.Baseline, expected, proposal, new SunderRpcInputSnapshot()));
    }

    [Theory]
    [InlineData("same-stamp")]
    [InlineData("link")]
    [InlineData("cancel")]
    public async Task SharedTransaction_RechecksStageAfterSecondInputVerificationAndHonorsFinalCancellation(string mutation)
    {
        if (OperatingSystem.IsWindows() && mutation == "link") return;
        using var tree = new CoreTree();
        await tree.Bootstrap();
        var expected = File.ReadAllBytes(tree.Baseline);
        await tree.Success("freeze");
        var proposal = File.ReadAllBytes(tree.Baseline);
        File.WriteAllBytes(tree.Baseline, expected);
        var foreign = tree.Temp.WriteAllText("foreign", "preserve linked bytes");
        var inputs = new SunderRpcInputSnapshot();
        inputs.Record(tree.Baseline, expected);
        using var cancellation = new CancellationTokenSource();
        var verifications = 0;
        string? stage = null;
        byte[]? changed = null;
        var error = Record.Exception(() => GenerateSunderRpcBindingsTask.CommitOwnedPlatformBaseline(
            tree.Root, expected, proposal, () =>
            {
                inputs.Verify();
                if (++verifications != 2) return;
                if (mutation == "same-stamp") changed = ChangeStageWithoutChangingStamp(stage!);
                else if (mutation == "cancel") cancellation.Cancel();
                else
                {
                    File.Delete(stage!);
                    File.CreateSymbolicLink(stage!, foreign);
                }
            }, path => stage = path, cancellationToken: cancellation.Token));
        Assert.Equal(2, verifications);
        Assert.NotNull(stage);
        if (mutation == "cancel")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.False(File.Exists(stage));
        }
        else
        {
            Assert.IsAssignableFrom<IOException>(error);
            if (mutation == "same-stamp") Assert.Equal(changed, File.ReadAllBytes(stage));
            else Assert.NotNull(new FileInfo(stage).LinkTarget);
        }
        Assert.Equal(expected, File.ReadAllBytes(tree.Baseline));
        Assert.Equal("preserve linked bytes", File.ReadAllText(foreign));
    }

    [Fact]
    public async Task SharedTransaction_CancelsWhileWaitingForTheBaselineLease()
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        var expected = File.ReadAllBytes(tree.Baseline);
        await tree.Success("freeze");
        var proposal = File.ReadAllBytes(tree.Baseline);
        File.WriteAllBytes(tree.Baseline, expected);
        using var held = GeneratedOutputLock.Acquire([tree.Baseline]);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(() =>
        {
            entered.SetResult();
            return GenerateSunderRpcBindingsTask.CommitOwnedPlatformBaseline(
                tree.Root, expected, proposal, () => { }, cancellationToken: cancellation.Token);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(expected, File.ReadAllBytes(tree.Baseline));
        }
        finally
        {
            held.Dispose();
            _ = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task WithDialect_PreservesValidDescriptorJsonDeeperThan64WithinThe256DepthBound()
    {
        using var tree = new CoreTree();
        await tree.Bootstrap();
        var root = JsonNode.Parse(tree.ReadBaseline().Contracts[Key].Current!.Descriptor.GetCanonicalUtf8())!.AsObject();
        root.Remove("$schema");
        JsonNode nested = new JsonObject { ["type"] = "boolean" };
        for (var index = 0; index < 23; index++)
            nested = new JsonObject { ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "null" }, new JsonObject { ["type"] = "array", ["items"] = nested }) };
        root["$defs"]!["DeepSource"] = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["items"] = nested }, ["required"] = new JsonArray("items"),
        };
        var options = new JsonSerializerOptions { MaxDepth = 256 };
        var descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(root, options));
        Assert.ThrowsAny<JsonException>(() => JsonNode.Parse(descriptor.GetCanonicalUtf8()));
        var converted = PlatformContractSourceOperations.WithDialect(descriptor);
        root["$schema"] = SunderRpcProtocol.JsonSchemaDialect;
        var expected = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(root, options));
        Assert.Equal(expected.GetCanonicalUtf8(), converted.GetCanonicalUtf8());
        Assert.Equal(descriptor.AbiSha256, converted.AbiSha256);
    }

    [Fact]
    public async Task CancellationBeforePlanning_AndInputAddedBeforeLeaseNeverCommit()
    {
        using var tree = new CoreTree();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(130, (await tree.Run("accept", ["--key", Key, "--next-version", "2.0.0"], token: cancellation.Token)).Code);
        var operation = new PlatformContractSourceOperations(tree.Root);
        File.WriteAllText(Path.Combine(tree.Sources, "Added.cs"), "// after planning, before lease");
        Assert.Throws<IOException>(() => operation.Accept(Key, null, "2.0.0", false));
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        string[][] arguments =
        [
            [], ["publish"], ["check", "--key", Key], ["check", "--next-version", "2.0.0"],
            ["check", "--acknowledge-breaking-change", "false"], ["check", "--core-root", "a", "--core-root", "b"],
            ["accept", "--key"], ["accept", "--key", ""], ["accept", "--acknowledge-breaking-change", "yes"],
            ["accept", "--current-version", "2.0.0"], ["accept", "--next-version", "2.0.0"],
            ["accept", "--key", Key, "--current-version", "v2", "--next-version", "3.0.0"],
            ["accept", "--key", Key, "--next-version", "2.0.0", "--acknowledge-breaking-change", "true"],
            ["freeze", "--current-version", "2.0.0"], ["freeze"], ["freeze", "--catalog-release", "1.1.0-rc.1"],
            ["payload"], ["export"], ["export", "--key", Key], ["export", "--trust-policy", "untrusted.json"],
            ["export", "--signer-adapter", "untrusted"], ["export", "--overwrite", "true"],
            ["export", "--catalog-release", "1.1.0", "--output", "absent.json", "--minimum-trust-generation", "1"],
        ];
        foreach (var args in arguments) yield return [args];
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task RealCli_RejectsClosedActionOptionsBeforeFilesystemWork(string[] arguments)
    {
        using var tree = new CoreTree();
        var args = arguments.Length == 0 ? new[] { "platform-contracts" }
            : new[] { "platform-contracts", arguments[0], "--core-root", tree.Root }.Concat(arguments[1..]).ToArray();
        var result = await Invoke(args);
        Assert.Equal(2, result.Code);
        Assert.Empty(Directory.GetFileSystemEntries(tree.BaselineParent));
        Assert.Empty(Directory.GetFileSystemEntries(tree.Output));
    }

    [Fact]
    public async Task RealCli_RequiresCoreRootRatherThanInferringTheWorkingDirectory()
        => Assert.Equal(2, (await Invoke(["platform-contracts", "check"])).Code);

    [Theory]
    [InlineData("validate-lock")]
    [InlineData("validate-source")]
    [InlineData("validate-native-lane")]
    [InlineData("development-keys")]
    [InlineData("development-build")]
    [InlineData("development-publish")]
    [InlineData("build")]
    [InlineData("inspect")]
    [InlineData("validate-native-output")]
    [InlineData("validate-release-set")]
    public async Task RealCli_RejectsRetiredRuntimeCommandsBeforeAnyInputOrStateAccess(string command)
    {
        var result = await Invoke([command, "--state-root", "missing", "--lock", "missing"]);
        Assert.Equal(2, result.Code);
        Assert.StartsWith($"Unknown command '{command}'.", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain($"Sunder.ExecutionRuntime.Build {command} ", result.Error, StringComparison.Ordinal);
        Assert.Contains("platform-contracts check", result.Error, StringComparison.Ordinal);
        Assert.Contains("core-stack-transport generate", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreCheckTarget_IsExplicitFixedRootAndNeverABuildOrSigningHook()
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PlatformCatalog", "BuildTool.csproj"));
        var target = Assert.Single(project.Descendants("Target"), target => (string?)target.Attribute("Name") == "CheckSunderPlatformContracts");
        Assert.Equal("Build", (string?)target.Attribute("DependsOnTargets"));
        Assert.Null(target.Attribute("BeforeTargets"));
        Assert.Null(target.Attribute("AfterTargets"));
        var command = (string)Assert.Single(target.Elements("Exec")).Attribute("Command")!;
        Assert.Contains("platform-contracts check --core-root", command, StringComparison.Ordinal);
        Assert.Contains("$(MSBuildProjectDirectory)/../../../../..", command, StringComparison.Ordinal);
        Assert.DoesNotContain("sign", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sunder.Sdk.Stacks.csproj", project.ToString(), StringComparison.Ordinal);
    }

    private static async Task<(int Code, string Output, string Error)> Invoke(
        string[] args, CancellationToken token = default, Action<string>? beforePublication = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await ExecutionRuntimeBuildCli.RunAsync(args, output, error, token, beforePublication);
        return (code, output.ToString(), error.ToString());
    }

    private static byte[] ChangeStageWithoutChangingStamp(string path)
    {
        var stamp = EngineeringFileOperations.ObserveFile(path);
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 1;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(bytes[0]);
            stream.Flush(flushToDisk: true);
        }
        File.SetLastWriteTimeUtc(path, stamp.WrittenUtc);
        Assert.Equal(stamp, EngineeringFileOperations.ObserveFile(path));
        return bytes;
    }

    private sealed class CoreTree : IDisposable
    {
        public TemporaryDirectoryScope Temp { get; } = TemporaryDirectoryScope.Create(nameof(PlatformContractSourceOperationsTests));
        public string Root { get; }
        public string Sources { get; }
        public string BaselineParent { get; }
        public string Baseline => Path.Combine(BaselineParent, "sunder-contracts.owned.json");
        public string Output { get; }
        public string Payload => Path.Combine(Output, "payload.json");
        public string LaterPayload => Path.Combine(Output, "later.json");

        public CoreTree()
        {
            Root = Temp.CreateDirectory("Core with spaces");
            Sources = Path.Combine(Root, "languages", "csharp", "dotnet", "packages", "Sunder.Sdk.Stacks");
            BaselineParent = Path.Combine(Root, "eng", "platform-contracts");
            Output = Temp.CreateDirectory("catalog output");
            Directory.CreateDirectory(Sources);
            Directory.CreateDirectory(BaselineParent);
            File.WriteAllText(Path.Combine(Root, "Sunder.Core.slnx"), "<Solution />");
            var fixtures = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PlatformSources"), "*.cs");
            Assert.NotEmpty(fixtures);
            foreach (var source in fixtures) File.Copy(source, Path.Combine(Sources, Path.GetFileName(source)));
        }

        public Task Bootstrap() => Success("accept", ["--key", Key, "--next-version", "2.0.0"]);
        public SunderRpcOwnedPlatformBaseline ReadBaseline() => SunderRpcAuthoringDocuments.ReadOwnedPlatformBaseline(Baseline);
        public string[] SourceBytes() => Snapshot(Sources);
        public string[] AllCoreBytes() => Snapshot(Root);
        private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

        public void ChangeSource(bool abiChange = false)
        {
            var path = Path.Combine(Sources, abiChange ? "StackContributorRpcWireModels.cs" : "StackContributorRpcServiceAdapter.cs");
            var before = abiChange ? "MinLength = 1, MaxLength = 256" : "The contributor does not implement the requested optional Stack operation.";
            var after = abiChange ? "MinLength = 1, MaxLength = 255" : "The optional Stack operation is not supported.";
            var source = File.ReadAllText(path);
            Assert.Contains(before, source, StringComparison.Ordinal);
            File.WriteAllText(path, source.Replace(before, after, StringComparison.Ordinal));
        }

        public Task<(int Code, string Output, string Error)> Run(string action, string[]? options = null,
            CancellationToken token = default, Action<string>? beforePublication = null)
        {
            options ??= action switch
            {
                "freeze" => ["--catalog-release", "1.1.0"],
                "export" => ["--catalog-release", "1.1.0", "--output", Payload],
                _ => [],
            };
            return Invoke(["platform-contracts", action, "--core-root", Root, .. options], token, beforePublication);
        }

        public async Task Success(string action, string[]? options = null)
        {
            var result = await Run(action, options);
            Assert.True(result.Code == 0, $"{action}: exit {result.Code}; {result.Error}");
        }

        public void Dispose() => Temp.Dispose();
    }
}
