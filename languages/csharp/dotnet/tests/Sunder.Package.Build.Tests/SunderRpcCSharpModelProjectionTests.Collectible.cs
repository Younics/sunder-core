using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    [Fact]
    public void OwnedContentAndReferenceCodecsCollectAfterReceiverDrain()
    {
        var weak = InvokeCollectibleResourceGraph();
        for (var attempt = 0; weak.IsAlive && attempt < 12; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(20);
        }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static WeakReference InvokeCollectibleResourceGraph()
    {
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.shared", "1.0.0",
            [new("source.cs", ReferenceContentSource)], References(), GenerateServiceInterfaces: true));
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        var sources = prepass.Sources.Append(new SunderRpcCSharpSourceFile("source.cs", ReferenceContentSource))
            .Append(new("authoring.g.cs", SunderRpcCSharpImplementationEmitter.Emit(prepass.Compilation.Contracts)))
            .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(prepass.Compilation.Contracts, "example.shared", "1.0.0", providerSchemasPresent: true))
            .Append(new("probe.cs", ReferenceContentProbe));
        var (load, probe) = LoadProjectionProbe(sources, "ResourceProbe");
        var weak = new WeakReference(load, trackResurrection: true);
        try
        {
            var create = probe.GetMethod("Create")!.CreateDelegate<Func<ISunderRpcClient, object>>();
            var invoke = probe.GetMethod("Invoke")!.CreateDelegate<Func<object, SunderRpcInvocationContext, JsonElement, Task<JsonElement>>>();
            var control = probe.GetMethod("CallControl")!.CreateDelegate<Func<object, Task<bool>>>();
            var content = probe.GetMethod("Content")!.CreateDelegate<Func<object, SunderContent>>();
            var dispose = probe.GetMethod("Dispose")!.CreateDelegate<Func<object, ValueTask>>();
            var descriptor = prepass.Compilation.Contracts.Single(contract => contract.ContractKey == "run-control").Descriptor;
            var provider = new SunderRpcProviderSnapshot("example.shared", "1.0.0", "example.shared.run.control", descriptor.ContractId,
                descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, Guid.NewGuid(), 2, 3,
                new("control-endpoint"), 7, SunderRpcProviderState.Active);
            var rpc = new ResourceProjectionRpc(provider);
            var state = create(rpc);
            try
            {
                var host = new StackContributorSourceParityTests.ContentHost();
                var input = JsonSerializer.SerializeToElement(new {
                    content = host.Content, authority = "per-call-authority",
                    control = new { packageId = provider.PackageId, packageVersion = provider.PackageVersion, providerId = provider.ProviderId,
                        contractId = provider.ContractId, contractVersion = provider.ContractVersion, descriptorSha256 = provider.DescriptorSha256,
                        abiSha256 = provider.AbiSha256, activationId = provider.ActivationId, activationEpoch = provider.ActivationEpoch,
                        sessionGeneration = provider.SessionGeneration, endpoint = provider.Endpoint.Value },
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new SunderRpcJsonConverterFactory() } });
                var output = invoke(state, host.Context(), input).GetAwaiter().GetResult();
                Assert.Equal(input.GetProperty("control").GetRawText(), output.GetProperty("control").GetRawText());
                Assert.Equal("per-call-authority", output.GetProperty("authority").GetString());
                Assert.Equal(new byte[] { 1, 2, 3 }, host.RegisteredBytes);
                Assert.Single(host.Registered);
                Assert.True(control(state).GetAwaiter().GetResult());
                content(state).DisposeAsync().AsTask().GetAwaiter().GetResult();
                Assert.Equal(1, rpc.Calls); Assert.Equal(1, rpc.ClosedScopes);
            }
            finally { dispose(state).AsTask().GetAwaiter().GetResult(); }
        }
        finally { load.Unload(); }
        return weak;
    }
}
