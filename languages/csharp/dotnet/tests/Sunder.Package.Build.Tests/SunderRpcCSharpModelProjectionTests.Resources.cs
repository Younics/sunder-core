using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    private const string ReferenceContentSource = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        using Api = Sunder.Generated.Packages.Example_Shared_Services;
        [SunderService(SunderServiceExposure.Public), SunderRuntimeUse]
        public sealed partial class RunControlService { public ValueTask<bool> ReadyAsync() => ValueTask.FromResult(true); }
        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class ChatService
        {
            public Payload? LastPayload { get; private set; }
            public static TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ValueTask<Payload> EchoAsync(Payload payload) { LastPayload = payload; return ValueTask.FromResult(payload); }
            public async ValueTask<bool> WaitAsync(Payload payload, System.Threading.CancellationToken token)
            { LastPayload = payload; Entered.TrySetResult(); await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token); return false; }
        }
        public sealed record Payload(
            [property: System.Text.Json.Serialization.JsonIgnore, SunderServiceMember] SunderContent Content)
        {
            [System.Text.Json.Serialization.JsonIgnore, SunderServiceMember]
            public ServiceReference<Api.IRunControlService> Control { get; init; } = null!;
            [System.Text.Json.Serialization.JsonIgnore, SunderServiceMember]
            public string Authority { get; init; } = "";
            [System.Text.Json.Serialization.JsonIgnore]
            public string Secret { get; init; } = "local-default";
            [System.Text.Json.Serialization.JsonIgnore]
            internal System.Action? DispatchStarted { get; init; } = () => { };
        }
        """;

    [Fact]
    public async Task GeneratedVisitorsForwardNestedContentAndBindReferencesToReceiverLifetime()
    {
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.shared", "1.0.0",
            [new("source.cs", ReferenceContentSource)], References(), GenerateServiceInterfaces: true));
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        var sources = prepass.Sources.Append(new SunderRpcCSharpSourceFile("source.cs", ReferenceContentSource))
            .Append(new("authoring.g.cs", SunderRpcCSharpImplementationEmitter.Emit(prepass.Compilation.Contracts)))
            .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(prepass.Compilation.Contracts, "example.shared", "1.0.0", providerSchemasPresent: true))
            .Append(new("probe.cs", ReferenceContentProbe));
        var (load, probe) = LoadProjectionProbe(sources, "ResourceProbe");
        try
        {
            var create = probe.GetMethod("Create")!.CreateDelegate<Func<ISunderRpcClient, object>>();
            var invoke = probe.GetMethod("Invoke")!.CreateDelegate<Func<object, SunderRpcInvocationContext, JsonElement, Task<JsonElement>>>();
            var control = probe.GetMethod("CallControl")!.CreateDelegate<Func<object, Task<bool>>>();
            var content = probe.GetMethod("Content")!.CreateDelegate<Func<object, SunderContent>>();
            var dispose = probe.GetMethod("Dispose")!.CreateDelegate<Func<object, ValueTask>>();
            var wait = probe.GetMethod("Wait")!.CreateDelegate<Func<object, SunderRpcInvocationContext, JsonElement, Task<JsonElement>>>();
            var entered = probe.GetMethod("Entered")!.CreateDelegate<Func<Task>>();
            var persisted = probe.GetMethod("Persisted")!.CreateDelegate<Func<object, JsonElement>>();
            var local = probe.GetMethod("LocalState")!.CreateDelegate<Func<object, bool>>();
            var clientRoundTrip = probe.GetMethod("ClientRoundTrip")!.CreateDelegate<Func<object, Task<JsonElement>>>();
            var descriptor = prepass.Compilation.Contracts.Single(contract => contract.ContractKey == "run-control").Descriptor;
            var provider = new SunderRpcProviderSnapshot("example.shared", "1.0.0", "example.shared.run.control", descriptor.ContractId,
                descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, Guid.NewGuid(), 2, 3,
                new("control-endpoint"), 7, SunderRpcProviderState.Active);
            var rpc = new ResourceProjectionRpc(provider);
            var state = create(rpc);
            try
            {
                var host = new StackContributorSourceParityTests.ContentHost();
                var input = JsonSerializer.SerializeToElement(new
                {
                    content = host.Content,
                    authority = "per-call-authority",
                    control = new
                    {
                        packageId = provider.PackageId, packageVersion = provider.PackageVersion, providerId = provider.ProviderId,
                        contractId = provider.ContractId, contractVersion = provider.ContractVersion, descriptorSha256 = provider.DescriptorSha256,
                        abiSha256 = provider.AbiSha256, activationId = provider.ActivationId, activationEpoch = provider.ActivationEpoch,
                        sessionGeneration = provider.SessionGeneration, endpoint = provider.Endpoint.Value,
                    },
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new SunderRpcJsonConverterFactory() } });
                var output = await invoke(state, host.Context(), input);
                Assert.Equal(input.GetProperty("control").GetRawText(), output.GetProperty("control").GetRawText());
                Assert.Equal("per-call-authority", output.GetProperty("authority").GetString());
                Assert.False(output.TryGetProperty("secret", out _));
                Assert.False(output.TryGetProperty("dispatchStarted", out _));
                Assert.Empty(persisted(state).EnumerateObject());
                Assert.True(local(state));
                Assert.Equal(host.Content, Assert.Single(host.Opened).Reference);
                Assert.Equal(new byte[] { 1, 2, 3 }, host.RegisteredBytes);
                Assert.Single(host.Registered); // Forward through receiving authority, rather than reuse the input token.
                await using var borrowed = content(state);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrowed.OpenReadAsync().AsTask());

                Assert.True(await control(state)); // The reference is not tied to the completed decoding invocation.
                Assert.Equal(1, rpc.Calls); Assert.Equal(1, rpc.ClosedScopes);
                Assert.Equal(1, rpc.Watches); // Catalog, reference codec and proxy share one receiver observer.
                var chatDescriptor = prepass.Compilation.Contracts.Single(contract => contract.ContractKey == "chat").Descriptor;
                rpc.CallerProvider = provider with { ContractId = chatDescriptor.ContractId, ContractVersion = chatDescriptor.Version,
                    DescriptorSha256 = chatDescriptor.Sha256, AbiSha256 = chatDescriptor.AbiSha256, ProviderId = "example.shared.chat", Endpoint = new("chat-endpoint") };
                rpc.ResponseContent = host.Content;
                rpc.Register = (_, stream, options, token) => host.Context().RegisterContentAsync(stream, options, token);
                rpc.Echo = async (request, _) => await invoke(state, host.Context(), request);
                var clientResult = await clientRoundTrip(state);
                Assert.Empty(clientResult.GetProperty("normal").EnumerateObject());
                Assert.Equal("caller-authority", clientResult.GetProperty("authority").GetString());
                Assert.Equal("local-default", clientResult.GetProperty("secret").GetString());
                Assert.Equal(input.GetProperty("control").GetRawText(), rpc.LastRequest!.Value.GetProperty("control").GetRawText());
                Assert.False(rpc.LastRequest.Value.TryGetProperty("secret", out _));
                var waiting = wait(state, host.Context(), input);
                await entered().WaitAsync(TimeSpan.FromSeconds(10));
                rpc.Retire();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(1, rpc.Calls); // Request participant retirement cancelled before any target invocation.
                Assert.Equal("rpc.stale-endpoint", (await Assert.ThrowsAsync<SunderRpcException>(() => control(state))).Error.Code);
                await dispose(state);
                await Assert.ThrowsAsync<SunderRpcException>(() => control(state));
                Assert.Equal(1, rpc.Calls);
            }
            finally { await dispose(state); }
        }
        finally { load.Unload(); }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public async Task GeneratedStreamRetainsBothOriginalCancellationTokensAfterEnumeration(bool owned, bool cancelCaller, bool earlyExit)
    {
        const string declaration = """
            using System.Collections.Generic;
            using System.Threading;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public interface ContentService { [SunderMethod(TimeoutSeconds = 2100)] IAsyncEnumerable<SunderContent> WatchAsync(CancellationToken token = default); }
            """;
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.shared", "1.0.0",
            [new("source.cs", declaration)], References(), GenerateServiceInterfaces: true));
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        var sources = owned ? prepass.Sources.Append(new SunderRpcCSharpSourceFile("source.cs", declaration))
            .Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(prepass.Compilation.Contracts, "example.shared", "1.0.0"))
            : SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(prepass.Compilation), "1.0.0");
        var (load, probe) = LoadProjectionProbe(sources.Append(new("probe.cs", $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Shared_Services{{(owned ? "" : ".CExample_Shared_Content")}};
            using Sunder.Generated.Packages.Example_Shared_Internal;
            public static class StreamProbe
            {
                public static object Create(ISunderRpcClient rpc)
                {
                    var owner = {{(owned ? "new CExample_Shared_ContentClient(rpc)" : "new PackageServices(rpc)")}};
                    return new State({{(owned ? "owner" : "owner.Example_Shared_Content.Content")}}, owner);
                }
                public static IAsyncEnumerable<SunderContent> Watch(object state, CancellationToken caller) => ((State)state).Client.WatchAsync(caller);
                public static ValueTask Dispose(object state) => ((State)state).Owner.DisposeAsync();
                private sealed record State(IContentService Client, IAsyncDisposable Owner);
            }
            """)), "StreamProbe");
        try
        {
            var create = probe.GetMethod("Create")!.CreateDelegate<Func<ISunderRpcClient, object>>();
            var watch = probe.GetMethod("Watch")!.CreateDelegate<Func<object, CancellationToken, IAsyncEnumerable<SunderContent>>>();
            var dispose = probe.GetMethod("Dispose")!.CreateDelegate<Func<object, ValueTask>>();
            var descriptor = Assert.Single(prepass.Compilation.Contracts).Descriptor;
            var provider = new SunderRpcProviderSnapshot("example.shared", "1.0.0", "example.shared.content", descriptor.ContractId,
                descriptor.Version, descriptor.Sha256, descriptor.AbiSha256, Guid.NewGuid(), 2, 3,
                new("content-endpoint"), 7, SunderRpcProviderState.Active);
            var rpc = new ResourceProjectionRpc(provider) { ResponseContent = new StackContributorSourceParityTests.ContentHost().Content };
            var state = create(rpc);
            try
            {
                using var caller = new CancellationTokenSource();
                using var enumeration = new CancellationTokenSource();
                var stream = watch(state, caller.Token);
                await using var iterator = stream.GetAsyncEnumerator(enumeration.Token);
                Assert.True(await iterator.MoveNextAsync());
                Assert.InRange(rpc.LastOptions!.DeadlineUtc!.Value, DateTimeOffset.UtcNow.AddSeconds(2080), DateTimeOffset.UtcNow.AddSeconds(2101));
                await using var retained = iterator.Current;
                if (!earlyExit) Assert.False(await iterator.MoveNextAsync());
                await iterator.DisposeAsync();
                Assert.Equal(0, rpc.ClosedScopes);
                await using (var readable = await retained.OpenReadAsync()) Assert.Equal(1, readable.ReadByte());

                (cancelCaller ? caller : enumeration).Cancel();
                await rpc.ScopeClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retained.OpenReadAsync().AsTask());
                Assert.Equal(1, rpc.ClosedScopes);
            }
            finally { await dispose(state); }
        }
        finally { load.Unload(); }
    }

    private static (AssemblyLoadContext Load, Type Probe) LoadProjectionProbe(IEnumerable<SunderRpcCSharpSourceFile> sources, string probeName, string? assemblyName = null)
    {
        var compilation = CSharpCompilation.Create(assemblyName ?? "ResourceProjection_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, new CSharpParseOptions(LanguageVersion.CSharp14), source.Path)),
            References().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var binary = new MemoryStream(); var emitted = compilation.Emit(binary);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics)); binary.Position = 0;
        var load = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        try { return (load, load.LoadFromStream(binary).GetType(probeName, true)!); }
        catch { load.Unload(); throw; }
    }

    private const string ReferenceContentProbe = """
        using System;
        using System.Text.Json;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        using Sunder.Generated.Packages.Example_Shared_Services;
        using Sunder.Generated.Packages.Example_Shared_Internal;
        public static class ResourceProbe
        {
            public static object Create(ISunderRpcClient rpc) => new Bindings(rpc);
            public static Task<JsonElement> Invoke(object state, SunderRpcInvocationContext context, JsonElement input)
            {
                var bindings = (Bindings)state;
                return bindings.Provider.CreateSunderRpcProviderRegistrationForServices(bindings.Rpc, bindings)
                    .Handler.InvokeUnaryAsync(context, "chat", "echo", input).AsTask();
            }
            public static async Task<bool> CallControl(object state) => await ((Bindings)state).Provider.LastPayload!.Control.Service.ReadyAsync();
            public static SunderContent Content(object state) => ((Bindings)state).Provider.LastPayload!.Content;
            public static JsonElement Persisted(object state) => JsonSerializer.SerializeToElement(((Bindings)state).Provider.LastPayload);
            public static bool LocalState(object state) => ((Bindings)state).Provider.LastPayload is { Secret: "local-default", DispatchStarted: not null };
            public static async Task<JsonElement> ClientRoundTrip(object state)
            {
                var binding = (Bindings)state;
                var reference = (await binding.Catalog.ListAsync()).Providers[0].Reference;
                await using var sending = SunderContent.FromBytes(new byte[] { 1, 2, 3 });
                await using var client = new CExample_Shared_ChatClient(binding.Rpc, binding);
                var response = await client.EchoAsync(new Payload(sending) { Control = reference, Authority = "caller-authority", Secret = "must-stay-local" });
                await using var receiving = response.Content;
                return JsonSerializer.SerializeToElement(new { normal = JsonSerializer.SerializeToElement(response), authority = response.Authority, secret = response.Secret });
            }
            public static Task<JsonElement> Wait(object state, SunderRpcInvocationContext context, JsonElement input)
            { var binding = (Bindings)state; return binding.Provider.CreateSunderRpcProviderRegistrationForServices(binding.Rpc, binding).Handler.InvokeUnaryAsync(context, "chat", "wait", input).AsTask(); }
            public static Task Entered() => ChatService.Entered.Task;
            public static async ValueTask Dispose(object state)
            { var binding = (Bindings)state; await ((IAsyncDisposable)binding.Catalog).DisposeAsync(); await binding.Lifetime.DisposeAsync(); }
            private sealed class Bindings : IServiceProvider
            {
                internal Bindings(ISunderRpcClient rpc) { Rpc = rpc; Lifetime = new(rpc); Catalog = new CExample_Shared_Run_ControlClientProviders(rpc, this); }
                internal ISunderRpcClient Rpc { get; }
                internal SunderRpcServiceBindingLifetime Lifetime { get; }
                internal ChatService Provider { get; } = new();
                internal CExample_Shared_Run_ControlClientProviders Catalog { get; }
                public object? GetService(Type type) => type == typeof(IServiceProviders<IRunControlService>) ? Catalog
                    : type == typeof(SunderRpcServiceBindingLifetime) ? Lifetime
                    : type == typeof(Func<SunderRpcProviderSnapshot, System.Threading.CancellationToken, IRunControlService>)
                        ? new Func<SunderRpcProviderSnapshot, System.Threading.CancellationToken, IRunControlService>(Catalog.BindExact) : null;
            }
        }
        """;

    private sealed class ResourceProjectionRpc(SunderRpcProviderSnapshot provider) : ISunderRpcClient
    {
        private SunderRpcProviderSnapshot _current = provider;
        private long _sequence;
        private readonly System.Threading.Channels.Channel<SunderRpcCatalogEvent> _events = System.Threading.Channels.Channel.CreateBounded<SunderRpcCatalogEvent>(4);
        private int _watches;
        internal int Watches => Volatile.Read(ref _watches);
        internal SunderRpcCallOptions? LastOptions { get; private set; }
        internal SunderRpcProviderSnapshot? CallerProvider { get; set; }
        internal JsonElement? LastRequest { get; private set; }
        internal Func<JsonElement, CancellationToken, ValueTask<JsonElement>>? Echo { get; set; }
        internal Func<SunderRpcEndpointReference, Stream, SunderRpcContentRegistrationOptions, CancellationToken, ValueTask<SunderRpcContentReference>>? Register { get; set; }
        internal void Retire()
        {
            _current = _current with { State = SunderRpcProviderState.Inactive, CatalogRevision = _current.CatalogRevision + 1 };
            _events.Writer.TryWrite(new(_current.CatalogRevision, ++_sequence, SunderRpcCatalogEventKind.Deactivated, _current));
        }
        internal int Calls { get; private set; }
        internal int ClosedScopes { get; private set; }
        internal TaskCompletionSource ScopeClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SunderRpcContentReference? ResponseContent { get; set; }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(endpoint == _current.Endpoint ? _current : endpoint == CallerProvider?.Endpoint ? CallerProvider : null);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(_current.CatalogRevision, _sequence,
                contractId == _current.ContractId ? [_current] : CallerProvider is { } caller && caller.ContractId == contractId ? [caller] : []));
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        { LastOptions = options; return ValueTask.FromResult<ISunderRpcCallScope>(new Scope(this)); }
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (serviceId == "chat" && methodId == "echo")
            { Assert.Equal(CallerProvider!.Endpoint, endpoint); LastRequest = request.Clone(); return Echo!(request, cancellationToken); }
            Assert.Equal(_current.Endpoint, endpoint); Assert.Equal("run-control", serviceId); Assert.Equal("ready", methodId);
            cancellationToken.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(JsonSerializer.SerializeToElement(true));
        }
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _watches); return _events.Reader.ReadAllAsync(cancellationToken); }
        public async IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.Equal(_current.Endpoint, endpoint); Assert.Equal("content", serviceId); Assert.Equal("watch", methodId);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
            yield return JsonSerializer.SerializeToElement(ResponseContent ?? throw new NotSupportedException(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new SunderRpcJsonConverterFactory() } });
        }
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Scope(ResourceProjectionRpc owner) : ISunderRpcCallScope
        {
            public DateTimeOffset DeadlineUtc => DateTimeOffset.MaxValue;
            public ValueTask DisposeAsync() { owner.ClosedScopes++; owner.ScopeClosed.TrySetResult(); return ValueTask.CompletedTask; }
            public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default) => owner.GetProviderAsync(endpoint, cancellationToken);
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => owner.DiscoverAsync(contractId, cancellationToken);
            public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => owner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => owner.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
                => owner.Register?.Invoke(endpoint, source, options, cancellationToken) ?? throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
            {
                Assert.Equal(owner.ResponseContent, reference); cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3]));
            }
        }
    }
}
