using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class StackContributorSourceParityTests
{
    private const string ContractKey = "stack-contributor-rpc-service-adapter";
    private const string ServiceFile = "StackContributorRpcServiceAdapter.cs";
    private const string WireFile = "StackContributorRpcWireModels.cs";
    private const string ServiceType = "Sunder.Sdk.Stacks.StackContributorRpcServiceAdapter";
    private const string FactoryDeclaration = "internal partial ISunderRpcServiceHandler CreateSunderRpcServiceHandler();";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SunderRpcJsonConverterFactory() },
    };

    [Fact]
    public void OrdinarySource_ExtractsAll48DefinitionsAndExactShippedDescriptorAndAbi()
    {
        // No descriptor, migration baseline, generated bindings, or compiled Stack types feed extraction.
        var metadataSourceCompile = Compile(ReadSources());
        var contract = Assert.Single(metadataSourceCompile.Contracts);
        var extracted = WithDialect(contract.Descriptor);

        Assert.Equal(ContractKey, contract.ContractKey);
        Assert.Equal(SunderServiceExposure.Public, contract.Exposure);
        Assert.Equal("Sunder.Sdk.Stacks.StackContributorRpcServiceAdapter", contract.ServiceType.ToDisplayString());
        Assert.Equal(Accessibility.Internal, contract.ServiceType.DeclaredAccessibility);
        Assert.Equal(48, contract.SourceTypes.Count);
        Assert.Equal(48, extracted.Definitions.Count);
        Assert.All(contract.SourceTypes, type =>
        {
            Assert.True(SymbolEqualityComparer.Default.Equals(contract.ServiceType.ContainingAssembly, type.SourceType.ContainingAssembly));
            Assert.All(type.SourceType.Locations, location => Assert.True(location.IsInSource));
            Assert.All(type.Properties.Where(property => property.IsOptional), property =>
                Assert.NotEqual(NullableAnnotation.Annotated,
                    Assert.IsAssignableFrom<INamedTypeSymbol>(property.Source.Type).TypeArguments.Single().NullableAnnotation));
        });
        Assert.Equal(4, contract.SourceTypes.Count(type => type.IsNumericEnum));
        Assert.Equal(6, contract.Methods.Count);
        Assert.All(contract.Methods, method => Assert.True(SymbolEqualityComparer.Default.Equals(contract.ServiceType, method.Source.ContainingType)));

        // The old JSON is only an oracle, read after the actual ordinary-source compilation succeeds.
        var expected = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(
            Path.Combine(StackRoot(), "Contracts", "sunder.stack.contributor.rpc.json")));
        Assert.Equal(expected.GetCanonicalUtf8(), extracted.GetCanonicalUtf8());
        Assert.Equal(expected.GetCanonicalAbiProjectionUtf8(), extracted.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(expected.Sha256, extracted.Sha256);
        Assert.Equal(expected.AbiSha256, extracted.AbiSha256);
        Assert.Equal("sunder.stack.contributor", extracted.ContractId);
        Assert.Equal("2.0.0", extracted.Version);
        Assert.Equal("38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898", extracted.Sha256);
        Assert.Equal("6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8", extracted.AbiSha256);
        Assert.Equal(extracted.Sha256, Convert.ToHexString(SHA256.HashData(extracted.GetCanonicalUtf8())).ToLowerInvariant());
        Assert.Equal(extracted.AbiSha256, Convert.ToHexString(SHA256.HashData(
            [.. "sunder.rpc.abi.v1\0"u8, .. extracted.GetCanonicalAbiProjectionUtf8()])).ToLowerInvariant());
        var service = Assert.Single(extracted.Services);
        Assert.Equal("stack-contributor", service.ServiceId);
        Assert.Equal(
            ["export", "import", "import-applied", "list-export-items", "metadata", "preview-import"],
            service.Methods.Select(method => method.MethodId).Order(StringComparer.Ordinal));
        Assert.Equal(Inventory(expected), Inventory(extracted));
        Assert.All(service.Methods, method => Assert.Equal(SunderRpcMethodKind.Unary, method.Kind));
        Assert.Equal(
            "The contributor does not implement the requested optional Stack operation.",
            extracted.Errors["stack.method.unsupported"].Description);
    }

    [Theory]
    [InlineData("id-bound")]
    [InlineData("json-bound")]
    [InlineData("schema-version-bound")]
    [InlineData("list-bound")]
    [InlineData("map-key-bound")]
    [InlineData("requiredness")]
    [InlineData("error-code")]
    [InlineData("error-description")]
    [InlineData("idempotency")]
    [InlineData("alias")]
    [InlineData("numeric-enum")]
    public void OrdinarySource_MutationsChangeExtractedContractRatherThanReadingTheOracle(string mutation)
    {
        var sources = ReadSources();
        var original = WithDialect(Assert.Single(Compile(sources).Contracts).Descriptor);
        var (file, before, after) = mutation switch
        {
            "id-bound" => (WireFile, "MinLength = 1, MaxLength = 256", "MinLength = 1, MaxLength = 255"),
            "json-bound" => (WireFile, "MinLength = 2, MaxLength = 1000000", "MinLength = 3, MaxLength = 1000000"),
            "schema-version-bound" => (WireFile, "Minimum = \"1\", Maximum = \"2147483647\"", "Minimum = \"2\", Maximum = \"2147483647\""),
            "list-bound" => (WireFile, "MinItems = 0, MaxItems = 10000", "MinItems = 0, MaxItems = 9999"),
            "map-key-bound" => (WireFile, "MinLength = 1, MaxLength = 1024", "MinLength = 1, MaxLength = 1023"),
            "requiredness" => (WireFile, "public required bool SupportsExport", "public SunderRpcOptional<bool> SupportsExport"),
            "error-code" => (ServiceFile, "stack.method.unsupported", "stack.operation.unsupported"),
            "error-description" => (ServiceFile, "The contributor does not implement the requested optional Stack operation.", "The optional operation is unavailable."),
            "idempotency" => (ServiceFile, "MethodId = \"import\", Idempotency = SunderServiceIdempotency.None", "MethodId = \"import\", Idempotency = SunderServiceIdempotency.Idempotent"),
            "alias" => (WireFile, "SunderValue(\"Id\")", "SunderValue(\"Identifier\")"),
            "numeric-enum" => ("StackContributorModels.cs", "    Secret,", "    Secret = 2,"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var source = Assert.Single(sources, source => Path.GetFileName(source.Path) == file);
        Assert.Contains(before, source.Text, StringComparison.Ordinal);
        var text = source.Text.Replace(before, after, StringComparison.Ordinal);
        var mutated = WithDialect(Assert.Single(Compile(sources.Select(candidate =>
            candidate == source ? candidate with { Text = text } : candidate).ToArray()).Contracts).Descriptor);

        Assert.False(original.GetCanonicalUtf8().AsSpan().SequenceEqual(mutated.GetCanonicalUtf8()));
        Assert.NotEqual(original.Sha256, mutated.Sha256);
        if (mutation == "error-description")
        {
            Assert.Equal(original.GetCanonicalAbiProjectionUtf8(), mutated.GetCanonicalAbiProjectionUtf8());
            Assert.Equal(original.AbiSha256, mutated.AbiSha256);
        }
        else
        {
            Assert.False(original.GetCanonicalAbiProjectionUtf8().AsSpan().SequenceEqual(mutated.GetCanonicalAbiProjectionUtf8()));
            Assert.NotEqual(original.AbiSha256, mutated.AbiSha256);
        }
        if (mutation == "alias")
        {
            Assert.DoesNotContain("Id", mutated.Definitions.Keys);
            Assert.Contains("Identifier", mutated.Definitions.Keys);
            Assert.Equal("#/$defs/Identifier", mutated.Definitions["StackContributorMetadata"]
                .GetProperty("properties").GetProperty("contributorId").GetProperty("$ref").GetString());
        }
        if (mutation == "requiredness")
        {
            Assert.DoesNotContain(mutated.Definitions["StackContributorMetadata"].GetProperty("required").EnumerateArray(),
                member => member.GetString() == "supportsExport");
        }
    }

    [Fact]
    public void CreateHandler_PreservesConstructorChecks()
    {
        Assert.Equal("contributor", Assert.Throws<ArgumentNullException>(() => SunderStackContributorRpc.CreateHandler(null!)).ParamName);
        AssertContributorError(new IdentityContributor(), "A Stack RPC contributor must implement at least one package-local Stack interface.");
        AssertContributorError(new AppliedContributor { ContributorId = " " }, "A Stack RPC contributor must expose a non-empty contributor id.");
        AssertContributorError(new AppliedContributor { DisplayName = " " }, "A Stack RPC contributor must expose a non-empty display name.");
    }

    [Fact]
    public void NeutralTransport_EmitsActualServicePartialAndCompilesWithOnlySdkAndCuratedBcl()
    {
        var sources = ReadSources();
        var contract = Assert.Single(Compile(sources).Contracts);
        var generated = SunderRpcCSharpImplementationEmitter.EmitServiceHandler(contract with { Descriptor = WithDialect(contract.Descriptor) });
        Assert.Contains("internal partial global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler CreateSunderRpcServiceHandler()", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateSunderRpcProviderRegistration", generated, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Sunder.Sdk.Worker", "Sunder.Package.Build", "Sunder.Package.Format", "Sunder.ExecutionRuntime.Build", "Microsoft.CodeAnalysis" })
            Assert.DoesNotContain(forbidden, generated, StringComparison.Ordinal);
        var compilation = CSharpCompilation.Create("NeutralStackTransport",
            sources.Append(new("CoreStackTransport.g.cs", generated)).Select(source =>
                CSharpSyntaxTree.ParseText(source.Text, new CSharpParseOptions(LanguageVersion.CSharp14), source.Path)),
            GetReferencePaths().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        // The shipping factory must actually hold the generated nested handler, not another handwritten bridge.
        var handler = SunderStackContributorRpc.CreateHandler(new Contributor());
        var field = Assert.Single(handler.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic));
        var generatedHandler = Assert.IsAssignableFrom<ISunderRpcServiceHandler>(field.GetValue(handler));
        Assert.Equal(ServiceType + "+__SunderGeneratedRpcHandler", generatedHandler.GetType().FullName);
    }

    [Fact]
    public void Factory_RequiresGenerationAndSuppressionIsOnlyForTheExactTrustedDeclaration()
    {
        var input = CompilerInput(ReadSources());
        foreach (var strict in new[] { input with { ServiceHandlerFactoryTypes = null }, input with { RequireGeneratedMembersResolved = true } })
        {
            var missing = new SunderRpcCSharpSourceCompiler().Compile(strict);
            Assert.False(missing.Success);
            Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Message.Contains("CS8795", StringComparison.Ordinal));
        }
        var extra = new SunderRpcCSharpSourceFile("Untrusted.cs", """
            using Sunder.Sdk.Rpc;
            internal partial class Untrusted
            {
                internal partial ISunderRpcServiceHandler CreateSunderRpcServiceHandler();
            }
            """);
        var untrusted = new SunderRpcCSharpSourceCompiler().Compile(input with { Sources = [.. input.Sources, extra] });
        Assert.False(untrusted.Success);
        Assert.Contains(untrusted.Diagnostics, diagnostic => diagnostic.Path == "Untrusted.cs" && diagnostic.Message.Contains("CS8795", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("internal ISunderRpcServiceHandler CreateSunderRpcServiceHandler() => null!;")]
    [InlineData("internal partial ISunderRpcServiceHandler CreateSunderRpcServiceHandler() => null!;")]
    [InlineData("internal partial ISunderRpcServiceHandler CreateSunderRpcServiceHandler(int unused);")]
    [InlineData("public partial ISunderRpcServiceHandler CreateSunderRpcServiceHandler();")]
    [InlineData("internal static partial ISunderRpcServiceHandler CreateSunderRpcServiceHandler();")]
    [InlineData("internal partial object CreateSunderRpcServiceHandler();")]
    [InlineData("internal partial ISunderRpcServiceHandler? CreateSunderRpcServiceHandler();")]
    public void Factory_RejectsAuthoredImplementationsAndSignatureCollisions(string replacement)
    {
        var sources = ReadSources().Select(source => source with
        {
            Text = Path.GetFileName(source.Path) == ServiceFile
                ? source.Text.Replace(FactoryDeclaration, replacement, StringComparison.Ordinal) : source.Text,
        }).ToArray();
        var result = new SunderRpcCSharpSourceCompiler().Compile(CompilerInput(sources));
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("export", true, false, false)]
    [InlineData("import", false, true, false)]
    [InlineData("applied", false, false, true)]
    [InlineData("all", true, true, true)]
    public async Task Handler_MetadataReflectsOptionalCapabilitiesAndRetainsDeclaredErrors(
        string capability, bool export, bool import, bool applied)
    {
        var contributor = ContributorWith(capability);
        var handler = SunderStackContributorRpc.CreateHandler(contributor);
        contributor.ContributorId = "changed-after-construction";
        contributor.DisplayName = "Changed";
        var response = await Invoke(handler, new ContentHost().Context(), "metadata", new { });
        AssertJson(new StackContributorMetadata("actual", "Actual", export, import, applied), response);
        var declared = Assert.IsAssignableFrom<ISunderRpcDeclaredErrorMetadata>(handler).DeclaredErrors;
        Assert.Equal(6, declared["stack-contributor"].Count);
        Assert.Empty(declared["stack-contributor"]["metadata"]);
        Assert.All(declared["stack-contributor"].Where(pair => pair.Key != "metadata"),
            pair => Assert.Equal(["stack.method.unsupported"], pair.Value));
    }

    [Theory]
    [InlineData("list-export-items", "applied", "{\"packageId\":\"owner.package\"}")]
    [InlineData("export", "applied", "{\"itemSelections\":[]}")]
    [InlineData("preview-import", "export", "{\"fragments\":[],\"inputValues\":{},\"idRemaps\":{}}")]
    [InlineData("import", "export", "{\"fragments\":[],\"inputValues\":{},\"idRemaps\":{},\"selectedActionIds\":[]}")]
    [InlineData("import-applied", "export", "{\"ownerPackageId\":\"forged\",\"contributorId\":\"forged\",\"fragmentIds\":[],\"importedItems\":[]}")]
    public async Task Handler_UnsupportedOperationsRetainExactDomainError(string method, string capability, string request)
    {
        var handler = SunderStackContributorRpc.CreateHandler(ContributorWith(capability));
        var host = new ContentHost();
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => handler.InvokeUnaryAsync(
            host.Context(), "stack-contributor", method, JsonSerializer.Deserialize<JsonElement>(request)).AsTask());
        Assert.Equal(SunderRpcErrorKind.Domain, error.Error.Kind);
        Assert.Equal("stack.method.unsupported", error.Error.Code);
        Assert.Equal($"This Stack contributor does not support '{method}'.", error.Error.Message);
        Assert.Empty(host.Registered);
        Assert.Empty(host.Opened);
    }

    [Fact]
    public async Task Handler_DiscoveryAndExportPreservePublicJsonAndMediateContent()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var stream = new TrackingStream();
        var openedWith = CancellationToken.None;
        var contributor = new Contributor
        {
            Items =
            [
                new("bare", "Bare", "item"),
                new("empty", "Empty", "item", "", false, []),
                new("full", "Full", "item", "description", true,
                [
                    new("Public", "", StackValueSensitivity.Public, "", "", "public", false, false, false),
                    new("Secret", "hidden", StackValueSensitivity.Secret, "secret", "safe", "secret", true, true, true),
                    new("Unclassified", "value"),
                ]),
            ],
            ExportResult = new(
            [
                new("with-file", "schema", 1, "With file", "{}", "", false,
                    [new("input", "Input", StackValueSensitivity.Public, false, "", ""), new("secret", "Secret", StackValueSensitivity.Secret)],
                    [new("nested/data.bin", cancellationToken => { openedWith = cancellationToken; return ValueTask.FromResult<Stream>(stream); }, 3)], "full"),
                new("no-file", "schema", int.MaxValue, "No file", "[]"),
                new("empty-files", "schema", 2, "Empty files", "{}", RequiredInputs: [], Files: []),
            ],
            [new("required.package"), new("optional.package", "stable", "2.0.0", "1.0.0", false)],
            ["", "warning"]),
        };
        var host = new ContentHost();
        var context = host.Context();
        var handler = SunderStackContributorRpc.CreateHandler(contributor);
        var discovery = new StackExportDiscoveryContext("owner.package");
        AssertJson(new StackExportItemList(contributor.Items), await Invoke(handler, context, "list-export-items", discovery, token));
        Assert.Equal(discovery, contributor.Discovery);
        Assert.Equal(token, contributor.LastToken);

        var request = new StackExportRequest(
        [
            new("bare"), new("empty", []),
            new("full", [new("public", false, "", StackValueSensitivity.Public), new("secret", true, "override", StackValueSensitivity.Secret), new("default")]),
        ]);
        var response = await Invoke(handler, context, "export", request, token);
        AssertJson(request, JsonSerializer.SerializeToElement(contributor.ExportRequest, JsonOptions));
        Assert.Equal(token, contributor.LastToken);
        Assert.Equal(token, openedWith);
        Assert.True(stream.Disposed);
        var registration = Assert.Single(host.Registered);
        Assert.Same(stream, registration.Stream);
        Assert.Equal(token, registration.Token);
        Assert.Equal(new SunderRpcContentRegistrationOptions("application/octet-stream", "data.bin", 3, null, SunderRpcContentRepeatability.SingleUse, 1), registration.Options);
        Assert.Equal(new byte[] { 1, 2, 3 }, host.RegisteredBytes);
        AssertJson(new StackRpcExportContribution(
            contributor.ExportResult.Fragments.Select(fragment => new StackRpcFragmentExport(
                fragment.FragmentId, fragment.SchemaId, fragment.SchemaVersion, fragment.DisplayName, fragment.JsonPayload,
                fragment.Description, fragment.DefaultSelected, fragment.RequiredInputs,
                fragment.Files is { Count: > 0 } ? [new("nested/data.bin", host.Content)] : null, fragment.SourceItemId)).ToArray(),
            contributor.ExportResult.PackageRequirements, contributor.ExportResult.Warnings), response);
        Assert.Empty(host.Opened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_ExportDisposesTheCurrentFileWhenRegistrationFailsOrCancels(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        Exception failure = cancel ? new OperationCanceledException(cancellation.Token) : new IOException("register failed");
        var stream = new TrackingStream();
        var nextFileOpened = false;
        var contributor = new Contributor
        {
            ExportResult = new(
                [new("fragment", "schema", 1, "Fragment", "{}", Files:
                [
                    new("data.bin", _ => ValueTask.FromResult<Stream>(stream)),
                    new("next.bin", _ => { nextFileOpened = true; return ValueTask.FromResult<Stream>(new TrackingStream()); }),
                ])], [], []),
        };
        var host = new ContentHost { RegistrationFailure = failure };
        var error = await Record.ExceptionAsync(() => Invoke(SunderStackContributorRpc.CreateHandler(contributor),
            host.Context(), "export", new StackExportRequest([]), cancellation.Token));
        Assert.Same(failure, error);
        Assert.True(stream.Disposed);
        Assert.False(nextFileOpened);
        Assert.Equal(cancellation.Token, Assert.Single(host.Registered).Token);
        Assert.Null(host.Registered.Single().Options.Length);
    }

    [Theory]
    [InlineData("preview-import")]
    [InlineData("import")]
    public async Task Handler_ImportOperationsStampIdentityAndOpenContentLazily(string method)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var host = new ContentHost();
        var contributor = new Contributor
        {
            PreviewResult = new(
                Enum.GetValues<StackImportActionKind>().Select(kind => new StackImportAction(kind.ToString(), "Action", kind, false, "")).ToArray(),
                [new("input", "Input", StackValueSensitivity.Public, false, "", ""), new("secret", "Secret", StackValueSensitivity.Secret)],
                [new("warning", "Warning", StackImportConflictSeverity.Warning), new("error", "Error", StackImportConflictSeverity.Error, "fragment")],
                ["", "preview warning"]),
            ImportResult = new(StackImportOutcome.Partial, [new("target", "Target", "item")],
                new Dictionary<string, string> { ["source"] = "target" }, ["warning"], ["error"]),
        };
        var handler = SunderStackContributorRpc.CreateHandler(contributor);
        contributor.ContributorId = "changed-after-construction";
        StackRpcFragmentImport[] fragments =
        [
            new("fragment", "forged.package", "forged-contributor", "schema", 1, "Fragment", "{}", "", [new("nested/data.bin", host.Content)]),
            new("empty", "forged.package", "forged-contributor", "schema", 2, "Empty", "[]", Files: []),
            new("absent", "forged.package", "forged-contributor", "schema", int.MaxValue, "Absent", "{}"),
        ];
        var inputValues = new Dictionary<string, string> { ["Input"] = "", ["secret"] = "private" };
        var remaps = new Dictionary<string, string> { ["Source"] = "target" };
        var context = host.Context();
        var response = method == "import"
            ? await Invoke(handler, context, method, new StackRpcImportRequest(fragments, inputValues, remaps, ["Create", "Reuse"]), token)
            : await Invoke(handler, context, method, new StackRpcImportPreviewRequest(fragments, inputValues, remaps), token);

        Assert.Equal(token, contributor.LastToken);
        var localFragments = method == "import" ? contributor.ImportRequest!.Fragments : contributor.PreviewRequest!.Fragments;
        Assert.Equal(3, localFragments.Count);
        Assert.All(localFragments, fragment =>
        {
            Assert.Equal("owner.package", fragment.OwnerPackageId);
            Assert.Equal("actual", fragment.ContributorId);
        });
        Assert.Equal(fragments.Select(fragment => (fragment.FragmentId, fragment.SchemaId, fragment.SchemaVersion, fragment.DisplayName, fragment.JsonPayload, fragment.Description)),
            localFragments.Select(fragment => (fragment.FragmentId, fragment.SchemaId, fragment.SchemaVersion, fragment.DisplayName, fragment.JsonPayload, fragment.Description)));
        Assert.Empty(localFragments[1].Files!);
        Assert.Null(localFragments[2].Files);
        var file = Assert.Single(localFragments[0].Files!);
        Assert.Equal("nested/data.bin", file.RelativePath);
        Assert.Equal(host.Content.Length, file.Length);
        Assert.Empty(host.Opened);
        await using (var opened = await file.OpenReadAsync(token))
        {
            Assert.Equal(1, opened.ReadByte());
            Assert.Equal(2, opened.ReadByte());
            Assert.Equal(3, opened.ReadByte());
        }
        Assert.True(Assert.Single(host.OpenedStreams).Disposed);
        Assert.Equal(host.Content, Assert.Single(host.Opened).Reference);
        Assert.Equal(token, host.Opened.Single().Token);
        Assert.Empty(host.Registered);
        if (method == "import")
        {
            Assert.Equal(["Create", "Reuse"], contributor.ImportRequest!.SelectedActionIds);
            Assert.Equal("", contributor.ImportRequest.InputValues["input"]);
            Assert.Equal("private", contributor.ImportRequest.InputValues["secret"]);
            Assert.Equal("target", contributor.ImportRequest.IdRemaps["source"]);
            AssertJson(contributor.ImportResult, response);
        }
        else
        {
            Assert.Equal("", contributor.PreviewRequest!.InputValues["input"]);
            Assert.Equal("private", contributor.PreviewRequest.InputValues["secret"]);
            Assert.Equal("target", contributor.PreviewRequest.IdRemaps["source"]);
            AssertJson(contributor.PreviewResult, response);
        }
    }

    [Fact]
    public async Task Handler_AcceptsSchemaValidDecimalIntegerLexemesThroughGeneratedTransport()
    {
        var contributor = new Contributor();
        var host = new ContentHost();
        var context = host.Context();
        var handler = SunderStackContributorRpc.CreateHandler(contributor);
        var import = JsonSerializer.Deserialize<JsonElement>("""
            {"fragments":[{"fragmentId":"fragment","ownerPackageId":"forged.package","contributorId":"forged",
              "schemaId":"schema","schemaVersion":1.0,"displayName":"Fragment","jsonPayload":"{}"}],
             "inputValues":{},"idRemaps":{},"selectedActionIds":[]}
            """);
        var export = JsonSerializer.Deserialize<JsonElement>("""
            {"itemSelections":[{"itemId":"item","details":[{"detailId":"detail","isSelected":true,"sensitivityOverride":0.0}]}]}
            """);
        Assert.Equal("1.0", import.GetProperty("fragments")[0].GetProperty("schemaVersion").GetRawText());
        Assert.Equal("0.0", export.GetProperty("itemSelections")[0].GetProperty("details")[0].GetProperty("sensitivityOverride").GetRawText());
        await Invoke(handler, context, "import", import);
        var fragment = Assert.Single(contributor.ImportRequest!.Fragments);
        Assert.Equal(1, fragment.SchemaVersion);
        Assert.Equal("owner.package", fragment.OwnerPackageId);
        Assert.Equal("actual", fragment.ContributorId);
        await Invoke(handler, context, "export", export);
        var selection = Assert.Single(contributor.ExportRequest!.ItemSelections);
        Assert.Equal(StackValueSensitivity.Public, Assert.Single(selection.Details!).SensitivityOverride);
        Assert.Empty(host.Registered);
        Assert.Empty(host.Opened);
    }

    [Theory]
    [InlineData(StackImportOutcome.Completed)]
    [InlineData(StackImportOutcome.Partial)]
    [InlineData(StackImportOutcome.Failed)]
    public async Task Handler_ImportRetainsNumericOutcomesAndEmptyRequiredCollections(StackImportOutcome outcome)
    {
        var contributor = new Contributor { ImportResult = new(outcome, [], new Dictionary<string, string>(), [], []) };
        var response = await Invoke(SunderStackContributorRpc.CreateHandler(contributor), new ContentHost().Context(),
            "import", new StackRpcImportRequest([], new Dictionary<string, string>(), new Dictionary<string, string>(), []));
        AssertJson(contributor.ImportResult, response);
        Assert.Equal((int)outcome, response.GetProperty("outcome").GetInt32());
    }

    [Fact]
    public async Task Handler_ImportAppliedStampsTheCapturedContributorAndHostOwner()
    {
        using var cancellation = new CancellationTokenSource();
        var contributor = new Contributor();
        var handler = SunderStackContributorRpc.CreateHandler(contributor);
        contributor.ContributorId = "changed-after-construction";
        var request = new StackImportAppliedContext("forged.package", "forged-contributor", ["fragment"], [new("target", "Target", "item")]);
        var response = await Invoke(handler, new ContentHost().Context(), "import-applied", request, cancellation.Token);
        AssertJson(new { }, response);
        AssertJson(request with { OwnerPackageId = "owner.package", ContributorId = "actual" },
            JsonSerializer.SerializeToElement(contributor.AppliedContext, JsonOptions));
        Assert.Equal(cancellation.Token, contributor.LastToken);
    }

    [Fact]
    public async Task Handler_RetainsRawRoutingAndGeneratedJsonRejections()
    {
        var handler = SunderStackContributorRpc.CreateHandler(new Contributor());
        var context = new ContentHost().Context();
        var empty = JsonSerializer.SerializeToElement(new { });
        var missingService = handler.InvokeUnaryAsync(context, "wrong", "metadata", empty);
        var missingMethod = handler.InvokeUnaryAsync(context, "stack-contributor", "wrong", empty);
        Assert.Equal("stack.service.not-found: The Stack contributor service was not found.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => missingService.AsTask())).Message);
        Assert.Equal("stack.method.not-found: The Stack contributor method was not found.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => missingMethod.AsTask())).Message);
        Assert.Equal("stack.stream.unsupported: The Stack contributor contract defines unary methods only.",
            Assert.Throws<InvalidOperationException>(() => handler.InvokeServerStreamAsync(context, "stack-contributor", "metadata", empty)).Message);
        foreach (var json in new[] { "{\"ItemSelections\":[]}", "{\"itemSelections\":[],\"extra\":true}" })
        {
            await Assert.ThrowsAsync<JsonException>(() => handler.InvokeUnaryAsync(context, "stack-contributor", "export",
                JsonSerializer.Deserialize<JsonElement>(json)).AsTask());
        }
    }

    private static SunderRpcCSharpCompilationResult Compile(IReadOnlyList<SunderRpcCSharpSourceFile> sources)
    {
        var input = CompilerInput(sources);
        var compiler = new SunderRpcCSharpSourceCompiler();
        var result = compiler.Compile(input);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        var generated = SunderRpcCSharpImplementationEmitter.EmitServiceHandler(contract with { Descriptor = WithDialect(contract.Descriptor) });
        var resolved = compiler.Compile(input with
        {
            SupplementalSources = [new("CoreStackTransport.g.cs", generated)], RequireGeneratedMembersResolved = true,
        });
        Assert.True(resolved.Success, string.Join(Environment.NewLine, resolved.Diagnostics));
        var final = Assert.Single(resolved.Contracts);
        Assert.Equal(contract.Descriptor.GetCanonicalUtf8(), final.Descriptor.GetCanonicalUtf8());
        Assert.Equal(generated, SunderRpcCSharpImplementationEmitter.EmitServiceHandler(final with { Descriptor = WithDialect(final.Descriptor) }));
        return resolved;
    }

    private static SunderRpcCSharpCompilerInput CompilerInput(IReadOnlyList<SunderRpcCSharpSourceFile> sources)
        => new(
            "sunder.core", "9.8.7", sources, GetReferencePaths(),
            OwnedContractsByContractKey: new Dictionary<string, SunderRpcCSharpOwnedContractIdentity>(StringComparer.Ordinal)
            {
                [ContractKey] = new("sunder.stack.contributor", "2.0.0"),
            }, ServiceHandlerFactoryTypes: new HashSet<string>(StringComparer.Ordinal) { ServiceType });

    private static SunderRpcContractDescriptor WithDialect(SunderRpcContractDescriptor descriptor)
    {
        var root = JsonNode.Parse(descriptor.GetCanonicalUtf8())!.AsObject();
        Assert.False(root.ContainsKey("$schema"));
        root["$schema"] = SunderRpcProtocol.JsonSchemaDialect;
        return SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static string[] Inventory(SunderRpcContractDescriptor descriptor)
        => descriptor.Services.SelectMany(service => service.Methods.Select(method =>
            $"{service.ServiceId}/{method.MethodId}:{method.Kind}:{method.Idempotency}:{method.RequestSchemaReference}:{method.OutputSchemaReference}:{string.Join(',', method.Errors)}"))
            .Order(StringComparer.Ordinal).ToArray();

    private static SunderRpcCSharpSourceFile[] ReadSources()
        => Directory.EnumerateFiles(StackRoot(), "*.cs", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal).Select(path => new SunderRpcCSharpSourceFile(path, File.ReadAllText(path)))
            .Append(new("ImplicitUsings.cs", """
                global using System;
                global using System.Collections.Generic;
                global using System.IO;
                global using System.Linq;
                global using System.Threading;
                global using System.Threading.Tasks;
                """)).ToArray();

    private static string StackRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "languages", "csharp", "dotnet", "packages", "Sunder.Sdk.Stacks");
            if (File.Exists(Path.Combine(path, ServiceFile))) return path;
        }
        throw new InvalidOperationException("Could not locate ordinary SDK Stack source.");
    }

    private static string[] GetReferencePaths()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string[] names = ["System.Private.CoreLib", "System.Runtime", "System.Runtime.Extensions", "System.Collections",
            "System.Linq", "System.ObjectModel", "System.Threading", "System.Threading.Tasks", "System.Memory", "System.Text.Json", "System.Net.Http", "System.ComponentModel", "System.Runtime.Numerics"];
        return names.Select(name => Path.Combine(runtime, name + ".dll"))
            .Append(typeof(SunderServiceAttribute).Assembly.Location).ToArray();
    }

    private static async Task<JsonElement> Invoke<T>(ISunderRpcServiceHandler handler, SunderRpcInvocationContext context,
        string method, T request, CancellationToken cancellationToken = default)
    {
        var descriptor = SunderStackContributorRpc.Descriptor;
        var operation = descriptor.Services.Single().FindMethod(method)!;
        var json = request is JsonElement element ? element : JsonSerializer.SerializeToElement(request, JsonOptions);
        Assert.True(descriptor.IsValid(operation.RequestSchemaReference, json, out var error), error);
        var response = await handler.InvokeUnaryAsync(context, "stack-contributor", method, json, cancellationToken);
        Assert.True(descriptor.IsValid(operation.OutputSchemaReference, response, out error), error);
        return response;
    }

    private static void AssertJson<T>(T expected, JsonElement actual)
        => Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected, JsonOptions), JsonNode.Parse(actual.GetRawText())),
            $"Expected {JsonSerializer.Serialize(expected, JsonOptions)}; actual {actual.GetRawText()}");

    private static void AssertContributorError(IPackageStackContributor contributor, string message)
    {
        var error = Assert.Throws<ArgumentException>(() => SunderStackContributorRpc.CreateHandler(contributor));
        Assert.Equal("contributor", error.ParamName);
        Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
    }

    private static IdentityContributor ContributorWith(string capability) => capability switch
    {
        "export" => new ExportContributor(), "import" => new ImportContributor(), "applied" => new AppliedContributor(), "all" => new Contributor(),
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };

    private class IdentityContributor : IPackageStackContributor
    {
        public string ContributorId { get; set; } = "actual";
        public string DisplayName { get; set; } = "Actual";
    }

    private sealed class ExportContributor : IdentityContributor, IPackageStackExporter
    {
        public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(StackExportDiscoveryContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>([]);
        public ValueTask<StackExportContribution> ExportAsync(StackExportRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new StackExportContribution([], [], []));
    }

    private sealed class ImportContributor : IdentityContributor, IPackageStackImporter
    {
        public ValueTask<StackImportPreview> PreviewImportAsync(StackImportPreviewRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new StackImportPreview([], [], [], []));
        public ValueTask<StackImportResult> ImportAsync(StackImportRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new StackImportResult(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []));
    }

    private sealed class AppliedContributor : IdentityContributor, IPackageStackImportAppliedHandler
    {
        public ValueTask OnStackImportAppliedAsync(StackImportAppliedContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Contributor : IdentityContributor, IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
    {
        public IReadOnlyList<StackExportItemDescriptor> Items { get; init; } = [];
        public StackExportContribution ExportResult { get; init; } = new([], [], []);
        public StackImportPreview PreviewResult { get; init; } = new([], [], [], []);
        public StackImportResult ImportResult { get; init; } = new(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []);
        public StackExportDiscoveryContext? Discovery { get; private set; }
        public StackExportRequest? ExportRequest { get; private set; }
        public StackImportPreviewRequest? PreviewRequest { get; private set; }
        public StackImportRequest? ImportRequest { get; private set; }
        public StackImportAppliedContext? AppliedContext { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(StackExportDiscoveryContext context, CancellationToken cancellationToken = default)
        {
            Discovery = context; LastToken = cancellationToken;
            return ValueTask.FromResult(Items);
        }
        public ValueTask<StackExportContribution> ExportAsync(StackExportRequest request, CancellationToken cancellationToken = default)
        {
            ExportRequest = request; LastToken = cancellationToken;
            return ValueTask.FromResult(ExportResult);
        }
        public ValueTask<StackImportPreview> PreviewImportAsync(StackImportPreviewRequest request, CancellationToken cancellationToken = default)
        {
            PreviewRequest = request; LastToken = cancellationToken;
            return ValueTask.FromResult(PreviewResult);
        }
        public ValueTask<StackImportResult> ImportAsync(StackImportRequest request, CancellationToken cancellationToken = default)
        {
            ImportRequest = request; LastToken = cancellationToken;
            return ValueTask.FromResult(ImportResult);
        }
        public ValueTask OnStackImportAppliedAsync(StackImportAppliedContext context, CancellationToken cancellationToken = default)
        {
            AppliedContext = context; LastToken = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class TrackingStream() : MemoryStream([1, 2, 3])
    {
        public bool Disposed { get; private set; }
        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }

    internal sealed class ContentHost
    {
        private delegate SunderRpcInvocationContext ContextFactory(SunderRpcProviderSnapshot provider,
            Func<Stream, SunderRpcContentRegistrationOptions, CancellationToken, ValueTask<SunderRpcContentReference>> register,
            Func<SunderRpcContentReference, CancellationToken, ValueTask<Stream>> open);
        private static readonly Lazy<ContextFactory> Factory = new(CreateFactory);
        public SunderRpcContentReference Content { get; } = new("host-content", 3, new string('a', 64), "application/octet-stream",
            "data.bin", DateTimeOffset.MaxValue, SunderRpcContentRepeatability.SingleUse);
        public Exception? RegistrationFailure { get; init; }
        public List<(Stream Stream, SunderRpcContentRegistrationOptions Options, CancellationToken Token)> Registered { get; } = [];
        public List<(SunderRpcContentReference Reference, CancellationToken Token)> Opened { get; } = [];
        public List<TrackingStream> OpenedStreams { get; } = [];
        public byte[]? RegisteredBytes { get; private set; }

        public SunderRpcInvocationContext Context(SunderRpcProviderSnapshot? provider = null) => Factory.Value(
            provider ?? new("owner.package", "1.0.0", "actual-provider", SunderStackContributorRpc.ContractId, SunderStackContributorRpc.ContractVersion,
                SunderStackContributorRpc.DescriptorSha256, SunderStackContributorRpc.AbiSha256, Guid.NewGuid(), 1, 1,
                new("endpoint"), 1, SunderRpcProviderState.Active), Register, Open);

        private async ValueTask<SunderRpcContentReference> Register(Stream stream, SunderRpcContentRegistrationOptions options, CancellationToken token)
        {
            Registered.Add((stream, options, token));
            if (RegistrationFailure is { } failure) throw failure;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, token);
            RegisteredBytes = buffer.ToArray();
            return Content;
        }

        private ValueTask<Stream> Open(SunderRpcContentReference reference, CancellationToken token)
        {
            Opened.Add((reference, token));
            var stream = new TrackingStream();
            OpenedStreams.Add(stream);
            return ValueTask.FromResult<Stream>(stream);
        }

        private static ContextFactory CreateFactory()
        {
            // Only test Host plumbing uses the SDK's existing friend identity; no Stack/schema reflection or production API is added.
            const string source = """
                using System;
                using System.IO;
                using System.Threading;
                using System.Threading.Tasks;
                using Sunder.Sdk.Rpc;
                public sealed class StackParityInvocationAuthority : ISunderRpcInvocationAuthority
                {
                    private readonly Func<Stream, SunderRpcContentRegistrationOptions, CancellationToken, ValueTask<SunderRpcContentReference>> register;
                    private readonly Func<SunderRpcContentReference, CancellationToken, ValueTask<Stream>> open;
                    private StackParityInvocationAuthority(
                        Func<Stream, SunderRpcContentRegistrationOptions, CancellationToken, ValueTask<SunderRpcContentReference>> register,
                        Func<SunderRpcContentReference, CancellationToken, ValueTask<Stream>> open)
                    { this.register = register; this.open = open; }
                    public static SunderRpcInvocationContext Create(SunderRpcProviderSnapshot provider,
                        Func<Stream, SunderRpcContentRegistrationOptions, CancellationToken, ValueTask<SunderRpcContentReference>> register,
                        Func<SunderRpcContentReference, CancellationToken, ValueTask<Stream>> open)
                        => new("caller.package", "1.0.0", provider, DateTimeOffset.MaxValue, 1, CancellationToken.None,
                            new StackParityInvocationAuthority(register, open));
                    public CancellationToken RevocationToken => CancellationToken.None;
                    public void Revoke() { }
                    public ValueTask<SunderRpcContentReference> RegisterContentAsync(Stream source, SunderRpcContentRegistrationOptions options, CancellationToken token)
                        => register(source, options, token);
                    public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(string path, SunderRpcContentRegistrationOptions options, CancellationToken token)
                        => throw new InvalidOperationException("Stack contributors must register streams, not filesystem paths.");
                    public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken token) => open(reference, token);
                }
                """;
            var compilation = CSharpCompilation.Create("Sunder.Runtime.Host.Tests",
                [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14))],
                GetReferencePaths().Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return Assembly.Load(stream.ToArray()).GetType("StackParityInvocationAuthority")!.GetMethod("Create")!
                .CreateDelegate<ContextFactory>();
        }
    }
}
