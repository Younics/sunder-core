using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OptionalReferences_FramedWorkerKeepsOptionalFallbackButCancelsMixedRequired(bool strict, bool midflight)
    {
        var prepass = OptionalReferencePrepass(OptionalWorkerSource);
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        var sources = prepass.Sources.Append(new SunderRpcCSharpSourceFile("source.cs", OptionalWorkerSource))
            .Append(new("authoring.cs", SunderRpcCSharpImplementationEmitter.Emit(prepass.Compilation.Contracts)))
            .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(prepass.Compilation.Contracts, "example.shared", "1.0.0", providerSchemasPresent: true))
            .Concat(WorkerHarnessSources())
            .Append(new("globals.cs", "global using System; global using System.IO; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks; global using Xunit;"))
            .Append(new("probe.cs", OptionalWorkerProbe));
        var (load, probe) = LoadProjectionProbe(sources, "OptionalWorkerProbe", "Sunder.Sdk.Worker.Tests");
        try { await probe.GetMethod("Run")!.CreateDelegate<Func<bool, bool, Task>>()(strict, midflight).WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { load.Unload(); }
    }

    private const string OptionalWorkerSource = """
        using System; using System.Threading; using System.Threading.Tasks;
        using Sunder.Sdk.Services; using Sunder.Sdk.Rpc;
        using Api = Sunder.Generated.Packages.Example_Shared_Services;
        [SunderService(SunderServiceExposure.Public), SunderRuntimeUse]
        public interface TargetService { ValueTask<bool> ReadyAsync(CancellationToken token = default); }
        public sealed record Payload {
            [SunderOptionalServiceReference] public required ServiceReference<Api.ITargetService>? Optional { get; init; }
            public required ServiceReference<Api.ITargetService>? Required { get; init; }
        }
        [SunderService(SunderServiceExposure.Public, ServiceId = "messages")]
        public sealed partial class SourceService {
            public int Entered { get; private set; }
            public bool CallerCancelled { get; private set; }
            public TaskCompletionSource FailureObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async ValueTask<bool> ExecuteAsync(Payload request, CancellationToken token = default) {
                Entered++;
                if (request.Optional is null) return true;
                try { return await request.Optional.Service.ReadyAsync(token); }
                catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.StaleEndpoint
                    && error.Error.DispatchState == SunderRpcDispatchState.NotDispatched && error.Error.RetrySafety == SunderRpcRetrySafety.Safe) {
                    FailureObserved.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, request.Optional.RetirementToken); }
                    catch (OperationCanceledException) when (request.Optional.RetirementToken.IsCancellationRequested) { }
                    CallerCancelled = token.IsCancellationRequested;
                    token.ThrowIfCancellationRequested();
                    return true;
                }
            }
        }
        """;

    private const string OptionalWorkerProbe = """
        using System.Text.Json;
        using Sunder.Sdk.Rpc; using Sunder.Sdk.Services; using Sunder.Sdk.Worker;
        using Sunder.Sdk.Worker.Tests.TestSupport;
        using Sunder.Generated.Packages.Example_Shared_Services;
        using Sunder.Generated.Packages.Example_Shared_Internal;
        public static class OptionalWorkerProbe {
            public static async Task Run(bool strict, bool midflight) {
                var source = new SourceService(); Services? services = null;
                await using var worker = new WorkerHarness(context => {
                    services = new Services(context.Rpc);
                    return new SunderWorkerOptions([WorkerHarness.Registration(source.CreateSunderRpcProviderRegistrationForServices(context.Rpc, services).Handler)]);
                });
                await worker.HandshakeAndActivateAsync();
                var descriptor = Sunder.Generated.Packages.Example_Shared_Internal.CExample_Shared_Target.__SunderContract.Descriptor;
                var activation = Guid.NewGuid();
                object Provider() => new { packageId = "test.target", packageVersion = "1.0.0", providerId = "test.target.provider",
                    contractId = descriptor.ContractId, contractVersion = descriptor.Version, descriptorSha256 = descriptor.Sha256,
                    abiSha256 = descriptor.AbiSha256, activationId = activation.ToString("N"), activationEpoch = 1, sessionGeneration = 7,
                    endpointReference = "rpc1_target", catalogRevision = 1, state = "active", faultCode = (string?)null };
                var locator = new { packageId = "test.target", packageVersion = "1.0.0", providerId = "test.target.provider",
                    contractId = descriptor.ContractId, contractVersion = descriptor.Version, descriptorSha256 = descriptor.Sha256,
                    abiSha256 = descriptor.AbiSha256, activationId = activation.ToString("D"), activationEpoch = 1, sessionGeneration = 7, endpoint = "rpc1_target" };
                await worker.SendInvocationAsync("source", "unary", "execute", new { optional = locator, required = strict ? locator : null });
                string? watch = null; var retireAfterClose = false; var retired = !midflight; var peerCalls = 0; var closed = 0;
                try {
                    while (true) {
                        using var frame = await worker.ReadAsync(); var message = frame.RootElement;
                        var type = message.GetProperty("type").GetString();
                        var id = message.TryGetProperty("id", out var identifier) ? identifier.GetString() : null;
                        switch (type) {
                            case "worker.get-provider":
                                await worker.SendAsync(new { type = "host.result", id, value = retired ? null : Provider() }); break;
                            case "worker.discover":
                                await worker.SendAsync(new { type = "host.result", id, value = new { revision = retired ? 2 : 1, sequence = retired ? 2 : 1,
                                    providers = retired ? Array.Empty<object>() : new[] { Provider() }, resetRequired = false } }); break;
                            case "worker.watch": watch = id; break;
                            case "worker.scope-open":
                                await worker.SendAsync(new { type = "host.result", id, value = new { scopeId = "target-scope", deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O") } }); break;
                            case "worker.invoke":
                                peerCalls++;
                                Assert.True(message.TryGetProperty("scopeId", out _));
                                await worker.SendAsync(new { type = "host.error", id, error = new { kind = "stale-endpoint", code = "rpc.endpoint.stale",
                                    message = "Target retired before dispatch.", dispatchState = "not-dispatched", retrySafety = "safe" } });
                                retireAfterClose = true; break;
                            case "worker.scope-close":
                                closed++;
                                await worker.SendAsync(new { type = "host.result", id, value = (object?)null });
                                if (retireAfterClose) {
                                    await source.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                                    Assert.NotNull(watch); retired = true; retireAfterClose = false;
                                    await worker.SendAsync(new { type = "host.event", id = watch, value = new { revision = 2, sequence = 2, kind = "removed", provider = Provider() } });
                                }
                                break;
                            case "worker.provider-fault-diagnostic":
                                Assert.True(strict);
                                await worker.SendAsync(new { type = "host.result", id, value = (object?)null }); break;
                            case "worker.cancel": break;
                            case "worker.result":
                                Assert.False(strict); Assert.True(message.GetProperty("value").GetBoolean());
                                Assert.Equal(1, source.Entered); Assert.False(source.CallerCancelled);
                                Assert.Equal(midflight ? 1 : 0, peerCalls); Assert.Equal(peerCalls, closed); return;
                            case "worker.error":
                                Assert.True(strict); Assert.Equal(midflight ? 1 : 0, source.Entered);
                                Assert.Equal(midflight ? 1 : 0, peerCalls); Assert.Equal(peerCalls, closed);
                                // Retirement propagation may race the body's catch;
                                // the generated result boundary must reject the fallback either way.
                                return;
                            default: throw new Exception(message.GetRawText());
                        }
                    }
                } finally { if (services is not null) await services.DisposeAsync(); }
            }
            private sealed class Services : IServiceProvider, IAsyncDisposable {
                private readonly ServiceClientLifetime lifetime;
                private readonly SunderRpcServiceBindingLifetime bindings;
                internal Services(ISunderRpcClient rpc) { bindings = new(rpc); lifetime = new(rpc, bindings); }
                public object? GetService(Type type) => type == typeof(SunderRpcServiceBindingLifetime) ? bindings
                    : type == typeof(Func<SunderRpcProviderSnapshot, CancellationToken, ITargetService>)
                        ? new Func<SunderRpcProviderSnapshot, CancellationToken, ITargetService>((provider, retirement) => new CExample_Shared_TargetClient(lifetime, provider, this, retirement)) : null;
                public async ValueTask DisposeAsync() { await lifetime.DisposeAsync(); await bindings.DisposeAsync(); }
            }
        }
        """;
}
