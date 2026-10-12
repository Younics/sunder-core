using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NaturalProviderContextFreeExplicitAndInheritedBindingsRemainValid(bool explicitImplementation)
    {
        var imports = SunderRpcCSharpImportPrepass.Prepare(WithProvider(_lock), _root);
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services;
            """ + (explicitImplementation ? """
            [SunderServiceProvider(typeof(IExampleService))]
            public sealed partial class Provider : IExampleService
            {
                public int Calls { get; private set; }
                ValueTask IExampleService.PingAsync(CancellationToken token) { Calls++; return ValueTask.CompletedTask; }
            }
            """ : """
            public class BaseProvider
            {
                public int Calls { get; private set; }
                public Task PingAsync() { Calls++; return Task.CompletedTask; }
            }
            [SunderServiceProvider(typeof(IExampleService))]
            public sealed partial class Provider : BaseProvider;
            """);
        var result = CompileNaturalProvider(imports, source);
        using var bytes = new MemoryStream(); var emission = result.SourceCompilation!.Emit(bytes);
        Assert.True(emission.Success, string.Join(Environment.NewLine, emission.Diagnostics)); bytes.Position = 0;
        var load = new AssemblyLoadContext("natural-context-free", isCollectible: true);
        try
        {
            var instance = Activator.CreateInstance(load.LoadFromStream(bytes).GetType("Provider", true)!)!;
            var registration = (SunderWorkerProviderRegistration)instance.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null)!;
            var output = await registration.Handler.InvokeUnaryAsync(null!, "example", "ping", JsonSerializer.SerializeToElement(new { }));
            Assert.Empty(output.EnumerateObject());
            Assert.Equal(1, instance.GetType().GetProperty("Calls")!.GetValue(instance));
        }
        finally { load.Unload(); }
    }

    [Fact]
    public void NaturalProviderWrongDerivedSignatureDoesNotFallBackToTheBaseMethod()
    {
        var imports = SunderRpcCSharpImportPrepass.Prepare(WithProvider(_lock), _root);
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services;
            public class BaseProvider { public Task PingAsync() => Task.CompletedTask; }
            [SunderServiceProvider(typeof(IExampleService))]
            public sealed partial class Provider : BaseProvider { public new Task<int> PingAsync() => Task.FromResult(1); }
            """;
        var result = new SunderRpcCSharpSourceCompiler().Compile(NaturalProviderInput(imports, source));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, value => value.Id == "rpc-authoring.handler-incomplete" && value.Message.Contains("PingAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NaturalProviderMixedMethodsReceiveTheActualHostContextForAuthorizationContentAndStreams()
    {
        var imports = PrepareNaturalImport(ContextDeclaration, "contextual");
        var compiled = CompileNaturalProvider(imports, ContextProvider);
        Assert.All(Assert.Single(compiled.ImportedProviders).NaturalMethods, binding => Assert.NotEmpty(binding.MethodId));
        using var bytes = new MemoryStream(); var emission = compiled.SourceCompilation!.Emit(bytes);
        Assert.True(emission.Success, string.Join(Environment.NewLine, emission.Diagnostics)); bytes.Position = 0;
        var load = new AssemblyLoadContext("natural-provider-real-context", isCollectible: true);
        try
        {
            var assembly = load.LoadFromStream(bytes);
            var type = assembly.GetType("ContextualProvider", true)!;
            var api = assembly.GetType("Sunder.Generated.Packages.Example_Dependency_Services.IContextualService", true)!;
            Assert.False(api.IsAssignableFrom(type)); // Deliberately not a C# interface implementation.
            var instance = Activator.CreateInstance(type)!;
            var registration = (SunderWorkerProviderRegistration)type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null)!;
            var host = new StackContributorSourceParityTests.ContentHost();
            var descriptor = Assert.Single(imports.Contracts).Descriptor;
            var context = host.Context(new("example.provider", "1.0.0", registration.ProviderId, descriptor.ContractId, descriptor.Version,
                descriptor.Sha256, descriptor.AbiSha256, Guid.NewGuid(), 1, 1, new("actual-context-endpoint"), 1, SunderRpcProviderState.Active));
            var handler = registration.Handler;
            Assert.Equal("plain:value", (await handler.InvokeUnaryAsync(context, "contextual", "plain", JsonSerializer.SerializeToElement("value"))).GetString());
            Assert.Null(type.GetProperty("LastContext")!.GetValue(instance));

            Assert.Equal("caller.package", (await handler.InvokeUnaryAsync(context, "contextual", "authorize", JsonSerializer.SerializeToElement("caller.package"))).GetString());
            Assert.Same(context, type.GetProperty("LastContext")!.GetValue(instance));
            var denied = await Assert.ThrowsAsync<SunderRpcException>(() => handler.InvokeUnaryAsync(context, "contextual", "authorize", JsonSerializer.SerializeToElement("forged.caller")).AsTask());
            Assert.Equal("test.caller-mismatch", denied.Error.Code);

            using var cancellation = new CancellationTokenSource();
            var content = await handler.InvokeUnaryAsync(context, "contextual", "upload", JsonSerializer.SerializeToElement("abc"), cancellation.Token);
            Assert.Equal("abc"u8.ToArray(), host.RegisteredBytes);
            Assert.True(Assert.Single(host.Registered).Token.CanBeCanceled);
            Assert.True(((CancellationToken)type.GetProperty("LastToken")!.GetValue(instance)!).CanBeCanceled);
            Assert.Same(context, type.GetProperty("LastContext")!.GetValue(instance));
            await using var sent = (SunderContent)type.GetProperty("LastContent")!.GetValue(instance)!;
            await using (var stillOwned = await sent.OpenReadAsync()) Assert.Equal((int)'a', stillOwned.ReadByte());
            Assert.Equal("caller.package:1", (await handler.InvokeUnaryAsync(context, "contextual", "download", content, cancellation.Token)).GetString());
            Assert.Equal(host.Content, Assert.Single(host.Opened).Reference);
            await using var received = (SunderContent)type.GetProperty("LastContent")!.GetValue(instance)!;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => received.OpenReadAsync().AsTask());

            var events = new List<string>();
            await foreach (var item in handler.InvokeServerStreamAsync(context, "contextual", "watch", JsonSerializer.SerializeToElement("event"), cancellation.Token))
                events.Add(item.GetString()!);
            Assert.Equal(new[] { "caller.package:event" }, events);
            Assert.Same(context, type.GetProperty("LastContext")!.GetValue(instance));
            using (var streamCancellation = new CancellationTokenSource())
            {
                await using var waiting = handler.InvokeServerStreamAsync(context, "contextual", "watch", JsonSerializer.SerializeToElement("wait"), streamCancellation.Token).GetAsyncEnumerator();
                Assert.True(await waiting.MoveNextAsync());
                var next = waiting.MoveNextAsync().AsTask();
                await ((TaskCompletionSource)type.GetProperty("WaitEntered")!.GetValue(instance)!).Task.WaitAsync(TimeSpan.FromSeconds(10));
                streamCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(((CancellationToken)type.GetProperty("LastToken")!.GetValue(instance)!).IsCancellationRequested);
            }
            var completed = await handler.InvokeUnaryAsync(context, "contextual", "reset", JsonSerializer.SerializeToElement(new { }));
            Assert.Empty(completed.EnumerateObject()); Assert.Equal(1, type.GetProperty("Resets")!.GetValue(instance));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.InvokeUnaryAsync(context, "contextual", "reset", JsonSerializer.SerializeToElement(new { }), cancellation.Token).AsTask());
            Assert.Equal(1, type.GetProperty("Resets")!.GetValue(instance));
        }
        finally { load.Unload(); }
    }

    [Theory]
    [InlineData("wrong-request")]
    [InlineData("wrong-result")]
    [InlineData("nullable-result")]
    [InlineData("duplicate-context")]
    [InlineData("cancellation-not-last")]
    [InlineData("ambiguous-context-overload")]
    [InlineData("renamed-method")]
    [InlineData("method-id-override")]
    public void NaturalProviderRejectsNonExactOrAmbiguousBindingsWithoutWireNameFallback(string mutation)
    {
        var imports = PrepareNaturalImport(ContextDeclaration, "contextual");
        var source = mutation switch
        {
            "wrong-request" => ContextProvider.Replace("PlainAsync(string request)", "PlainAsync(long request)", StringComparison.Ordinal),
            "wrong-result" => ContextProvider.Replace("Task<string> PlainAsync(string request) => Task.FromResult(\"plain:\" + request)", "Task<long> PlainAsync(string request) => Task.FromResult(1L)", StringComparison.Ordinal),
            "nullable-result" => ContextProvider.Replace("Task<string> PlainAsync", "Task<string?> PlainAsync", StringComparison.Ordinal).Replace("Task.FromResult(\"plain:\" + request)", "Task.FromResult<string?>(\"plain:\" + request)", StringComparison.Ordinal),
            "duplicate-context" => ContextProvider.Replace("SunderRpcInvocationContext request, string context)", "SunderRpcInvocationContext request, string context, SunderRpcInvocationContext other)", StringComparison.Ordinal),
            "cancellation-not-last" => ContextProvider.Replace("PlainAsync(string request)", "PlainAsync(CancellationToken token, string request)", StringComparison.Ordinal),
            "ambiguous-context-overload" => ContextProvider.Replace("public Task<string> PlainAsync", "public Task<string> PlainAsync(string request, SunderRpcInvocationContext context) => Task.FromResult(request);\n    public Task<string> PlainAsync", StringComparison.Ordinal),
            "renamed-method" => ContextProvider.Replace("public Task<string> PlainAsync", "[SunderMethod(MethodId = \"plain\")] public Task<string> OtherAsync", StringComparison.Ordinal),
            _ => ContextProvider.Replace("public Task<string> PlainAsync", "[SunderMethod(MethodId = \"other\")] public Task<string> PlainAsync", StringComparison.Ordinal),
        };
        Assert.NotEqual(ContextProvider, source);
        var result = new SunderRpcCSharpSourceCompiler().Compile(NaturalProviderInput(imports, source));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.handler-incomplete" && diagnostic.Path == "provider.cs");
    }

    [Fact]
    public void NaturalProviderRejectsATamperedNaturalSignatureEvenWhenTheImplementationMatchesIt()
    {
        var imports = PrepareNaturalImport(ContextDeclaration, "contextual", consumer: false);
        var sources = imports.Sources.Select(source => source.Path.StartsWith("natural-services/", StringComparison.Ordinal)
            ? source with { Text = source.Text.Replace("global::System.Threading.Tasks.ValueTask<string> PlainAsync", "global::System.Threading.Tasks.ValueTask<long> PlainAsync", StringComparison.Ordinal) }
            : source).ToArray();
        var provider = ContextProvider.Replace("Task<string> PlainAsync(string request) => Task.FromResult(\"plain:\" + request)", "Task<long> PlainAsync(string request) => Task.FromResult(1L)", StringComparison.Ordinal);
        var result = new SunderRpcCSharpSourceCompiler().Compile(NaturalProviderInput(imports, provider) with { SupplementalSources = sources });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.handler-incomplete" && diagnostic.Message.Contains("exact locked descriptor", StringComparison.Ordinal));
    }

    private const string ContextDeclaration = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        [SunderService(SunderServiceExposure.Public), SunderServiceError("test.caller-mismatch")]
        public interface ContextualService
        {
            ValueTask<string> PlainAsync(string value);
            [SunderMethod(Errors = new[] { "test.caller-mismatch" })] ValueTask<string> AuthorizeAsync(string caller);
            ValueTask<SunderContent> UploadAsync(string value);
            ValueTask<string> DownloadAsync(SunderContent value);
            IAsyncEnumerable<string> WatchAsync(string value);
            ValueTask ResetAsync();
        }
        """;

    private const string ContextProvider = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Runtime.CompilerServices;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        using Sunder.Generated.Packages.Example_Dependency_Services;
        [SunderServiceProvider(typeof(IContextualService))]
        public sealed partial class ContextualProvider
        {
            public SunderRpcInvocationContext? LastContext { get; private set; }
            public SunderContent? LastContent { get; private set; }
            public CancellationToken LastToken { get; private set; }
            public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Resets { get; private set; }
            public Task<string> PlainAsync(string request) => Task.FromResult("plain:" + request);
            // Parameter names deliberately oppose their transport roles.
            public ValueTask<string> AuthorizeAsync(SunderRpcInvocationContext request, string context)
            {
                LastContext = request;
                if (context != request.CallerPackageId) throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "test.caller-mismatch", "Wrong caller."));
                return ValueTask.FromResult(request.CallerPackageId);
            }
            public Task<SunderContent> UploadAsync(string value, SunderRpcInvocationContext invocation, CancellationToken token)
            {
                LastContext = invocation;
                LastToken = token;
                LastContent = SunderContent.FromBytes(Encoding.UTF8.GetBytes(value), "text/plain", "input.txt");
                return Task.FromResult(LastContent);
            }
            public async ValueTask<string> DownloadAsync(SunderRpcInvocationContext invocation, SunderContent value, CancellationToken token)
            {
                LastContext = invocation;
                LastContent = value;
                await using var input = await value.OpenReadAsync(token);
                return invocation.CallerPackageId + ":" + input.ReadByte();
            }
            public async IAsyncEnumerable<string> WatchAsync(string value, SunderRpcInvocationContext invocation, [EnumeratorCancellation] CancellationToken token = default)
            {
                LastContext = invocation;
                LastToken = token;
                await Task.Yield(); token.ThrowIfCancellationRequested();
                yield return invocation.CallerPackageId + ":" + value;
                if (value == "wait") { WaitEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            }
            public Task ResetAsync() { Resets++; return Task.CompletedTask; }
        }
        """;
}
