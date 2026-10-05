using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderRpcCSharpProviderDeclarationTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Worker;
        using ServiceAlias = Example.GreetingService;
        namespace Example;
        [SunderRpcService(SunderRpcExposure.Public)]
        [SunderRpcDomainError("example.expected")]
        public sealed partial class GreetingService
        {
            public static int Constructed;
            public GreetingService() { Constructed++; throw new InvalidOperationException("Storage is unavailable before activation."); }
            [SunderRpcMethod(Errors = new[] { "example.expected" })]
            public ValueTask<Value> ReadAsync(Value request) => throw new NotImplementedException();
            [SunderRpcMethod]
            public IAsyncEnumerable<Value> WatchAsync(Value request) => throw new NotImplementedException();
        }
        public sealed record Value { public required string Text { get; init; } }
        public static class Bootstrap
        {
            public static SunderWorkerProviderRegistration Declare(ISunderRpcServiceHandler handler)
                => ServiceAlias.DeclareSunderRpcProvider(handler);
        }
        """;

    [Fact]
    public async Task Declaration_ProvidesImmutableIdentityAndErrorsWithoutConstructingServiceAndForwardsExactly()
    {
        var compiler = new SunderRpcCSharpSourceCompiler();
        var input = Input(Source);
        var compiled = compiler.Compile(input);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        var contract = Assert.Single(compiled.Contracts);
        var generated = SunderRpcCSharpImplementationEmitter.Emit(compiled.Contracts);
        var verified = compiler.Compile(input with { SupplementalSources = [new("generated.cs", generated)], RequireGeneratedMembersResolved = true });
        Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics));
        var assembly = CSharpCompilation.Create("ProviderDeclaration", new[] { Source, generated }.Select(text => CSharpSyntaxTree.ParseText(text,
            new CSharpParseOptions(LanguageVersion.CSharp14))), input.ReferencePaths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        using var stream = new MemoryStream();
        var emit = assembly.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext("provider-declaration", isCollectible: true);
        try
        {
            var loaded = context.LoadFromStream(stream);
            var declare = loaded.GetType("Example.Bootstrap")!.GetMethod("Declare")!
                .CreateDelegate<Func<ISunderRpcServiceHandler, SunderWorkerProviderRegistration>>();
            var probe = new Probe();
            var registration = declare(probe);
            Assert.Equal(0, loaded.GetType("Example.GreetingService")!.GetField("Constructed")!.GetValue(null));
            Assert.Equal("example.owner.greeting.provider", registration.ProviderId);
            Assert.Equal(contract.Descriptor.ContractId, registration.ContractId);
            Assert.Equal(contract.Descriptor.Version, registration.ContractVersion);
            Assert.Equal(contract.Descriptor.Sha256, registration.DescriptorSha256);
            Assert.Equal(contract.Descriptor.AbiSha256, registration.AbiSha256);
            Assert.Equal(new[] { "example.expected" }, registration.DeclaredErrors["greeting"]["read"]);
            Assert.Empty(registration.DeclaredErrors["greeting"]["watch"]);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)registration.DeclaredErrors["greeting"]["read"]).Add("forged"));
            Assert.Throws<ArgumentNullException>(() => declare(null!));
            using var cancellation = new CancellationTokenSource();
            var request = JsonSerializer.SerializeToElement(new { text = "opaque forwarded payload" });
            Assert.Equal(request, await registration.Handler.InvokeUnaryAsync(null!, "service", "read", request, cancellation.Token));
            Assert.Equal(("service", "read", cancellation.Token), probe.Last);
            var events = registration.Handler.InvokeServerStreamAsync(null!, "service", "watch", request, cancellation.Token);
            await foreach (var item in events) { Assert.Equal(request, item); break; }
            Assert.Equal(("service", "watch", cancellation.Token), probe.Last);
            Assert.Equal(1, probe.Disposals);
            var failure = new InvalidOperationException("forwarded failure");
            probe.Failure = failure;
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Handler.InvokeUnaryAsync(null!, "service", "read", request).AsTask()));
        }
        finally { context.Unload(); }
    }

    [Theory]
    [InlineData("public static int DeclareSunderRpcProvider;")]
    [InlineData("public sealed class __SunderGeneratedRpcDeclaration { }")]
    public void Declaration_ReservesGeneratedMembers(string member)
    {
        var source = Source.Replace("public static int Constructed;", "public static int Constructed; " + member, StringComparison.Ordinal)
            .Replace("=> ServiceAlias.DeclareSunderRpcProvider(handler);", "=> throw new NotImplementedException();", StringComparison.Ordinal);
        var result = new SunderRpcCSharpSourceCompiler().Compile(Input(source));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.generated-name-collision");
    }

    [Fact]
    public void Declaration_DoesNotSuppressUnrelatedMissingMembers()
    {
        var source = Source.Replace("=> ServiceAlias.DeclareSunderRpcProvider(handler);", "=> Value.DeclareSunderRpcProvider(handler);", StringComparison.Ordinal);
        var result = new SunderRpcCSharpSourceCompiler().Compile(Input(source));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("CS0117", StringComparison.Ordinal));
    }

    private static SunderRpcCSharpCompilerInput Input(string source) => new("example.owner", "1.0.0", [new("Source.cs", source)],
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(SunderRpcServiceAttribute).Assembly.Location, typeof(SunderWorker).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    private sealed class Probe : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors => throw new InvalidOperationException("Declaration must use generated metadata.");
        public (string, string, CancellationToken) Last { get; private set; }
        public int Disposals { get; private set; }
        public Exception? Failure { get; set; }
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
        {
            Last = (serviceId, methodId, token);
            return Failure is null ? ValueTask.FromResult(request) : ValueTask.FromException<JsonElement>(Failure);
        }
        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            Last = (serviceId, methodId, token);
            try { yield return request; await Task.CompletedTask; }
            finally { Disposals++; }
        }
    }
}
