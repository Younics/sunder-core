using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpSourceCompilerTests
{
    [Theory]
    [InlineData("[SunderService(SunderServiceExposure.Public)]", false, "discover,invoke")]
    [InlineData("[SunderService(SunderServiceExposure.Public), SunderAppUse(Enabled = false)]", null, "")]
    [InlineData("[SunderService(SunderServiceExposure.Public), SunderAppUse(Required = true, Actions = new[] { \"discover\" })]", true, "discover")]
    public void OrdinaryConcretePublicServiceOwnsItsAppPolicyWithoutInheritingRuntimeUse(string declaration, bool? required, string actions)
    {
        var source = SimpleServiceSource.Replace("[SunderService(SunderServiceExposure.Package)]", declaration, StringComparison.Ordinal);
        var result = CompileSource(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        Assert.Equal(required, contract.AppUse?.Required);
        Assert.Equal(actions, string.Join(',', contract.AppUse?.Actions ?? []));
        Assert.Null(contract.RuntimeUse);
        var extension = CompileSource("""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface ExtensionService { ValueTask<string> ReadAsync(); }
            """);
        Assert.True(extension.Success, string.Join(Environment.NewLine, extension.Diagnostics));
        Assert.Null(Assert.Single(extension.Contracts).AppUse);
    }

    [Fact]
    public void OrdinaryExtensionInterfacesSupportExplicitDistinctProvidersInTheSamePipeline()
    {
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public interface ExtensionService { ValueTask<string> EchoAsync(string value); Task FlushAsync(); }
            [SunderServiceProvider(typeof(ExtensionService), ProviderId = "example.first")]
            public sealed partial class First : ExtensionService
            { public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value); public Task FlushAsync() => Task.CompletedTask; }
            [SunderServiceProvider(typeof(ExtensionService), ProviderId = "example.second")]
            public sealed partial class Second : ExtensionService
            { public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value); public Task FlushAsync() => Task.CompletedTask; }
            """;
        var input = new SunderRpcCSharpCompilerInput("example.extensions", "1.2.3", [new("Extension.cs", source)], CompilationReferences());
        var compiler = new SunderRpcCSharpSourceCompiler(); var first = compiler.Compile(input);
        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.Equal(new[] { "example.first", "example.second" }, Assert.Single(first.Contracts).Providers.Select(provider => provider.ProviderId));
        var generated = SunderRpcCSharpImplementationEmitter.Emit(first.Contracts);
        var verified = compiler.Compile(input with { SupplementalSources = [new("providers.g.cs", generated)], RequireGeneratedMembersResolved = true });
        Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics));
        Assert.Equal(first.Contracts[0].Descriptor.GetCanonicalUtf8(), verified.Contracts[0].Descriptor.GetCanonicalUtf8());
    }

    [Theory]
    [InlineData("Package", "Enabled = false")]
    [InlineData("Public", "Enabled = false, Required = true")]
    [InlineData("Public", "Enabled = false, Actions = new[] { \"discover\" }")]
    public void DisabledAppUseRejectsConflictingOrPackagePrivatePolicy(string exposure, string policy)
    {
        var result = CompileSource(SimpleServiceSource.Replace("[SunderService(SunderServiceExposure.Package)]",
            $"[SunderService(SunderServiceExposure.{exposure}), SunderAppUse({policy})]", StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.app-use-invalid");
    }

    [Fact]
    public void OrdinaryServicesGenerateNaturalRootsCompletionAndInterfaceWithoutLifecycleHelpers()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            namespace Natural;
            using Sunder.Generated.Packages.Example_Natural_Services;
            [SunderService]
            internal sealed partial class GreetingService : IAsyncDisposable
            {
                public ValueTask<string?> GreetAsync(string? name, CancellationToken token = default) => ValueTask.FromResult(name);
                public Task CompleteAsync() => Task.CompletedTask;
                public ValueTask FlushAsync() => ValueTask.CompletedTask;
                public ValueTask<IReadOnlyList<string>> ListAsync(IReadOnlyList<string> values) => ValueTask.FromResult(values);
                public Task<IReadOnlyDictionary<string, int?>> MapAsync(IReadOnlyDictionary<string, int?> values) => Task.FromResult(values);
                public async IAsyncEnumerable<int?> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
                { await Task.Yield(); token.ThrowIfCancellationRequested(); yield return 3; }
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
                private Task HiddenAsync() => Task.CompletedTask;
                public string LocalHelper() => "local";
                public override string ToString() => "service";
            }
            internal sealed class Model(IGreetingService service) { public IGreetingService Service { get; } = service; }
            """;
        var input = new SunderRpcCSharpCompilerInput("example.natural", "2.0.0-beta.1+build.7", [new("Natural.cs", source)], CompilationReferences(), GenerateServiceInterfaces: true);
        var planned = SunderRpcCSharpOwnedClientPrepass.Compile(new(), input);
        Assert.True(planned.Compilation.Success, string.Join(Environment.NewLine, planned.Compilation.Diagnostics));
        var contract = Assert.Single(planned.Compilation.Contracts);
        Assert.Equal("2.0.0-beta.1+build.7", contract.Descriptor.Version);
        Assert.Equal(new[] { "complete", "flush", "greet", "list", "map", "watch" }, contract.Methods.Select(method => method.MethodId).Order(StringComparer.Ordinal));
        using var nullValue = JsonDocument.Parse("null");
        Assert.True(contract.Descriptor.IsValid("#/$defs/GreetingGreetResult", nullValue.RootElement, out _));
        var sources = planned.Sources.Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(planned.Compilation.Contracts, "example.natural", input.PackageVersion, providerSchemasPresent: true))
            .Append(new("authoring.g.cs", SunderRpcCSharpImplementationEmitter.Emit(planned.Compilation.Contracts)))
            .Append(new("Natural.cs", source)).ToArray();
        var compilation = CSharpCompilation.Create("Natural", sources.Select(file => CSharpSyntaxTree.ParseText(file.Text, path: file.Path)),
            CompilationReferences().Select(path => MetadataReference.CreateFromFile(path)), new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var binary = new MemoryStream();
        var emitted = compilation.Emit(binary);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
    }

    [Fact]
    public void OptionalMethodAnnotationPreservesTheOrdinaryPublicCanonicalBytes()
    {
        var explicitMethod = SimpleServiceSource.Replace("SunderServiceExposure.Package", "SunderServiceExposure.Public", StringComparison.Ordinal);
        var normal = explicitMethod.Replace("[SunderMethod]", "", StringComparison.Ordinal);
        var previous = Assert.Single(CompileSource(explicitMethod).Contracts);
        var current = CompileSource(normal);
        Assert.True(current.Success, string.Join(Environment.NewLine, current.Diagnostics));
        Assert.Equal(previous.Descriptor.GetCanonicalUtf8(), Assert.Single(current.Contracts).Descriptor.GetCanonicalUtf8());
    }

    [Fact]
    public void OrdinaryMetadataPreservesStableNamesConstraintsAndErrorsWithoutRpcAttributes()
    {
        var source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public, ContractId = "example.stable.api", ServiceId = "stable-service", IncludeSchemaDialect = true)]
            [SunderServiceError("test.error", Description = "Failure")]
            public sealed partial class StableService
            {
                [SunderMethod(MethodId = "old-method", Idempotency = SunderServiceIdempotency.Idempotent, Errors = new[] { "test.error" })]
                public ValueTask<Reply> RunAsync(Request request) => ValueTask.FromResult(new Reply { Value = request.Value });
            }
            [SunderValue("StableRequest")]
            public sealed record Request { [SunderValueConstraints(MinLength = 1, MaxLength = 12)] public required string Value { get; init; } }
            [SunderValue("StableReply")]
            public sealed record Reply { public required string Value { get; init; } }
            """;
        var current = CompileSource(source);
        Assert.True(current.Success, string.Join(Environment.NewLine, current.Diagnostics));
        var descriptor = Assert.Single(current.Contracts).Descriptor;
        Assert.Equal("example.stable.api", descriptor.ContractId);
        var service = Assert.Single(descriptor.Services);
        Assert.Equal("stable-service", service.ServiceId);
        var method = Assert.Single(service.Methods);
        Assert.Equal("old-method", method.MethodId);
        Assert.Equal(new[] { "test.error" }, method.Errors);
        Assert.Equal(1, descriptor.Definitions["StableRequest"].GetProperty("properties").GetProperty("value").GetProperty("minLength").GetInt32());
        Assert.Equal(12, descriptor.Definitions["StableRequest"].GetProperty("properties").GetProperty("value").GetProperty("maxLength").GetInt32());
    }

    [Theory]
    [InlineData("[SunderRpcService]")]
    [InlineData("[Sunder.Sdk.Rpc.SunderRpcService]")]
    public void RetiredServiceDeclarationsAreExplicitlyUnsupported(string declaration)
    {
        var result = CompileSource(SimpleServiceSource.Replace("[SunderService(SunderServiceExposure.Package)]", declaration, StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("CS0246", StringComparison.Ordinal)
            || diagnostic.Message.Contains("CS0234", StringComparison.Ordinal));
    }
}
