using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests
{
    public sealed class SunderRpcCSharpImportedProviderTests
    {
        private const string ContractId = "example.rpc.v1.minimal";
        private const string GeneratedNamespace = "Sunder.Generated.Rpc.Imports.CExample_Rpc_V1_Minimal";
        private const string ProviderAttribute = "[SunderServiceProvider(typeof(IMinimalProviderService))]";
        private const string UnaryImplementation = "ValueTask<Payload> IFirstProvider.EchoAsync(SunderRpcInvocationContext context, Payload request, CancellationToken cancellationToken) => throw new System.NotImplementedException();";
        private const string StreamImplementation = "IAsyncEnumerable<Payload> ISecondProvider.WatchAsync(SunderRpcInvocationContext context, Payload request, CancellationToken cancellationToken) => throw new System.NotImplementedException();";
        private const string MarkerSource = """
            namespace Sunder.Generated.Rpc;
            public interface IMinimalProviderService
                : Imports.CExample_Rpc_V1_Minimal.IFirstProvider,
                  Imports.CExample_Rpc_V1_Minimal.ISecondProvider
            {
            }
            """;
        private static readonly string ProviderSource = $$"""
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Rpc;
            using {{GeneratedNamespace}};
            namespace Example.Imported;
            {{ProviderAttribute}}
            public sealed partial class ImportedProvider : IMinimalProviderService
            {
                {{UnaryImplementation}}
                {{StreamImplementation}}
            }
            """;
        private const string OwnedSource = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            namespace Example.Owned;
            [SunderService(SunderServiceExposure.Public)]
            [SunderServiceProvider(ProviderId = "example.owned.provider")]
            public sealed partial class OwnedService
            {
                public ValueTask<Response> EchoAsync() => throw new System.NotImplementedException();
            }
            public sealed record Response
            {
                public required string Value { get; init; }
            }
            """;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Compile_MultipleImportedServices_ReusesDescriptorAndVerifiesGeneratedRegistration(bool explicitImplementations)
        {
            var input = CreateInput();
            if (!explicitImplementations)
            {
                input = WithProviderSource(input, ProviderSource
                    .Replace("ValueTask<Payload> IFirstProvider.", "public ValueTask<Payload> ", StringComparison.Ordinal)
                    .Replace("IAsyncEnumerable<Payload> ISecondProvider.", "public IAsyncEnumerable<Payload> ", StringComparison.Ordinal));
            }
            input = input with
            {
                Sources = input.Sources.Append(new SunderRpcCSharpSourceFile("Bootstrap.cs", """
                    namespace Example.Imported;
                    public static class Bootstrap
                    {
                        public static global::Sunder.Sdk.Worker.SunderWorkerProviderRegistration Create()
                            => new ImportedProvider().CreateSunderRpcProviderRegistration();
                        public static global::Sunder.Sdk.Worker.SunderWorkerProviderRegistration Declare(global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler handler)
                            => ImportedProvider.DeclareSunderRpcProvider(handler);
                    }
                    """)).ToArray(),
            };
            var compiler = new SunderRpcCSharpSourceCompiler();
            var result = compiler.Compile(input);

            AssertSuccess(result);
            Assert.Empty(result.Contracts);
            var imported = Assert.Single(result.ImportedProviders);
            var expectedSurface = Assert.Single(input.ImportedProviderSurfaces!);
            Assert.Same(expectedSurface, imported.Surface);
            Assert.Same(expectedSurface.Contract.Descriptor, imported.Surface.Contract.Descriptor);
            Assert.Equal(ContractId, imported.Surface.Contract.ContractKey);
            Assert.Equal(ContractId + ".provider", imported.Provider.ProviderId);
            Assert.Equal(SunderServiceAudience.Public, imported.Provider.Audience);
            Assert.Equal(2, imported.Surface.Contract.Descriptor.Services.Count);

            var generated = SunderRpcCSharpImplementationEmitter.Emit(result.Contracts, result.ImportedProviders);
            Assert.Contains($"new global::{GeneratedNamespace}.ExampleRpcV1MinimalRpcHandler(this, this)", generated, StringComparison.Ordinal);
            Assert.Contains(imported.Surface.Contract.Descriptor.Sha256, generated, StringComparison.Ordinal);
            Assert.Contains(imported.Surface.Contract.Descriptor.AbiSha256, generated, StringComparison.Ordinal);
            Assert.Contains("\"2.3.4\"", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonSerializer", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("__SunderGeneratedRpcHandler", generated, StringComparison.Ordinal);

            var unresolved = compiler.Compile(input with { RequireGeneratedMembersResolved = true });
            Assert.False(unresolved.Success);
            Assert.Equal(2, unresolved.Diagnostics.Count);
            Assert.All(unresolved.Diagnostics, diagnostic => Assert.Equal("rpc-authoring.unsupported-source", diagnostic.Id));
            Assert.Contains(unresolved.Diagnostics, diagnostic => diagnostic.Message.Contains("CS0117", StringComparison.Ordinal));
            Assert.Contains(unresolved.Diagnostics, diagnostic => diagnostic.Message.Contains("CS1061", StringComparison.Ordinal));
            var verified = compiler.Compile(input with
            {
                SupplementalSources = input.SupplementalSources!
                    .Append(new SunderRpcCSharpSourceFile("authoring.g.cs", generated)).ToArray(),
                RequireGeneratedMembersResolved = true,
            });
            AssertSuccess(verified);
            Assert.Empty(verified.Contracts);
            Assert.Same(expectedSurface, Assert.Single(verified.ImportedProviders).Surface);
        }

        [Theory]
        [InlineData("unary")]
        [InlineData("stream")]
        [InlineData("wrong-return-type")]
        [InlineData("no-marker")]
        public void Compile_IncompleteImportedImplementation_ReportsHandlerIncomplete(string missing)
        {
            var source = missing switch
            {
                "unary" => ProviderSource.Replace(UnaryImplementation, string.Empty, StringComparison.Ordinal),
                "stream" => ProviderSource.Replace(StreamImplementation, string.Empty, StringComparison.Ordinal),
                "wrong-return-type" => ProviderSource.Replace("ValueTask<Payload> IFirstProvider.EchoAsync", "public Task<Payload> EchoAsync", StringComparison.Ordinal),
                _ => ProviderSource.Replace(": IMinimalProviderService", ": IFirstProvider, ISecondProvider", StringComparison.Ordinal),
            };

            AssertDiagnostic(new SunderRpcCSharpSourceCompiler().Compile(WithProviderSource(CreateInput(), source)),
                "rpc-authoring.handler-incomplete");
        }

        [Theory]
        [InlineData("marker")]
        [InlineData("services")]
        [InlineData("partial-marker")]
        [InlineData("partial-service")]
        public void Compile_OrdinarySourceCannotSpoofTrustedImportedInterfaces(string spoof)
        {
            var input = CreateInput();
            var supplemental = input.SupplementalSources!.ToArray();
            if (spoof is "marker" or "services")
            {
                var index = spoof == "marker" ? 1 : 0;
                input = input with
                {
                    Sources = input.Sources.Append(supplemental[index]).ToArray(),
                    SupplementalSources = supplemental.Where((_, candidate) => candidate != index).ToArray(),
                };
            }
            else
            {
                var index = spoof == "partial-marker" ? 1 : 0;
                var interfaceName = spoof == "partial-marker" ? "IMinimalProviderService" : "IFirstProvider";
                var namespaceName = spoof == "partial-marker" ? "Sunder.Generated.Rpc" : GeneratedNamespace;
                supplemental[index] = supplemental[index] with
                {
                    Text = supplemental[index].Text.Replace($"public interface {interfaceName}", $"public partial interface {interfaceName}", StringComparison.Ordinal),
                };
                input = input with
                {
                    // Matching paths must not make an ordinary syntax tree trusted.
                    Sources = input.Sources.Append(new SunderRpcCSharpSourceFile(supplemental[index].Path,
                        $"namespace {namespaceName}; public partial interface {interfaceName} {{ }}")).ToArray(),
                    SupplementalSources = supplemental,
                };
            }

            AssertDiagnostic(new SunderRpcCSharpSourceCompiler().Compile(input), "rpc-authoring.handler-incomplete");
        }

        [Theory]
        [InlineData("marker")]
        [InlineData("services")]
        [InlineData("inherited-service")]
        public void Compile_MissingTrustedSurfaceOrService_ReportsHandlerIncomplete(string missing)
        {
            var input = CreateInput();
            input = input with
            {
                SupplementalSources = missing switch
                {
                    "marker" => [input.SupplementalSources![0]],
                    "services" => [input.SupplementalSources![1]],
                    _ => [input.SupplementalSources![0], new("imports.g.cs", MarkerSource.Replace(
                        "Imports.CExample_Rpc_V1_Minimal.IFirstProvider,", string.Empty, StringComparison.Ordinal))],
                },
            };

            AssertDiagnostic(new SunderRpcCSharpSourceCompiler().Compile(input), "rpc-authoring.handler-incomplete");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Compile_ReferencedMetadataCannotSupplyImportedInterfaces(bool supplementalMarker)
        {
            var input = CreateInput();
            var contract = input.ImportedProviderSurfaces![0].Contract with
            {
                GeneratedLogicalName = "Sunder.Package.Build.Tests.ImportedProviderMetadata",
            };
            var metadataName = typeof(ImportedProviderMetadata.IMetadataProviderService).FullName!;
            var markerName = supplementalMarker ? "Sunder.Generated.Rpc.IMetadataProviderService" : metadataName;
            input = input with
            {
                Sources = [new("Provider.cs", $$"""
                    [Sunder.Sdk.Services.SunderServiceProvider(typeof({{markerName}}))]
                    public partial class Provider : {{markerName}} { }
                    """)],
                ReferencePaths = input.ReferencePaths.Append(typeof(ImportedProviderMetadata.IMetadataProviderService).Assembly.Location).ToArray(),
                SupplementalSources = supplementalMarker ? [new("imports.g.cs", $$"""
                    namespace Sunder.Generated.Rpc;
                    public interface IMetadataProviderService : global::{{metadataName}} { }
                    """)] : [],
                ImportedProviderSurfaces = [new("metadataProvider", contract, markerName)],
            };

            AssertDiagnostic(new SunderRpcCSharpSourceCompiler().Compile(input), "rpc-authoring.handler-incomplete");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Compile_UnboundProviderImport_FailsWithoutAnOrdinarySourceProvider(bool supplementalProvider)
        {
            var input = CreateInput();
            input = input with
            {
                Sources = [],
                SupplementalSources = supplementalProvider
                    ? input.SupplementalSources!.Concat(input.Sources).ToArray()
                    : input.SupplementalSources,
            };

            AssertDiagnostic(new SunderRpcCSharpSourceCompiler().Compile(input), "rpc-authoring.handler-incomplete");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Compile_ProviderIdentityCollisions_AreGlobalAcrossOwnedAndImportedProviders(bool owned)
        {
            var input = CreateInput();
            var otherSource = owned
                ? OwnedSource.Replace("example.owned.provider", ContractId + ".provider", StringComparison.Ordinal)
                : ProviderSource.Replace("class ImportedProvider", "class OtherProvider", StringComparison.Ordinal);
            input = input with { Sources = input.Sources.Append(new SunderRpcCSharpSourceFile("Other.cs", otherSource)).ToArray() };

            var result = new SunderRpcCSharpSourceCompiler().Compile(input);

            AssertDiagnostic(result, "rpc-authoring.identity-collision");
            Assert.Single(Assert.Single(result.Diagnostics).RelatedSpans!);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Compile_MultipleRegistrationsOnOneSourceClass_AreRejected(bool owned)
        {
            var input = CreateInput();
            input = owned
                ? input with
                {
                    Sources = [new("Owned.cs", OwnedSource.Replace(
                        "[SunderServiceProvider(ProviderId = \"example.owned.provider\")]",
                        "[SunderServiceProvider(ProviderId = \"example.one\")]\n[SunderServiceProvider(ProviderId = \"example.two\")]", StringComparison.Ordinal))],
                    ImportedProviderSurfaces = [],
                    SupplementalSources = [],
                }
                : WithProviderSource(input, ProviderSource.Replace(ProviderAttribute,
                    "[SunderServiceProvider(typeof(IMinimalProviderService), ProviderId = \"example.one\")]\n[SunderServiceProvider(typeof(IMinimalProviderService), ProviderId = \"example.two\")]", StringComparison.Ordinal));

            AssertDiagnostic(new SunderRpcCSharpSourceCompiler().Compile(input), "rpc-authoring.provider-invalid");
        }

        [Theory]
        [InlineData("Owner", SunderServiceAudience.Owner)]
        [InlineData("Public", SunderServiceAudience.Public)]
        public void Compile_ImportedProvider_UsesExistingAudienceAndExplicitIdentityRules(string audience, SunderServiceAudience expected)
        {
            var input = WithProviderSource(CreateInput(), ProviderSource.Replace(ProviderAttribute,
                $"[SunderServiceProvider(typeof(IMinimalProviderService), ProviderId = \"example.custom\", Audience = SunderServiceAudience.{audience})]", StringComparison.Ordinal));

            var result = new SunderRpcCSharpSourceCompiler().Compile(input);

            AssertSuccess(result);
            var provider = Assert.Single(result.ImportedProviders).Provider;
            Assert.Equal("example.custom", provider.ProviderId);
            Assert.Equal(expected, provider.Audience);
        }

        [Theory]
        [InlineData("ProviderId = \"Not.Canonical\"")]
        [InlineData("Audience = (SunderServiceAudience)99")]
        public void Compile_ImportedProvider_RejectsInvalidIdentityOrAudience(string arguments)
        {
            var input = WithProviderSource(CreateInput(), ProviderSource.Replace(ProviderAttribute,
                $"[SunderServiceProvider(typeof(IMinimalProviderService), {arguments})]", StringComparison.Ordinal));

            AssertDiagnostic(new SunderRpcCSharpSourceCompiler().Compile(input), "rpc-authoring.provider-invalid");
        }

        [Theory]
        [InlineData("public abstract partial class ImportedProvider")]
        [InlineData("public sealed class ImportedProvider")]
        [InlineData("public partial class ImportedProvider<T>")]
        [InlineData("public partial record ImportedProvider")]
        public void Compile_ImportedProvider_RequiresOrdinaryConcreteNonGenericPartialClass(string declaration)
        {
            var input = WithProviderSource(CreateInput(), ProviderSource.Replace(
                "public sealed partial class ImportedProvider", declaration, StringComparison.Ordinal));

            AssertDiagnostic(new SunderRpcCSharpSourceCompiler().Compile(input), "rpc-authoring.provider-invalid");
        }

        [Theory]
        [InlineData(127, true)]
        [InlineData(128, false)]
        public void Compile_ProviderLimit_CountsOwnedAndImportedRegistrations(int importedCount, bool success)
        {
            var input = CreateInput() with
            {
                Sources = Enumerable.Range(0, importedCount).Select(index => new SunderRpcCSharpSourceFile($"Provider{index}.cs",
                    ProviderSource.Replace("class ImportedProvider", $"class ImportedProvider{index}", StringComparison.Ordinal)
                        .Replace(ProviderAttribute,
                            $"[SunderServiceProvider(typeof(IMinimalProviderService), ProviderId = \"example.provider.p{index}\")]", StringComparison.Ordinal)))
                    .Append(new("Owned.cs", OwnedSource)).ToArray(),
            };

            var result = new SunderRpcCSharpSourceCompiler().Compile(input);

            if (success)
            {
                AssertSuccess(result);
                Assert.Equal(importedCount, result.ImportedProviders.Count);
                Assert.Single(Assert.Single(result.Contracts).Providers);
            }
            else
            {
                AssertDiagnostic(result, "rpc-authoring.source-limit-exceeded");
            }
        }

        private static SunderRpcCSharpCompilerInput CreateInput()
        {
            var descriptor = SunderRpcContractDescriptor.Parse("""
                {
                  "descriptorVersion": 1,
                  "schemaProfile": "sunder.rpc.values.v1",
                  "contractId": "example.rpc.v1.minimal",
                  "version": "2.3.4",
                  "$defs": {
                    "Payload": {
                      "type": "object",
                      "properties": { "wire_value": { "type": "string", "minLength": 2 } },
                      "required": ["wire_value"],
                      "additionalProperties": false
                    }
                  },
                  "errors": {},
                  "services": {
                    "first": {
                      "methods": {
                        "echo": {
                          "kind": "unary", "requestSchema": { "$ref": "#/$defs/Payload" }, "responseSchema": { "$ref": "#/$defs/Payload" },
                          "idempotency": "idempotent", "errors": []
                        }
                      }
                    },
                    "second": {
                      "methods": {
                        "watch": {
                          "kind": "server-stream", "requestSchema": { "$ref": "#/$defs/Payload" }, "eventSchema": { "$ref": "#/$defs/Payload" },
                          "idempotency": "none", "errors": []
                        }
                      }
                    }
                  }
                }
                """u8);
            // This fixture deliberately starts at the low-level descriptor boundary;
            // it has no authored named models to project.
            var contract = new SunderRpcCSharpImportedContract(ContractId, GeneratedNamespace, descriptor)
            { ModelProjection = Sunder.Package.Format.SunderRpcModelProjection.Empty };
            var platformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                                     ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
            var references = platformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Append(typeof(SunderServiceAttribute).Assembly.Location)
                .Append(typeof(SunderWorkerProviderRegistration).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new("example.consumer", "9.8.7", [new("Provider.cs", ProviderSource)], references,
                SupplementalSources:
                [
                    new("binding.g.cs", SunderRpcCSharpGenerator.Generate(descriptor, GeneratedNamespace)),
                    new("imports.g.cs", MarkerSource),
                ],
                ImportedProviderSurfaces: [new("minimalProvider", contract, "Sunder.Generated.Rpc.IMinimalProviderService")]);
        }

        private static SunderRpcCSharpCompilerInput WithProviderSource(SunderRpcCSharpCompilerInput input, string source)
            => input with { Sources = [new("Provider.cs", source)] };

        private static void AssertSuccess(SunderRpcCSharpCompilationResult result)
            => Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Id + ": " + diagnostic.Message)));

        private static void AssertDiagnostic(SunderRpcCSharpCompilationResult result, string id)
        {
            Assert.False(result.Success);
            Assert.Equal(id, Assert.Single(result.Diagnostics).Id);
            Assert.Empty(result.Contracts);
            Assert.Empty(result.ImportedProviders);
        }
    }
}

namespace Sunder.Package.Build.Tests.ImportedProviderMetadata
{
    public interface IMetadataProviderService : IFirstProvider, ISecondProvider { }
    public interface IFirstProvider { }
    public interface ISecondProvider { }
}
