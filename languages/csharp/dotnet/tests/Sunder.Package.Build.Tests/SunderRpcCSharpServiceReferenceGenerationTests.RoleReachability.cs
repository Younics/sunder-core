using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpServiceReferenceGenerationTests
{
    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task PublicReferenceClosureFollowsRoleExecutionNotUnrelatedAppOrDiscoverOnlyInventory()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-reference-role-reachability");
        var root = scope.CreateDirectory("producer");
        var sourcePath = Path.Combine(root, "Producer.cs");
        var lockPath = Path.Combine(root, "sunder-contracts.lock.json");
        await File.WriteAllTextAsync(sourcePath, RoleSource);
        await File.WriteAllTextAsync(lockPath, "{\n  \"lockVersion\": 1,\n  \"packageArtifacts\": {},\n  \"packageImports\": {},\n  \"providerImports\": {},\n  \"platformImports\": {}\n}\n");
        var project = WriteProducerProject(root, sourcePath);
        var accepted = await RunProducer(root, project, "Accept"); // Owning scratch baseline, never a product baseline.
        Assert.True(accepted.Exit == 0, accepted.Output);
        var baselinePath = Path.Combine(root, "sunder-contracts.owned.json");
        var baseline = await File.ReadAllBytesAsync(baselinePath);
        var locked = await File.ReadAllBytesAsync(lockPath);
        var output = Path.Combine(root, "obj", "sunder-rpc");

        foreach (var defines in new[] { "", "WITH_APP", "WITH_APP%3BAPP_DISCOVER", "WITH_APP%3BAPP_SUBSCRIBE" })
        {
            var check = await RunProducer(root, project, "Check", defines);
            Assert.True(check.Exit == 0, check.Output);
            var generate = await RunProducer(root, project, "", defines);
            Assert.True(generate.Exit == 0, generate.Output);
            Assert.Equal(baseline, await File.ReadAllBytesAsync(baselinePath));
            Assert.Equal(locked, await File.ReadAllBytesAsync(lockPath));
            using var projection = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "contracts", "rpc-model-projection.v1.json")));
            Assert.Equal(2, projection.RootElement.GetProperty("contracts").GetArrayLength());
            if (defines.Length == 0) continue;
            using var app = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "app-authoring-receipt.v1.json")));
            Assert.Equal(2, app.RootElement.GetProperty("contracts").GetArrayLength()); // Complete inventory is retained.
            var uses = app.RootElement.GetProperty("uses").EnumerateArray().ToArray();
            Assert.DoesNotContain(uses, use => use.GetProperty("contractKey").GetString() == "target");
            if (defines == "WITH_APP") Assert.Empty(uses);
            else Assert.Equal(defines.EndsWith("APP_DISCOVER", StringComparison.Ordinal) ? new[] { "discover" } : new[] { "discover", "subscribe" },
                Assert.Single(uses).GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        }

        foreach (var defines in new[] { "WITH_APP%3BAPP_INVOKE", "WITH_APP%3BAPP_INVOKE%3BTARGET_INVOKE_ONLY" })
        {
            var denied = await RunProducer(root, project, "Check", defines);
            Assert.NotEqual(0, denied.Exit);
            Assert.Contains("rpc-authoring.service-reference-use-missing", denied.Output, StringComparison.Ordinal);
            Assert.Contains("Role 'app'", denied.Output, StringComparison.Ordinal);
        }
        var authorized = "WITH_APP%3BAPP_INVOKE%3BTARGET_DISCOVER";
        var allowed = await RunProducer(root, project, "Check", authorized);
        Assert.True(allowed.Exit == 0, allowed.Output);
        var materialized = await RunProducer(root, project, "", authorized);
        Assert.True(materialized.Exit == 0, materialized.Output);
        using (var app = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "app-authoring-receipt.v1.json"))))
        {
            var target = Assert.Single(app.RootElement.GetProperty("uses").EnumerateArray(), use => use.GetProperty("contractKey").GetString() == "target");
            Assert.Equal(["discover"], target.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        }
        Assert.Equal(baseline, await File.ReadAllBytesAsync(baselinePath));
        Assert.Equal(locked, await File.ReadAllBytesAsync(lockPath));
        Assert.Equal(RoleSource, await File.ReadAllTextAsync(sourcePath));

        // Even an unused App inventory must retain globally valid reference targets.
        var unused = await RunProducer(root, project, "", "WITH_APP"); Assert.True(unused.Exit == 0, unused.Output);
        var receipt = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "app-authoring-receipt.v1.json")))!;
        var holder = receipt["contracts"]!.AsArray().Single(contract => contract!["contractKey"]!.GetValue<string>() == "holder")!;
        var reference = holder["modelProjection"]!["serviceReferences"]!.AsObject().First().Value!;
        reference["contractId"] = "example.reference.producer.absent";
        var malformed = SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(receipt));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(malformed,
            receipt["lockSha256"]!.GetValue<string>(), "example.reference.producer", "1.0.0"));
    }

    private const string RoleSource = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Generated.Packages.Example_Reference_Producer_Services;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        [assembly: SunderPackage(Id = "example.reference.producer", Name = "Reference role producer")]
        [SunderService(SunderServiceExposure.Public), SunderRuntimeUse(Actions = ["discover", "invoke"])]
        #if TARGET_DISCOVER
        [SunderAppUse(Actions = ["discover"])]
        #elif TARGET_INVOKE_ONLY
        [SunderAppUse(Actions = ["invoke"])]
        #endif
        public interface TargetService { ValueTask<string> ReadAsync(CancellationToken token = default); }
        [SunderService(SunderServiceExposure.Public), SunderRuntimeUse(Actions = ["discover", "invoke", "subscribe"])]
        #if APP_INVOKE
        [SunderAppUse(Actions = ["discover", "invoke"])]
        #elif APP_SUBSCRIBE
        [SunderAppUse(Actions = ["discover", "subscribe"])]
        #elif APP_DISCOVER
        [SunderAppUse(Actions = ["discover"])]
        #endif
        public interface HolderService
        {
            ValueTask<string> UseAsync(ReferencePacket packet, CancellationToken token = default);
            IAsyncEnumerable<string> WatchAsync(CancellationToken token = default);
        }
        public sealed record ReferencePacket
        {
            public required ServiceReference<ITargetService> Target { get; init; }
            public SunderRpcOptional<ReferencePacket?> Next { get; init; }
        }
        [SunderServiceProvider(typeof(HolderService), ProviderId = "example.reference.holder")]
        public sealed partial class HolderProvider : HolderService
        {
            public ValueTask<string> UseAsync(ReferencePacket packet, CancellationToken token = default) => packet.Target.Service.ReadAsync(token);
            public async IAsyncEnumerable<string> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
            { await Task.Yield(); token.ThrowIfCancellationRequested(); yield return "event"; }
        }
        #if WITH_APP
        [SunderPackageCodeRole(SunderPackageCodeRole.App)] public sealed class AppState { public string Title => "Unrelated UI state"; }
        #endif
        #if APP_INVOKE || APP_SUBSCRIBE
        [SunderPackageView("consumer", "Consumer", ViewModel = typeof(AppConsumer))]
        public sealed class AppView : Avalonia.Controls.UserControl;
        public sealed class AppConsumer(IHolderService service)
        {
            #if APP_INVOKE
            public ValueTask<string> UseAsync(ReferencePacket packet) => service.UseAsync(packet);
            #else
            public IAsyncEnumerable<string> WatchAsync() => service.WatchAsync();
            #endif
        }
        #endif
        """;
}
