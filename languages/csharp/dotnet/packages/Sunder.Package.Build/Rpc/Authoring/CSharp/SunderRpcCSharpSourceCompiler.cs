using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpSourceFile(string Path, string Text);

internal sealed record SunderRpcCSharpOwnedContractIdentity(
    string ContractId,
    string ContractVersion);

internal enum SunderRpcCSharpCompilationOutputKind
{
    Library,
    ConsoleApplication,
    WindowsApplication,
}

internal sealed record SunderRpcCSharpCompilerInput(
    string PackageId,
    string PackageVersion,
    IReadOnlyList<SunderRpcCSharpSourceFile> Sources,
    IReadOnlyList<string> ReferencePaths,
    IReadOnlyList<string>? DefineConstants = null,
    bool NullableEnabled = true,
    IReadOnlyDictionary<string, string>? OwnedContractIdsByServiceMetadataName = null,
    IReadOnlyDictionary<string, SunderRpcCSharpOwnedContractIdentity>? OwnedContractsByContractKey = null,
    IReadOnlyList<SunderRpcCSharpSourceFile>? SupplementalSources = null,
    bool RequireGeneratedMembersResolved = false,
    SunderRpcCSharpCompilationOutputKind CompilationOutputKind = SunderRpcCSharpCompilationOutputKind.Library,
    IReadOnlyList<SunderRpcCSharpImportedProviderSurface>? ImportedProviderSurfaces = null,
    IReadOnlySet<string>? ServiceHandlerFactoryTypes = null,
    NullableContextOptions? NullableContext = null,
    IReadOnlySet<string>? AllowUnboundImportedProviderKeys = null,
    string AssemblyName = "Sunder.Rpc.Authoring",
    bool GenerateServiceInterfaces = false,
    IReadOnlyList<SunderRpcCSharpPackageApiSurface>? ImportedPackageApis = null)
{
    internal IReadOnlyDictionary<string, Sunder.Package.Format.SunderRpcServiceReferenceProjection> ServiceReferenceTargets { get; init; }
        = new Dictionary<string, Sunder.Package.Format.SunderRpcServiceReferenceProjection>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<INamedTypeSymbol, SunderRpcCSharpImportedModel> ImportedModels { get; init; }
        = new Dictionary<INamedTypeSymbol, SunderRpcCSharpImportedModel>(SymbolEqualityComparer.Default);
}

internal sealed record SunderRpcCSharpCompilerDiagnostic(
    string Id,
    string Message,
    string? Path,
    int? Line,
    int? Column,
    int? EndLine = null,
    int? EndColumn = null,
    IReadOnlyList<SunderRpcCSharpSourceSpan>? RelatedSpans = null);

internal sealed record SunderRpcCSharpSourceSpan(
    string Path,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

internal sealed record SunderRpcCSharpProviderModel(
    string ProviderId,
    SunderServiceAudience Audience,
    string ServiceId,
    INamedTypeSymbol SourceType)
{
    internal string BindingSuffix { get; init; } = string.Empty;
    internal string RegistrationFactory => "CreateSunderRpcProviderRegistration" + BindingSuffix;
    internal string ServicesFactory => "CreateSunderRpcProviderRegistrationForServices" + BindingSuffix;
    internal string DeclarationFactory => "DeclareSunderRpcProvider" + BindingSuffix;
    internal string PrivateFactory => "__CreateSunderRpcProviderRegistration" + BindingSuffix;
    internal string HandlerTypeName => "__SunderGeneratedRpcHandler" + BindingSuffix;
    internal string NaturalHandlerTypeName => "__SunderNaturalRpcHandler" + BindingSuffix;
}

internal sealed record SunderRpcCSharpImportedProvider(
    SunderRpcCSharpImportedProviderSurface Surface,
    SunderRpcCSharpProviderModel Provider)
{
    internal SunderRpcCSharpPlatformAdapter? SemanticAdapter { get; init; }
    internal IReadOnlyList<SunderRpcCSharpNaturalProviderMethod> NaturalMethods { get; init; } = [];
}

internal sealed record SunderRpcCSharpCompiledContract(
    string ContractKey,
    SunderServiceExposure Exposure,
    string GeneratedLogicalName,
    SunderRpcContractDescriptor Descriptor,
    IReadOnlyList<SunderRpcCSharpProviderModel> Providers,
    INamedTypeSymbol ServiceType,
    IReadOnlyList<SunderRpcCSharpMethodModel> Methods)
{
    internal string PackageId { get; init; } = string.Empty;
    internal Sunder.Package.Format.SunderRpcModelProjection ModelProjection { get; init; } = null!;
    internal Sunder.Package.Format.SunderRpcMethodPolicies MethodPolicies { get; init; } = Sunder.Package.Format.SunderRpcMethodPolicies.Empty;
    internal IReadOnlyList<SunderRpcCSharpSourceTypeModel> SourceTypes { get; init; } = [];
    internal SunderRpcOwnedUsePolicy? AppUse { get; init; }
    internal SunderRpcOwnedUsePolicy? RuntimeUse { get; init; }
    internal bool UsesDateTimeOffset { get; init; }
    internal IReadOnlyDictionary<string, Sunder.Package.Format.SunderRpcServiceReferenceProjection> SourceServiceReferences { get; init; }
        = new Dictionary<string, Sunder.Package.Format.SunderRpcServiceReferenceProjection>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, SunderRpcCSharpResourceTarget> ResourceTargets { get; init; } = new Dictionary<string, SunderRpcCSharpResourceTarget>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, SunderRpcCSharpResourceLayout> ImportedValueLayouts { get; init; } = new Dictionary<string, SunderRpcCSharpResourceLayout>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> ImportedOptionalServiceReferenceMembers { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

internal sealed record SunderRpcCSharpSourceTypeModel(
    INamedTypeSymbol SourceType,
    bool IsTransparent,
    bool IsNumericEnum,
    IReadOnlyList<SunderRpcCSharpSourcePropertyModel> Properties)
{
    internal string DefinitionReference { get; init; } = string.Empty;

    internal bool TransparentValueAcceptsNull { get; init; }

    internal IReadOnlyList<SunderRpcCSharpSourceEnumMemberModel> EnumMembers { get; init; } = [];

    internal SunderRpcCSharpSourceUnionModel? Union { get; init; }

    internal SunderRpcCSharpSourceSyntheticTagModel? SyntheticTag { get; init; }

    internal SunderRpcCSharpSourceConstructorModel? Construction { get; init; }
}

internal sealed record SunderRpcCSharpSourceConstructorModel(
    IMethodSymbol Constructor,
    IReadOnlyList<IPropertySymbol?> Parameters,
    IReadOnlyList<IPropertySymbol> Initializers);

internal sealed record SunderRpcCSharpSourceSyntheticTagModel(string PropertyName, string Tag);

internal sealed record SunderRpcCSharpSourceUnionModel(
    string? Discriminator,
    IReadOnlyList<SunderRpcCSharpSourceUnionVariantModel> Variants)
{
    internal bool AcceptsNull { get; init; }
}

internal sealed record SunderRpcCSharpSourceUnionVariantModel(
    INamedTypeSymbol SourceType,
    string DefinitionReference,
    string? Tag);

internal sealed record SunderRpcCSharpSourceEnumMemberModel(
    IFieldSymbol Source,
    string? StringValue,
    long NumericValue);

internal sealed record SunderRpcCSharpSourcePropertyModel(
    IPropertySymbol Source,
    string WireName,
    bool IsOptional,
    bool HasExplicitWireName);

internal sealed record SunderRpcCSharpMethodModel(
    string MethodId,
    string Kind,
    string RequestDefinition,
    string OutputDefinition,
    string Idempotency,
    IReadOnlyList<string> Errors,
    IMethodSymbol Source)
{
    internal int TimeoutSeconds { get; init; }
    internal bool ReturnsUnit => Source.ReturnType is INamedTypeSymbol { Arity: 0, Name: "Task" or "ValueTask" };
}

internal sealed record SunderRpcCSharpCompilationResult(
    IReadOnlyList<SunderRpcCSharpCompiledContract> Contracts,
    IReadOnlyList<SunderRpcCSharpCompilerDiagnostic> Diagnostics)
{
    public string ResolvedPackageId { get; init; } = string.Empty;

    public IReadOnlyList<SunderRpcCSharpImportedProvider> ImportedProviders { get; init; } = [];

    public bool Success => Diagnostics.Count == 0;
    internal IReadOnlyList<SunderRpcCSharpPackageApiSurface> ImportedPackageApis { get; init; } = [];
    internal IReadOnlyList<SunderRpcCSharpImportedUse> ImportedUses { get; init; } = [];
    internal CSharpCompilation? SourceCompilation { get; init; }
    internal IReadOnlySet<INamedTypeSymbol> TrustedImportedModelTypes { get; init; }
        = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
}

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    private const int MaximumSourceBytes = 4_194_304;
    private const int MaximumContracts = 128;
    private const int MaximumProviders = 128;
    private const int MaximumIdentityCharacters = 128;
    private const int MaximumTraversalDepth = 64;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private const string InitialContractVersion = "1.0.0";
    private static readonly AssemblyName SdkAssemblyIdentity = typeof(SunderServiceAttribute).Assembly.GetName();

    public SunderRpcCSharpCompilationResult Compile(SunderRpcCSharpCompilerInput input)
        => CompileCore(input, discoverOwnedClients: false);

    // Only the internal owned-client prepass may use this provisional result.
    // It must recompile all source strictly with the derived clients before use.
    internal SunderRpcCSharpCompilationResult DiscoverForOwnedClientPrepass(SunderRpcCSharpCompilerInput input)
        => CompileCore(input, discoverOwnedClients: true);

    private SunderRpcCSharpCompilationResult CompileCore(SunderRpcCSharpCompilerInput input, bool discoverOwnedClients)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diagnostics = new List<SunderRpcCSharpCompilerDiagnostic>();
        if ((!string.IsNullOrWhiteSpace(input.PackageId) && !PackageId.TryParse(input.PackageId, out _))
            || !SemanticVersion.TryParse(input.PackageVersion, out _) || input.PackageVersion.Length > 256)
        {
            diagnostics.Add(new(
                !SemanticVersion.TryParse(input.PackageVersion, out _) || input.PackageVersion.Length > 256 ? "rpc-authoring.private-package-version-invalid" : "rpc-authoring.service-invalid",
                "C# RPC compilation requires a canonical lowercase package ID when supplied and a strict semantic package version.",
                null,
                null,
                null));
            return new([], diagnostics);
        }

        var sourceBytes = input.Sources.Sum(static source => (long)NormalizedSourceByteCount(source.Text));
        if (sourceBytes > MaximumSourceBytes)
        {
            diagnostics.Add(new(
                "rpc-authoring.source-limit-exceeded",
                $"RPC-significant C# source exceeds the inclusive {MaximumSourceBytes} byte limit.",
                null,
                null,
                null));
            return new([], diagnostics);
        }

        var parseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp14,
            DocumentationMode.None,
            SourceCodeKind.Regular,
            input.DefineConstants ?? []);
        var ordinarySyntaxTrees = input.Sources.Select(source => CSharpSyntaxTree.ParseText(
            SourceText.From(source.Text, Encoding.UTF8),
            parseOptions,
            source.Path,
            cancellationToken: default)).ToArray();
        var supplementalSyntaxTrees = (input.SupplementalSources ?? []).Select(source => CSharpSyntaxTree.ParseText(
            SourceText.From(source.Text, Encoding.UTF8),
            parseOptions,
            source.Path,
            cancellationToken: default)).ToArray();
        var syntaxTrees = ordinarySyntaxTrees.Concat(supplementalSyntaxTrees).ToArray();

        var references = new List<MetadataReference>(input.ReferencePaths.Count);
        try
        {
            foreach (var path in input.ReferencePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                references.Add(MetadataReference.CreateFromFile(Path.GetFullPath(path)));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new(
                "rpc-authoring.unsupported-source",
                $"A C# compilation reference is unavailable or invalid: {exception.Message}",
                null,
                null,
                null));
            return new([], diagnostics);
        }

        var compilation = CSharpCompilation.Create(
            input.AssemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(
                input.CompilationOutputKind switch
                {
                    SunderRpcCSharpCompilationOutputKind.Library => OutputKind.DynamicallyLinkedLibrary,
                    SunderRpcCSharpCompilationOutputKind.ConsoleApplication => OutputKind.ConsoleApplication,
                    SunderRpcCSharpCompilationOutputKind.WindowsApplication => OutputKind.WindowsApplication,
                    _ => throw new ArgumentOutOfRangeException(nameof(input.CompilationOutputKind)),
                },
                optimizationLevel: OptimizationLevel.Release,
                deterministic: true,
                nullableContextOptions: input.NullableContext ?? (input.NullableEnabled
                    ? NullableContextOptions.Enable
                    : NullableContextOptions.Disable)));
        var compilationErrors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        AttributeSymbols attributes;
        try
        {
            attributes = ResolveAttributes(compilation);
        }
        catch (CompilerFailure failure)
        {
            diagnostics.Add(failure.Diagnostic);
            return new([], diagnostics);
        }

        var sourceTreeSet = ordinarySyntaxTrees.ToHashSet<SyntaxTree>(ReferenceEqualityComparer.Instance);
        var sourceTypes = ordinarySyntaxTrees
            .SelectMany(static tree => tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Select(declaration => compilation.GetSemanticModel(declaration.SyntaxTree).GetDeclaredSymbol(declaration))
            .OfType<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(static symbol => SourcePath(symbol), StringComparer.Ordinal)
            .ThenBy(static symbol => SourceStart(symbol))
            .ToArray();
        var importedSurfaces = new Dictionary<INamedTypeSymbol, SunderRpcCSharpImportedProviderSurface>(SymbolEqualityComparer.Default);
        try
        {
            var supplementalTreeSet = supplementalSyntaxTrees.ToHashSet<SyntaxTree>(ReferenceEqualityComparer.Instance);
            foreach (var surface in input.ImportedProviderSurfaces ?? [])
            {
                var symbol = compilation.GetTypeByMetadataName(surface.InterfaceMetadataName);
                if (symbol is not { TypeKind: TypeKind.Interface, IsGenericType: false }
                    || !IsOrdinarySourceSymbol(symbol, supplementalTreeSet)
                    || symbol.AllInterfaces.Any(service => !IsOrdinarySourceSymbol(service, supplementalTreeSet)))
                {
                    throw FailureAtLocations(
                        "rpc-authoring.handler-incomplete",
                        $"Imported RPC provider surface '{surface.Key}' must be declared solely in trusted supplemental source, including its service interfaces.",
                        symbol?.Locations ?? []);
                }
                foreach (var service in surface.Contract.Descriptor.Services)
                {
                    var metadataName = surface.IsNatural
                        ? surface.Contract.NaturalServices.Single(value => value.ServiceId == service.ServiceId).InterfaceMetadataName
                        : surface.Contract.GeneratedLogicalName + ".I" + SunderRpcCSharpSyntax.Identifier(service.ServiceId) + "Provider";
                    var serviceSymbol = compilation.GetTypeByMetadataName(metadataName);
                    if (serviceSymbol is not { TypeKind: TypeKind.Interface, IsGenericType: false }
                        || !IsOrdinarySourceSymbol(serviceSymbol, supplementalTreeSet)
                        || !SymbolEqualityComparer.Default.Equals(symbol, serviceSymbol)
                            && !symbol.AllInterfaces.Contains(serviceSymbol, SymbolEqualityComparer.Default))
                    {
                        throw Failure(
                            "rpc-authoring.handler-incomplete",
                            $"Imported RPC provider surface '{surface.Key}' must inherit trusted generated service interface '{metadataName}'.",
                            symbol);
                    }
                }
                if (!importedSurfaces.TryAdd(symbol, surface))
                {
                    throw Failure(
                        "rpc-authoring.generated-name-collision",
                        $"Imported RPC provider surface '{surface.Key}' duplicates generated interface '{surface.InterfaceMetadataName}'.",
                        symbol);
                }
            }
            // A sole implementation intent can bind its canonical natural
            // contract directly. Generated key-specific interfaces remain useful
            // when two reviewed reexport intents select the same contract.
            foreach (var group in (input.ImportedProviderSurfaces ?? []).Where(surface => surface.IsNatural)
                         .GroupBy(surface => surface.Contract.ContractKey, StringComparer.Ordinal))
            {
                if (group.Count() != 1) continue;
                var surface = group.Single();
                var canonical = compilation.GetTypeByMetadataName(SunderRpcCSharpImportPrepass.NaturalProviderInterface(surface.Contract));
                if (canonical is not null) importedSurfaces.TryAdd(canonical, surface);
            }
            foreach (var sourceType in sourceTypes)
            {
                foreach (var property in sourceType.GetMembers().OfType<IPropertySymbol>().Where(property => HasAttribute(property, attributes.ServiceMember)))
                    if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                        || property.GetMethod?.DeclaredAccessibility != Accessibility.Public || property.Type.TypeKind == TypeKind.Delegate)
                        throw Failure("rpc-authoring.service-member-invalid", "SunderServiceMember requires a public instance, non-indexed value property with a public getter; local callbacks and nonpublic members cannot be transferred.", property);
                if (HasAttribute(sourceType, attributes.AppUse) && !HasAttribute(sourceType, attributes.Service))
                    throw Failure("rpc-authoring.app-use-invalid", "App use policy belongs on an owned service declaration, not a provider or value type.", sourceType);
                if (sourceType.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributes.RuntimeUse)
                        && attribute.ConstructorArguments.Length == 0) && !HasAttribute(sourceType, attributes.Service))
                    throw Failure("rpc-authoring.runtime-use-invalid", "Runtime use policy belongs on an owned service declaration, not a provider or value type.", sourceType);
            }
        }
        catch (CompilerFailure failure)
        {
            diagnostics.Add(failure.Diagnostic);
            return new([], diagnostics);
        }
        // Generated metadata names its reviewed codecs directly. Verify their
        // source authority before reporting secondary C# accessibility errors
        // from a substituted reference assembly or a missing generated model.
        try { input = input with { ImportedModels = TrustedImportedModels(compilation, input, sourceTreeSet) }; }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new("rpc-authoring.model-projection-invalid", exception.Message, null, null, null));
            return new([], diagnostics);
        }
        // Prefer the RPC handler diagnostic for incomplete imported interface implementations.
        foreach (var diagnostic in compilationErrors.Where(diagnostic =>
                     !discoverOwnedClients && diagnostic.Id != "CS0535"
                     && !(importedSurfaces.Count != 0
                          && diagnostic.Id is "CS0736" or "CS0737" or "CS0738" or "CS0539" or "CS0540")
                     && !IsExpectedGeneratedMemberDiagnostic(diagnostic, input, compilation)))
        {
            diagnostics.Add(FromRoslynDiagnostic(diagnostic));
        }
        if (diagnostics.Count != 0)
        {
            return new([], diagnostics);
        }
        var serviceSymbols = sourceTypes.Where(symbol => HasAttribute(symbol, attributes.Service)).ToArray();
        var packageId = ResolvePackageId(input.PackageId, compilation, attributes.Package, serviceSymbols, diagnostics);
        if (packageId is null)
        {
            return new([], diagnostics);
        }
        if (discoverOwnedClients && input.GenerateServiceInterfaces && !(input.SupplementalSources ?? []).Any(source => source.Path == "service-forward-types.g.cs"))
        {
            var forwards = new StringBuilder("// <auto-generated />\n#nullable enable\n");
            foreach (var service in serviceSymbols)
            {
                string name;
                try { name = Editor.GeneratedPackageNames.ServiceInterface(packageId, service); }
                catch (ArgumentException) { continue; }
                if (compilation.GetTypeByMetadataName(name) is not null) continue;
                var dot = name.LastIndexOf('.');
                forwards.Append("namespace ").Append(name[..dot]).Append(" { ")
                    .Append(service.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")
                    .Append(" interface ").Append(name[(dot + 1)..]).AppendLine(" { } }");
            }
            if (forwards.Length > "// <auto-generated />\n#nullable enable\n".Length)
                return CompileCore(input with { SupplementalSources = (input.SupplementalSources ?? []).Append(new SunderRpcCSharpSourceFile("service-forward-types.g.cs", forwards.ToString())).ToArray() }, discoverOwnedClients: true);
        }
        try
        {
            input = input with { ServiceReferenceTargets = BuildServiceReferenceTargets(packageId, serviceSymbols, attributes,
                input.ImportedProviderSurfaces ?? [], input.ImportedPackageApis ?? []) };
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new("rpc-authoring.model-projection-invalid", exception.Message, null, null, null));
            return new([], diagnostics);
        }
        if (serviceSymbols.Length > MaximumContracts)
        {
            diagnostics.Add(new(
                "rpc-authoring.source-limit-exceeded",
                $"C# RPC source declares {serviceSymbols.Length} contracts; the inclusive limit is {MaximumContracts}.",
                null,
                null,
                null));
            return new([], diagnostics);
        }

        var contracts = new List<SunderRpcCSharpCompiledContract>(serviceSymbols.Length);
        var importedProviders = new List<SunderRpcCSharpImportedProvider>();
        IReadOnlyList<SunderRpcCSharpImportedUse> importedUses = [];
        var contractIds = new Dictionary<string, INamedTypeSymbol>(StringComparer.OrdinalIgnoreCase);
        var contractKeys = new Dictionary<string, INamedTypeSymbol>(StringComparer.OrdinalIgnoreCase);
        var generatedNames = new Dictionary<string, INamedTypeSymbol>(StringComparer.OrdinalIgnoreCase);
        var serviceApiNames = new Dictionary<string, INamedTypeSymbol>(StringComparer.OrdinalIgnoreCase);
        var contractIndexes = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        try
        {
            foreach (var service in serviceSymbols)
            {
                var contract = CompileService(input with { PackageId = packageId }, sourceTreeSet, attributes, service);
                if (input.GenerateServiceInterfaces)
                {
                    var apiName = Editor.GeneratedPackageNames.ServiceInterface(packageId, contract.Descriptor.Services.Single().ServiceId);
                    var authored = compilation.GetTypeByMetadataName(apiName);
                    if (authored is not null && IsOrdinarySourceSymbol(authored, sourceTreeSet))
                        throw Failure("rpc-authoring.generated-name-collision", $"Generated service API '{apiName}' conflicts with an authored declaration.", service, authored);
                    if (serviceApiNames.TryGetValue(apiName, out var otherService))
                        throw Failure("rpc-authoring.generated-name-collision", $"Services '{otherService}' and '{service}' generate the same API '{apiName}'. Use distinct source service names.", otherService, service);
                    serviceApiNames.Add(apiName, service);
                }
                if (contractIds.TryGetValue(contract.Descriptor.ContractId, out var existing))
                {
                    throw Failure(
                        "rpc-authoring.identity-collision",
                        $"RPC services '{existing.Name}' and '{service.Name}' produce colliding contract identity '{contract.Descriptor.ContractId}'.",
                        existing,
                        service);
                }
                contractIds.Add(contract.Descriptor.ContractId, service);
                if (contractKeys.TryGetValue(contract.ContractKey, out existing))
                {
                    throw Failure(
                        "rpc-authoring.identity-collision",
                        $"RPC services '{existing.Name}' and '{service.Name}' produce colliding contract key '{contract.ContractKey}'.",
                        existing,
                        service);
                }
                contractKeys.Add(contract.ContractKey, service);
                if (generatedNames.TryGetValue(contract.GeneratedLogicalName, out existing))
                {
                    throw Failure(
                        "rpc-authoring.generated-name-collision",
                        $"RPC services '{existing.Name}' and '{service.Name}' produce colliding generated name '{contract.GeneratedLogicalName}'.",
                        existing,
                        service);
                }
                generatedNames.Add(contract.GeneratedLogicalName, service);
                contractIndexes.Add(service, contracts.Count);
                contracts.Add(contract);
            }

            foreach (var providerType in sourceTypes)
            {
                foreach (var providerAttribute in providerType.GetAttributes()
                             .Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributes.Provider)))
                {
                    // The owning declaration already compiled its own shorthand.
                    // Other explicit bindings on that same class are independent.
                    if (HasAttribute(providerType, attributes.Service)
                        && (providerAttribute.ConstructorArguments.Length == 0
                            || providerAttribute.ConstructorArguments[0].Value is INamedTypeSymbol ownService
                                && SymbolEqualityComparer.Default.Equals(ownService, providerType))) continue;
                    if (providerAttribute.ConstructorArguments.Length != 1
                        || providerAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol service)
                    {
                        throw Failure(
                            "rpc-authoring.provider-invalid",
                            $"RPC provider '{providerType.Name}' must bind an ordinary source service or a trusted imported provider surface.",
                            providerType);
                    }
                    if (importedSurfaces.TryGetValue(service, out var surface))
                    {
                        IReadOnlyList<SunderRpcCSharpNaturalProviderMethod> naturalMethods = [];
                        if (surface.IsNatural)
                            naturalMethods = BindNaturalProvider(compilation, providerType, surface, sourceTreeSet, attributes);
                        else ValidateStandaloneProvider(providerType, service, sourceTreeSet, attributes.Method, imported: true);
                        importedProviders.Add(new(surface, CompileProvider(
                            providerAttribute,
                            surface.Contract.Descriptor.ContractId,
                            surface.Contract.Descriptor.Services.OrderBy(static item => item.ServiceId, StringComparer.Ordinal).First().ServiceId,
                            SunderServiceExposure.Public,
                            providerType,
                            service,
                            sourceTreeSet)) { NaturalMethods = naturalMethods });
                        continue;
                    }
                    var adapter = CompilePlatformAdapter(service, attributes, importedSurfaces.Values, providerType);
                    if (adapter is not null)
                    {
                        ValidateStandaloneProvider(providerType, service, sourceTreeSet, attributes.Method, imported: true);
                        importedProviders.Add(new(adapter.Surface, CompileProvider(providerAttribute,
                            adapter.Surface.Contract.Descriptor.ContractId, adapter.Surface.Contract.Descriptor.Services.Single().ServiceId,
                            SunderServiceExposure.Public, providerType, service, sourceTreeSet)) { SemanticAdapter = adapter });
                        continue;
                    }
                    if (!contractIndexes.TryGetValue(service, out var contractIndex))
                    {
                        throw Failure(
                            "rpc-authoring.provider-invalid",
                            $"RPC provider '{providerType.Name}' must bind an ordinary source service or a trusted imported provider surface.",
                            providerType);
                    }
                    ValidateStandaloneProvider(providerType, service, sourceTreeSet, attributes.Method);
                    var contract = contracts[contractIndex];
                    var serviceId = contract.Descriptor.Services.Single().ServiceId;
                    var provider = CompileProvider(
                        providerAttribute,
                        contract.Descriptor.ContractId,
                        serviceId,
                        contract.Exposure,
                        providerType,
                        service,
                        sourceTreeSet);
                    contracts[contractIndex] = contract with
                    {
                        Providers = contract.Providers.Append(provider)
                            .OrderBy(static item => item.ProviderId, StringComparer.Ordinal)
                            .ToArray(),
                    };
                }
            }

            foreach (var surface in importedSurfaces.Values)
            {
                if (!importedProviders.Any(provider => provider.Surface == surface)
                    && input.AllowUnboundImportedProviderKeys?.Contains(surface.Key) != true)
                {
                    throw FailureAtLocations(
                        "rpc-authoring.handler-incomplete",
                        $"Imported RPC provider surface '{surface.Key}' has no local provider registration.",
                        compilation.GetTypeByMetadataName(surface.InterfaceMetadataName)!.Locations);
                }
            }
            var packageProviderIds = new Dictionary<string, SunderRpcCSharpProviderModel>(StringComparer.OrdinalIgnoreCase);
            var packageProviderTypes = new Dictionary<INamedTypeSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
            foreach (var (contractId, provider) in contracts.SelectMany(contract => contract.Providers.Select(provider => (contract.Descriptor.ContractId, Provider: provider)))
                         .Concat(importedProviders.Select(imported => (imported.Surface.Contract.Descriptor.ContractId, imported.Provider))))
            {
                if (!packageProviderTypes.TryGetValue(provider.SourceType, out var bindings))
                    packageProviderTypes.Add(provider.SourceType, bindings = new(StringComparer.Ordinal));
                if (!bindings.Add(contractId))
                    throw Failure("rpc-authoring.provider-invalid", $"RPC source class '{provider.SourceType.Name}' repeats provider binding '{contractId}'. Declare each contract once on a class.", provider.SourceType);
                if (packageProviderIds.TryGetValue(provider.ProviderId, out var existing))
                {
                    throw Failure(
                        "rpc-authoring.identity-collision",
                        $"RPC package has colliding provider identity '{provider.ProviderId}'.",
                        existing.SourceType,
                        provider.SourceType);
                }
                packageProviderIds.Add(provider.ProviderId, provider);
            }
            SunderRpcCSharpProviderModel Bind(string contractId, SunderRpcCSharpProviderModel provider)
                => provider with { BindingSuffix = packageProviderTypes[provider.SourceType].Count > 1 ? "_" + Editor.GeneratedPackageNames.ContractSymbol(contractId) : string.Empty };
            contracts = contracts.Select(contract => contract with { Providers = contract.Providers.Select(provider => Bind(contract.Descriptor.ContractId, provider)).ToArray() }).ToList();
            importedProviders = importedProviders.Select(imported => imported with { Provider = Bind(imported.Surface.Contract.Descriptor.ContractId, imported.Provider) }).ToList();
            var responsePolicyMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var contract in contracts)
                foreach (var method in contract.Methods)
                {
                    var serviceId = contract.Descriptor.Services.Single().ServiceId;
                    responsePolicyMethods.Add(method.Source);
                    _ = ResponseContentCompletion(method.Source, contract.Descriptor, serviceId, method.MethodId);
                    foreach (var provider in contract.Providers)
                    {
                        var implementation = ProviderMethod(provider.SourceType, method.Source);
                        responsePolicyMethods.Add(implementation);
                        _ = ResponseContentCompletion(implementation, contract.Descriptor, serviceId, method.MethodId);
                    }
                }
            foreach (var provider in importedProviders)
                foreach (var method in provider.NaturalMethods)
                    responsePolicyMethods.Add(ProviderMethod(provider.Provider.SourceType, method.InvocationMethod));
            foreach (var method in sourceTypes.SelectMany(type => type.GetMembers().OfType<IMethodSymbol>()))
                if (!responsePolicyMethods.Contains(method) && method.GetAttributes().Any(attribute =>
                    SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributes.Method)
                    && attribute.NamedArguments.Any(pair => pair.Key == nameof(SunderMethodAttribute.CompleteOnResponseContentRetirement))))
                    throw Failure("rpc-authoring.method-invalid", "Response-content completion policy must bind an owned or imported natural provider method.", method);
            importedUses = CompileImportedUses(compilation, input, sourceTreeSet, sourceTypes, attributes, importedProviders);
            ValidateOptionalReferences(sourceTypes, contracts);
            var providerCount = packageProviderIds.Count;
            if (providerCount > MaximumProviders)
            {
                throw new CompilerFailure(new(
                    "rpc-authoring.source-limit-exceeded",
                    $"C# RPC source declares {providerCount} local providers; the inclusive limit is {MaximumProviders}.",
                    null,
                    null,
                    null));
            }
        }
        catch (CompilerFailure failure)
        {
            diagnostics.Add(failure.Diagnostic);
            return new([], diagnostics);
        }
        var remainingCompilationErrors = compilationErrors
            .Where(diagnostic => !discoverOwnedClients && !IsExpectedGeneratedMemberDiagnostic(diagnostic, input, compilation))
            .ToArray();
        if (remainingCompilationErrors.Length != 0)
        {
            diagnostics.AddRange(remainingCompilationErrors.Select(FromRoslynDiagnostic));
            return new([], diagnostics);
        }

        var projection = SunderRpcCSharpModelProjection.Plan(contracts, CompleteImportedContracts(input));
        if (!projection.Success) return new([], projection.Diagnostics);
        return new(
            projection.Contracts,
            diagnostics)
        {
            ResolvedPackageId = packageId,
            SourceCompilation = compilation,
            TrustedImportedModelTypes = input.ImportedModels.Keys.ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default),
            ImportedPackageApis = input.ImportedPackageApis ?? [],
            ImportedUses = importedUses,
            ImportedProviders = importedProviders.OrderBy(static item => item.Surface.Key, StringComparer.Ordinal)
                .ThenBy(static item => item.Provider.ProviderId, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static SunderRpcCSharpCompiledContract CompileService(
        SunderRpcCSharpCompilerInput input,
        IReadOnlySet<SyntaxTree> sourceTrees,
        AttributeSymbols attributes,
        INamedTypeSymbol service)
    {
        if (HasAttribute(service, attributes.PlatformImport))
            throw Failure("rpc-authoring.authority-mismatch", "A platform-import domain interface cannot also declare an owned RPC service.", service);
        if (service.TypeKind is not (TypeKind.Class or TypeKind.Interface)
            || service.IsGenericType
            || service.ContainingType is not null
            || service.IsRecord
            || !HasOrdinarySourceDeclaration(service, sourceTrees))
        {
            throw Failure(
                "rpc-authoring.service-invalid",
                $"RPC service '{service.ToDisplayString()}' must be a non-generic ordinary source class or interface.",
                service);
        }
        if (service.TypeKind == TypeKind.Class && service.IsAbstract)
        {
            throw Failure(
                "rpc-authoring.service-invalid",
                $"Concrete RPC service '{service.Name}' cannot be abstract.",
                service);
        }
        if (service.TypeKind == TypeKind.Class && service.IsRecord)
        {
            throw Failure(
                "rpc-authoring.service-invalid",
                $"Concrete RPC service '{service.Name}' must be a class rather than a record.",
                service);
        }
        if (service.TypeKind == TypeKind.Class && !IsPartialType(service))
        {
            throw Failure(
                "rpc-authoring.service-invalid",
                $"Concrete RPC service '{service.Name}' must be partial so generated registration members can be attached.",
                service);
        }

        var serviceAttribute = GetAttribute(service, attributes.Service)!;
        var exposure = (SunderServiceExposure)(int)(serviceAttribute.ConstructorArguments[0].Value ?? 0);
        if (!Enum.IsDefined(exposure))
        {
            throw Failure("rpc-authoring.service-invalid", $"RPC service '{service.Name}' has an invalid exposure.", service);
        }
        var explicitKey = GetNamedString(serviceAttribute, "Key");
        if (!SunderRpcCSharpNaming.TryCreate(explicitKey ?? service.Name, explicitKey is null ? "Service" : null, out var serviceName))
        {
            throw Failure("rpc-authoring.service-invalid", $"RPC service name '{service.Name}' cannot produce a canonical wire identity.", service);
        }
        var canonicalServiceName = serviceName!;
        var contractKey = canonicalServiceName.LowerKebab;

        var serviceId = GetNamedString(serviceAttribute, nameof(SunderServiceAttribute.ServiceId))
                        ?? canonicalServiceName.LowerKebab;
        var contractId = GetNamedString(serviceAttribute, nameof(SunderServiceAttribute.ContractId))
                         ?? $"{input.PackageId}.{canonicalServiceName.ContractSuffix}";
        if (contractId.Length > MaximumIdentityCharacters)
        {
            throw Failure(
                "rpc-authoring.source-limit-exceeded",
                $"Derived RPC contract identity '{contractId}' exceeds {MaximumIdentityCharacters} characters.",
                service);
        }
        if (!PackageId.TryParse(contractId, out _))
        {
            throw Failure(
                "rpc-authoring.service-invalid",
                $"RPC service '{service.Name}' contract identity '{contractId}' is not canonical.",
                service);
        }
        var serviceMetadataName = service.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        if (input.OwnedContractIdsByServiceMetadataName?.TryGetValue(serviceMetadataName, out var acceptedContractId) == true
            && !string.Equals(acceptedContractId, contractId, StringComparison.Ordinal))
        {
            throw Failure(
                "rpc-authoring.baseline-drift",
                $"RPC service '{serviceMetadataName}' derives contract identity '{contractId}', but its owned baseline records '{acceptedContractId}'.",
                service);
        }
        var contractVersion = exposure == SunderServiceExposure.Package ? input.PackageVersion : InitialContractVersion;
        if (exposure == SunderServiceExposure.Public && input.OwnedContractsByContractKey?.TryGetValue(contractKey, out var acceptedContract) == true)
        {
            if (!PackageId.TryParse(acceptedContract.ContractId, out _)
                || !SemanticVersion.TryParse(acceptedContract.ContractVersion, out _))
            {
                throw Failure(
                    "rpc-authoring.baseline-drift",
                    $"RPC owned baseline entry '{contractKey}' has an invalid contract identity or version.",
                    service);
            }
            if (!string.Equals(acceptedContract.ContractId, contractId, StringComparison.Ordinal))
            {
                throw Failure(
                    "rpc-authoring.baseline-drift",
                    $"RPC service '{serviceMetadataName}' derives contract identity '{contractId}', but contract key '{contractKey}' records '{acceptedContract.ContractId}'.",
                    service);
            }
            contractVersion = acceptedContract.ContractVersion;
        }
        var methods = service.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => HasAttribute(method, attributes.Method)
                || Editor.ServiceAuthoringSemantics.IsImplicitMethod(method))
            .OrderBy(SourcePath, StringComparer.Ordinal)
            .ThenBy(SourceStart)
            .ToArray();
        if (methods.Length == 0)
        {
            throw Failure("rpc-authoring.service-invalid", $"RPC service '{service.Name}' declares no RPC methods.", service);
        }
        var overloadedMethod = methods.GroupBy(static method => method.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (overloadedMethod is not null)
        {
            throw Failure(
                "rpc-authoring.method-invalid",
                $"RPC method overloads named '{overloadedMethod.Key}' are outside the V1 source profile.",
                overloadedMethod.Cast<ISymbol>().ToArray());
        }

        var definitions = new DefinitionGraph(sourceTrees, attributes, input.ServiceReferenceTargets, input.ImportedModels, exposure);
        var methodModels = methods.Select(method => CompileMethod(
            method,
            canonicalServiceName.Pascal,
            definitions,
            attributes)).ToArray();
        var errors = CompileErrors(service, definitions, attributes);
        foreach (var argument in serviceAttribute.NamedArguments.Where(pair => pair.Key == nameof(SunderServiceAttribute.AdditionalDefinitions)))
        {
            if (argument.Value.Kind != TypedConstantKind.Array || argument.Value.IsNull || argument.Value.Values.Length > 256)
                throw Failure("rpc-authoring.value-invalid", "AdditionalDefinitions must be a non-null array of at most 256 ordinary source value types.", service);
            var retained = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var value in argument.Value.Values)
            {
                if (value.Value is not ITypeSymbol type || !retained.Add(type))
                    throw Failure("rpc-authoring.value-invalid", "AdditionalDefinitions must contain distinct ordinary source value types.", service);
                definitions.GetDefinition(type, service);
            }
        }
        var methodIds = new Dictionary<string, SunderRpcCSharpMethodModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var method in methodModels)
        {
            if (methodIds.TryGetValue(method.MethodId, out var existingMethod))
            {
                throw Failure(
                    "rpc-authoring.identity-collision",
                    $"RPC service '{service.Name}' produces colliding method identity '{method.MethodId}'.",
                    existingMethod.Source,
                    method.Source);
            }
            methodIds.Add(method.MethodId, method);
        }

        var descriptorJson = BuildDescriptorJson(
            contractId,
            contractVersion,
            serviceId,
            methodModels,
            errors,
            definitions.Definitions,
            serviceAttribute.NamedArguments.Any(pair => pair.Key == nameof(SunderServiceAttribute.IncludeSchemaDialect) && pair.Value.Value is true));
        SunderRpcContractDescriptor descriptor;
        try
        {
            descriptor = SunderRpcContractDescriptor.Parse(descriptorJson);
        }
        catch (SunderRpcDescriptorException exception)
        {
            throw Failure(
                "rpc-authoring.value-invalid",
                $"RPC service '{service.Name}' produced an invalid V1 descriptor: {exception.Message}",
                service);
        }

        var providers = service.GetAttributes()
            .Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributes.Provider))
            .Where(attribute => attribute.ConstructorArguments.Length == 0
                || attribute.ConstructorArguments[0].Value is INamedTypeSymbol ownService && SymbolEqualityComparer.Default.Equals(ownService, service))
            .Select(attribute => CompileProvider(attribute, descriptor.ContractId, serviceId, exposure, service, service, sourceTrees))
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();
        if (providers.Length == 0 && service.TypeKind == TypeKind.Class)
        {
            ValidateGeneratedProviderMembers(service, sourceTrees, input.ServiceHandlerFactoryTypes);
            var providerId = $"{descriptor.ContractId}.provider";
            if (providerId.Length > MaximumIdentityCharacters)
            {
                throw Failure(
                    "rpc-authoring.source-limit-exceeded",
                    $"Derived RPC provider identity '{providerId}' exceeds {MaximumIdentityCharacters} characters.",
                    service);
            }
            providers =
            [
                new(
                    providerId,
                    exposure == SunderServiceExposure.Package
                        ? SunderServiceAudience.Owner
                        : SunderServiceAudience.Public,
                    serviceId,
                    service),
            ];
        }
        return new(
            contractKey,
            exposure,
            Editor.GeneratedPackageNames.Transport(input.PackageId, contractId),
            descriptor,
            providers,
            service,
            methodModels)
        {
            PackageId = input.PackageId,
            SourceTypes = definitions.GetSourceTypes(descriptor),
            SourceServiceReferences = definitions.ServiceReferences,
            ImportedValueLayouts = definitions.ImportedLayouts,
            ImportedOptionalServiceReferenceMembers = definitions.ImportedOptionalServiceReferenceMembers,
            UsesDateTimeOffset = definitions.UsesDateTimeOffset,
            AppUse = CompileAppUsePolicy(service, descriptor, attributes.AppUse, exposure),
            RuntimeUse = CompileUsePolicy(service, descriptor, attributes.RuntimeUse, "runtime"),
            MethodPolicies = methodModels.Any(method => method.TimeoutSeconds != 0)
                ? new(new Dictionary<string, IReadOnlyDictionary<string, Sunder.Package.Format.SunderRpcMethodPolicy>>(StringComparer.Ordinal)
                { [serviceId] = methodModels.Where(method => method.TimeoutSeconds != 0).ToDictionary(method => method.MethodId,
                    method => new Sunder.Package.Format.SunderRpcMethodPolicy(method.TimeoutSeconds), StringComparer.Ordinal) })
                : Sunder.Package.Format.SunderRpcMethodPolicies.Empty,
        };
    }

    private static SunderRpcOwnedUsePolicy? CompileAppUsePolicy(INamedTypeSymbol service, SunderRpcContractDescriptor descriptor,
        INamedTypeSymbol attribute, SunderServiceExposure exposure)
    {
        var declaration = GetAttribute(service, attribute);
        if (declaration?.NamedArguments.Any(pair => pair.Key == nameof(SunderAppUseAttribute.Enabled) && pair.Value.Value is false) == true)
        {
            if (exposure != SunderServiceExposure.Public
                || declaration.NamedArguments.Any(pair => pair.Key == nameof(SunderAppUseAttribute.Required) && pair.Value.Value is true
                    || pair.Key == nameof(SunderAppUseAttribute.Actions)))
                throw Failure("rpc-authoring.app-use-invalid", "Only public services may disable App use, without required readiness or actions.", service);
            return null;
        }
        return CompileUsePolicy(service, descriptor, attribute, "app")
            ?? (service.TypeKind == TypeKind.Class && exposure == SunderServiceExposure.Public
                ? SunderRpcAuthoringDocuments.ValidateUsePolicy(descriptor, required: false, actions: null, "app") : null);
    }

    private static SunderRpcOwnedUsePolicy? CompileUsePolicy(INamedTypeSymbol service, SunderRpcContractDescriptor descriptor, INamedTypeSymbol attribute, string role)
    {
        var declarations = service.GetAttributes().Where(value => SymbolEqualityComparer.Default.Equals(value.AttributeClass, attribute)
            && value.ConstructorArguments.Length == 0).ToArray();
        if (declarations.Length > 1) throw Failure($"rpc-authoring.{role}-use-invalid", "Declare the owning service use policy only once.", service);
        var declaration = declarations.SingleOrDefault();
        if (declaration is null) return null;
        return CompileUsePolicy(service, descriptor, declaration, role);
    }

    private static SunderRpcOwnedUsePolicy CompileUsePolicy(INamedTypeSymbol service, SunderRpcContractDescriptor descriptor, AttributeData declaration, string role)
    {
        var required = declaration.NamedArguments.Any(pair => pair.Key == nameof(SunderAppUseAttribute.Required) && pair.Value.Value is true);
        string[]? actions = null;
        foreach (var argument in declaration.NamedArguments.Where(pair => pair.Key == nameof(SunderAppUseAttribute.Actions)))
        {
            if (argument.Value.IsNull || argument.Value.Kind != TypedConstantKind.Array || argument.Value.Values.Length is < 1 or > 3
                || argument.Value.Values.Any(value => value.Value is not string))
                throw Failure($"rpc-authoring.{role}-use-invalid", "Consumer actions must be a non-null, nonempty array of at most three action names.", service);
            actions = argument.Value.Values.Select(value => (string)value.Value!).ToArray();
        }
        try { return SunderRpcAuthoringDocuments.ValidateUsePolicy(descriptor, required, actions, role); }
        catch (InvalidDataException exception) { throw Failure($"rpc-authoring.{role}-use-invalid", exception.Message, service); }
    }

    private static SunderRpcCSharpMethodModel CompileMethod(
        IMethodSymbol method,
        string serviceDefinitionKey,
        DefinitionGraph definitions,
        AttributeSymbols attributes)
    {
        if (method.MethodKind != MethodKind.Ordinary
            || method.IsStatic
            || method.DeclaredAccessibility != Accessibility.Public)
        {
            throw Failure(
                "rpc-authoring.method-invalid",
                $"RPC method '{method.ToDisplayString()}' must be a public, non-static, non-generic ordinary method.",
                method);
        }
        if (method.IsGenericMethod)
        {
            throw Failure(
                "rpc-authoring.unsupported-source",
                $"Generic RPC method '{method.ToDisplayString()}' is outside the V1 source profile.",
                method);
        }

        var (kind, outputType, isAsyncShape) = UnwrapOutput(method.ReturnType, method);
        var methodAttribute = GetAttribute(method, attributes.Method);
        var terminalSuffix = isAsyncShape ? "Async" : null;
        if (!SunderRpcCSharpNaming.TryCreate(method.Name, terminalSuffix, out var methodName))
        {
            throw Failure("rpc-authoring.method-invalid", $"RPC method name '{method.Name}' cannot produce a canonical wire identity.", method);
        }

        var wireParameters = method.Parameters
            .Where(parameter => !IsCancellationToken(parameter.Type) && !IsInvocationContext(parameter.Type))
            .ToArray();
        var foundContext = false;
        var foundCancellation = false;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                throw Failure("rpc-authoring.method-invalid", $"RPC parameter '{parameter.Name}' cannot use ref, in, or out.", parameter);
            }
            if (IsInvocationContext(parameter.Type))
            {
                if (foundContext || foundCancellation)
                {
                    throw Failure("rpc-authoring.method-invalid", $"RPC invocation context has an invalid position on method '{method.Name}'.", parameter);
                }
                foundContext = true;
            }
            else if (IsCancellationToken(parameter.Type))
            {
                if (foundCancellation)
                {
                    throw Failure("rpc-authoring.method-invalid", $"RPC method '{method.Name}' has more than one cancellation token.", parameter);
                }
                foundCancellation = true;
            }
            else if (foundCancellation || parameter.IsParams)
            {
                throw Failure("rpc-authoring.method-invalid", $"RPC wire parameter '{parameter.Name}' has an invalid shape or position.", parameter);
            }
        }

        var requestOverride = GetNamedString(methodAttribute, nameof(SunderMethodAttribute.RequestDefinitionName));
        var outputOverride = GetNamedString(methodAttribute, nameof(SunderMethodAttribute.OutputDefinitionName));
        var requestDefinition = wireParameters.Length switch
        {
            0 => definitions.GetUnitDefinition(),
            1 => definitions.GetRootDefinition(wireParameters[0].Type, wireParameters[0], requestOverride ?? $"{serviceDefinitionKey}{methodName!.Pascal}Request"),
            _ => definitions.AddSyntheticRequest(
                requestOverride ?? $"{serviceDefinitionKey}{methodName!.Pascal}Request",
                wireParameters,
                method),
        };
        var outputDefinition = outputType is null ? definitions.GetUnitDefinition()
            : definitions.GetRootDefinition(outputType, method, outputOverride ?? $"{serviceDefinitionKey}{methodName!.Pascal}{(kind == "server-stream" ? "Event" : "Result")}");
        if (wireParameters.Length <= 1
            && requestOverride is not null
            && !string.Equals(requestOverride, requestDefinition, StringComparison.Ordinal)
            || outputOverride is not null && !string.Equals(outputOverride, outputDefinition, StringComparison.Ordinal))
        {
            throw Failure(
                "rpc-authoring.value-invalid",
                $"RPC method '{method.Name}' definition-name overrides must match the reachable DTO declaration in this compiler slice.",
                method);
        }

        var idempotencyValue = GetNamedInt(methodAttribute, nameof(SunderMethodAttribute.Idempotency));
        var idempotencyKind = (SunderRpcMethodIdempotency)idempotencyValue;
        if (!Enum.IsDefined(idempotencyKind))
        {
            throw Failure(
                "rpc-authoring.method-invalid",
                $"RPC method '{method.Name}' has an invalid idempotency value.",
                method);
        }
        var idempotency = idempotencyKind == SunderRpcMethodIdempotency.Idempotent ? "idempotent" : "none";
        var errors = GetNamedStrings(methodAttribute, nameof(SunderMethodAttribute.Errors));
        var timeoutSeconds = GetNamedInt(methodAttribute, nameof(SunderMethodAttribute.TimeoutSeconds));
        if (timeoutSeconds is < 0 or > Sunder.Package.Format.SunderRpcMethodPolicies.MaximumTimeoutSeconds)
            throw Failure("rpc-authoring.method-invalid", "TimeoutSeconds must be zero (Host default) or an integer in 1..2100; Host policy still clamps the requested deadline.", method);
        return new(
            GetNamedString(methodAttribute, nameof(SunderMethodAttribute.MethodId)) ?? methodName!.LowerKebab,
            kind,
            requestDefinition,
            outputDefinition,
            idempotency,
            errors,
            method) { TimeoutSeconds = timeoutSeconds };
    }

    private static IReadOnlyList<ErrorModel> CompileErrors(
        INamedTypeSymbol service,
        DefinitionGraph definitions,
        AttributeSymbols attributes)
    {
        var errors = new List<ErrorModel>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in service.GetAttributes()
                     .Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributes.DomainError)))
        {
            var code = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;
            if (!codes.Add(code))
            {
                throw Failure("rpc-authoring.service-invalid", $"RPC service '{service.Name}' declares error '{code}' more than once.", service);
            }
            var detailsType = GetNamedType(attribute, nameof(SunderServiceErrorAttribute.DetailsType));
            errors.Add(new(
                code,
                GetNamedString(attribute, nameof(SunderServiceErrorAttribute.Description)),
                detailsType is null ? null : definitions.GetDefinition(detailsType, service)));
        }
        return errors;
    }

    private static SunderRpcCSharpProviderModel CompileProvider(
        AttributeData attribute,
        string contractId,
        string serviceId,
        SunderServiceExposure exposure,
        INamedTypeSymbol providerType,
        INamedTypeSymbol service,
        IReadOnlySet<SyntaxTree> sourceTrees)
    {
        if (attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Value is ITypeSymbol explicitService
            && !SymbolEqualityComparer.Default.Equals(explicitService, service))
        {
            throw Failure(
                "rpc-authoring.provider-invalid",
                $"RPC provider shorthand on '{providerType.Name}' may only target service '{service.Name}'.",
                providerType);
        }
        ValidateGeneratedProviderMembers(providerType, sourceTrees);

        var audienceValue = GetNamedInt(attribute, nameof(SunderServiceProviderAttribute.Audience));
        var audience = (SunderServiceAudience)audienceValue;
        if (audience == SunderServiceAudience.Inferred)
        {
            audience = exposure == SunderServiceExposure.Package
                ? SunderServiceAudience.Owner
                : SunderServiceAudience.Public;
        }
        if (audience is not (SunderServiceAudience.Owner or SunderServiceAudience.Public))
        {
            throw Failure("rpc-authoring.provider-invalid", $"RPC provider on '{providerType.Name}' has an invalid audience.", providerType);
        }
        if (exposure == SunderServiceExposure.Package && audience == SunderServiceAudience.Public)
        {
            throw FailureAtLocations(
                "rpc-authoring.provider-invalid",
                $"Package-exposed RPC service '{service.Name}' cannot have a public provider audience.",
                [GetNamedArgumentValueLocation(attribute, nameof(SunderServiceProviderAttribute.Audience))
                 ?? SourceLocation(providerType)]);
        }

        var providerId = GetNamedString(attribute, nameof(SunderServiceProviderAttribute.ProviderId)) ?? $"{contractId}.provider";
        if (providerId.Length > MaximumIdentityCharacters)
        {
            throw Failure(
                "rpc-authoring.source-limit-exceeded",
                $"Derived RPC provider identity '{providerId}' exceeds {MaximumIdentityCharacters} characters.",
                providerType);
        }
        if (!PackageId.TryParse(providerId, out _))
        {
            throw Failure(
                "rpc-authoring.provider-invalid",
                $"RPC provider identity '{providerId}' is not canonical.",
                providerType);
        }
        return new(
            providerId,
            audience,
            serviceId,
            providerType);
    }

    private static void ValidateStandaloneProvider(
        INamedTypeSymbol provider,
        INamedTypeSymbol service,
        IReadOnlySet<SyntaxTree> sourceTrees,
        INamedTypeSymbol methodAttribute,
        bool imported = false)
    {
        ValidateProviderClass(provider, sourceTrees);
        if (service.TypeKind == TypeKind.Interface
            ? !provider.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, service))
            : !InheritsFrom(provider, service))
        {
            throw Failure(
                imported ? "rpc-authoring.handler-incomplete" : "rpc-authoring.provider-invalid",
                $"RPC provider '{provider.Name}' does not implement service '{service.Name}'.",
                provider);
        }
        if (service.TypeKind == TypeKind.Interface)
        {
            IEnumerable<INamedTypeSymbol> interfaces = imported ? service.AllInterfaces.Prepend(service) : [service];
            foreach (var method in interfaces.SelectMany(static type => type.GetMembers().OfType<IMethodSymbol>()))
            {
                if (!imported && !HasAttribute(method, methodAttribute)
                    && !Editor.ServiceAuthoringSemantics.IsImplicitMethod(method)) continue;
                if (provider.FindImplementationForInterfaceMember(method) is not IMethodSymbol { IsAbstract: false, IsStatic: false } implementation
                    || imported && !SymbolEqualityComparer.Default.Equals(implementation.ReturnType, method.ReturnType))
                {
                    throw Failure(
                        "rpc-authoring.handler-incomplete",
                        $"RPC provider '{provider.Name}' does not implement service method '{method.Name}'.",
                        provider);
                }
            }
        }
    }

    private static void ValidateProviderClass(INamedTypeSymbol provider, IReadOnlySet<SyntaxTree> sourceTrees)
    {
        if (provider.TypeKind != TypeKind.Class
            || provider.IsAbstract
            || provider.IsRecord
            || provider.IsGenericType
            || provider.IsFileLocal
            || provider.ContainingType is not null
            || !HasOrdinarySourceDeclaration(provider, sourceTrees)
            || !IsPartialType(provider))
        {
            throw Failure(
                "rpc-authoring.provider-invalid",
                $"RPC provider '{provider.ToDisplayString()}' must be a concrete, non-generic partial class in ordinary source.",
                provider);
        }
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol expectedBase)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, expectedBase))
            {
                return true;
            }
        }
        return false;
    }

    private static void ValidateGeneratedProviderMembers(
        INamedTypeSymbol providerType,
        IReadOnlySet<SyntaxTree> sourceTrees,
        IReadOnlySet<string>? serviceHandlerFactoryTypes = null)
    {
        var factories = providerType.GetMembers("CreateSunderRpcServiceHandler");
        if (providerType.GetMembers().Any(member =>
                IsGeneratedProviderMember(member.Name)
                && HasOrdinarySourceDeclaration(member, sourceTrees))
            || factories.Any(member => HasOrdinarySourceDeclaration(member, sourceTrees)
                && (member is not IMethodSymbol method || !IsServiceHandlerFactoryDeclaration(method)
                    || method.PartialImplementationPart is { } implementation
                        && HasOrdinarySourceDeclaration(implementation, sourceTrees)))
            || serviceHandlerFactoryTypes?.Contains(providerType.ToDisplayString()) == true
                && (factories.Length != 1 || factories[0] is not IMethodSymbol factory
                    || !IsServiceHandlerFactoryDeclaration(factory)))
        {
            throw Failure(
                "rpc-authoring.generated-name-collision",
                $"RPC provider '{providerType.Name}' requires an unimplemented internal partial service-handler factory declaration and must not collide with reserved generated members.",
                providerType);
        }
    }

    private static bool IsGeneratedProviderMember(string name)
        => new[] { "CreateSunderRpcProviderRegistration", "CreateSunderRpcProviderRegistrationForServices", "CreateSunderRpcProviderRegistrations",
                "CreateSunderRpcProviderRegistrationsForServices", "__CreateSunderRpcProviderRegistration", "DeclareSunderRpcProvider",
                "__SunderGeneratedRpcHandler", "__SunderNaturalRpcHandler", "__SunderGeneratedRpcDeclaration" }
            .Any(reserved => name == reserved || name.StartsWith(reserved + "_", StringComparison.Ordinal));

    private static (string Kind, ITypeSymbol? OutputType, bool IsAsyncShape) UnwrapOutput(
        ITypeSymbol returnType,
        IMethodSymbol method)
    {
        if (returnType is INamedTypeSymbol named)
        {
            var namespaceName = named.ContainingNamespace.ToDisplayString();
            if (namespaceName == "System.Threading.Tasks" && named is { Arity: 0, Name: "Task" or "ValueTask" })
                return ("unary", null, true);
            if (namespaceName == "System.Threading.Tasks"
                && named is { Arity: 1, Name: "Task" or "ValueTask" })
            {
                return ("unary", named.TypeArguments[0], true);
            }
            if (namespaceName == "System.Collections.Generic"
                && named is { Arity: 1, Name: "IAsyncEnumerable" })
            {
                return ("server-stream", named.TypeArguments[0], true);
            }
        }

        throw Failure(
            "rpc-authoring.method-invalid",
            $"Service method '{method.Name}' must return Task, ValueTask, Task<T>, ValueTask<T>, or IAsyncEnumerable<T>.",
            method);
    }

    private static byte[] BuildDescriptorJson(
        string contractId,
        string version,
        string serviceId,
        IReadOnlyList<SunderRpcCSharpMethodModel> methods,
        IReadOnlyList<ErrorModel> errors,
        IReadOnlyDictionary<string, JsonObject> definitions,
        bool includeSchemaDialect)
    {
        var definitionObject = new JsonObject();
        foreach (var definition in definitions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            definitionObject.Add(definition.Key, definition.Value.DeepClone());
        }

        var methodObject = new JsonObject();
        foreach (var method in methods.OrderBy(static method => method.MethodId, StringComparer.Ordinal))
        {
            var value = new JsonObject
            {
                ["errors"] = new JsonArray(method.Errors.Select(static error => JsonValue.Create(error)).ToArray()),
                [method.Kind == "unary" ? "responseSchema" : "eventSchema"] = Reference(method.OutputDefinition),
                ["idempotency"] = method.Idempotency,
                ["kind"] = method.Kind,
                ["requestSchema"] = Reference(method.RequestDefinition),
            };
            methodObject.Add(method.MethodId, value);
        }

        var errorObject = new JsonObject();
        foreach (var error in errors.OrderBy(static error => error.Code, StringComparer.Ordinal))
        {
            var value = new JsonObject();
            if (error.Description is not null)
            {
                value.Add("description", error.Description);
            }
            if (error.DetailsDefinition is not null)
            {
                value.Add("detailsSchema", Reference(error.DetailsDefinition));
            }
            errorObject.Add(error.Code, value);
        }

        var descriptor = new JsonObject
        {
            ["$defs"] = definitionObject,
            ["contractId"] = contractId,
            ["descriptorVersion"] = 1,
            ["errors"] = errorObject,
            ["schemaProfile"] = SunderRpcProtocol.ValueSchemaProfile,
            ["services"] = new JsonObject
            {
                [serviceId] = new JsonObject
                {
                    ["methods"] = methodObject,
                },
            },
            ["version"] = version,
        };
        if (includeSchemaDialect) descriptor["$schema"] = SunderRpcProtocol.JsonSchemaDialect;
        return Encoding.UTF8.GetBytes(descriptor.ToJsonString(new JsonSerializerOptions { WriteIndented = false, MaxDepth = 256 }));
    }

    private static JsonObject Reference(string definitionName) => new() { ["$ref"] = $"#/$defs/{definitionName}" };

    private static AttributeSymbols ResolveAttributes(CSharpCompilation compilation)
        => new(
            ResolveAttribute<SunderPackageAttribute>(compilation),
            ResolveAttribute<SunderServiceAttribute>(compilation),
            ResolveAttribute<SunderAppUseAttribute>(compilation),
            ResolveAttribute<SunderRuntimeUseAttribute>(compilation),
            ResolveAttribute<SunderServiceProviderAttribute>(compilation),
            ResolveAttribute<SunderPlatformImportAttribute>(compilation),
            ResolveAttribute<SunderMethodAttribute>(compilation),
            ResolveAttribute<SunderServiceErrorAttribute>(compilation),
            ResolveAttribute<SunderValueAttribute>(compilation),
            ResolveAttribute<SunderServiceMemberAttribute>(compilation),
            ResolveAttribute<SunderTransparentValueAttribute>(compilation),
            ResolveAttribute<SunderNumericEnumAttribute>(compilation),
            ResolveAttribute<SunderUnionAttribute>(compilation),
            ResolveAttribute<SunderDiscriminatorAttribute>(compilation),
            ResolveAttribute<SunderVariantAttribute>(compilation),
            ResolveAttribute<SunderValueConstraintsAttribute>(compilation),
            compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPropertyNameAttribute"),
            compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute"));

    private static INamedTypeSymbol ResolveAttribute<TAttribute>(CSharpCompilation compilation)
    {
        var metadataName = typeof(TAttribute).FullName!;
        var symbol = compilation.GetTypeByMetadataName(metadataName);
        if (symbol is null
            || !string.Equals(symbol.ContainingAssembly.Identity.Name, SdkAssemblyIdentity.Name, StringComparison.Ordinal)
            || symbol.ContainingAssembly.Identity.Version != SdkAssemblyIdentity.Version)
        {
            throw new CompilerFailure(new(
                "rpc-authoring.unsupported-source",
                $"The compilation must reference the exact Sunder.Sdk assembly that defines '{metadataName}'. Expected {SdkAssemblyIdentity.Name}, Version={SdkAssemblyIdentity.Version}; resolved {symbol?.ContainingAssembly.Identity.ToString() ?? "no unambiguous SDK symbol"}.",
                null,
                null,
                null));
        }
        return symbol;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute)
        => symbol.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute));

    private static AttributeData? GetAttribute(ISymbol symbol, INamedTypeSymbol attribute)
    {
        var matches = symbol.GetAttributes().Where(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute)).ToArray();
        if (matches.Length > 1) throw Failure("rpc-authoring.service-invalid", $"'{symbol.Name}' repeats semantic metadata '{attribute.Name}', including migration aliases.", symbol);
        return matches.FirstOrDefault();
    }

    private static string? GetNamedString(AttributeData? attribute, string name)
        => attribute?.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string;

    private static int GetNamedInt(AttributeData? attribute, string name)
    {
        var value = attribute?.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value;
        return value is int number ? number : 0;
    }

    private static ITypeSymbol? GetNamedType(AttributeData attribute, string name)
        => attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as ITypeSymbol;

    private static IReadOnlyList<string> GetNamedStrings(AttributeData? attribute, string name)
    {
        if (attribute is null) return [];
        var value = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value;
        return value.Kind == TypedConstantKind.Array
            ? value.Values.Select(static item => item.Value as string ?? string.Empty).ToArray()
            : [];
    }

    private static string? ResolvePackageId(
        string suppliedPackageId,
        CSharpCompilation compilation,
        INamedTypeSymbol packageAttribute,
        IReadOnlyList<INamedTypeSymbol> services,
        ICollection<SunderRpcCSharpCompilerDiagnostic> diagnostics)
    {
        var declaration = compilation.Assembly.GetAttributes()
            .SingleOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, packageAttribute));
        var declaredPackageId = declaration is null
            ? null
            : GetNamedString(declaration, nameof(SunderPackageAttribute.Id));
        if (declaredPackageId is not null && !PackageId.TryParse(declaredPackageId, out _))
        {
            diagnostics.Add(new(
                "rpc-authoring.service-invalid",
                $"The Sunder package attribute declares noncanonical package ID '{declaredPackageId}'.",
                declaration!.ApplicationSyntaxReference?.SyntaxTree.FilePath,
                null,
                null));
            return null;
        }
        if (!string.IsNullOrWhiteSpace(suppliedPackageId)
            && declaredPackageId is not null
            && !string.Equals(suppliedPackageId, declaredPackageId, StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                "rpc-authoring.service-invalid",
                $"Supplied package ID '{suppliedPackageId}' does not match source package ID '{declaredPackageId}'.",
                declaration!.ApplicationSyntaxReference?.SyntaxTree.FilePath,
                null,
                null));
            return null;
        }

        var packageId = string.IsNullOrWhiteSpace(suppliedPackageId) ? declaredPackageId : suppliedPackageId;
        if (packageId is null && services.Count != 0)
        {
            var location = SourceLocation(services[0]);
            var span = location.IsInSource ? ToSourceSpan(location) : null;
            diagnostics.Add(new(
                "rpc-authoring.service-invalid",
                "Source-authored RPC services require an exact [assembly: SunderPackage] package identity.",
                span?.Path,
                span?.StartLine,
                span?.StartColumn,
                span?.EndLine,
                span?.EndColumn));
            return null;
        }
        return packageId ?? string.Empty;
    }

    private static int NormalizedSourceByteCount(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        return checked(Encoding.UTF8.GetByteCount(normalized) + 1);
    }

    private static bool IsCancellationToken(ITypeSymbol type)
        => type.Name == nameof(CancellationToken)
           && type.ContainingNamespace.ToDisplayString() == "System.Threading";

    private static bool IsInvocationContext(ITypeSymbol type)
        => type.Name == nameof(SunderRpcInvocationContext)
           && type.ContainingNamespace.ToDisplayString() == "Sunder.Sdk.Rpc";

    private static bool IsOrdinarySourceSymbol(ISymbol symbol, IReadOnlySet<SyntaxTree> sourceTrees)
        => symbol.DeclaringSyntaxReferences.Length > 0
           && symbol.DeclaringSyntaxReferences.All(reference => sourceTrees.Contains(reference.SyntaxTree));

    private static bool HasOrdinarySourceDeclaration(ISymbol symbol, IReadOnlySet<SyntaxTree> sourceTrees)
        => symbol.DeclaringSyntaxReferences.Any(reference => sourceTrees.Contains(reference.SyntaxTree));

    private static bool IsPartialType(INamedTypeSymbol type)
        => type.DeclaringSyntaxReferences.All(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration
            && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

    private static string SourcePath(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(static location => location.IsInSource)?.SourceTree?.FilePath ?? string.Empty;

    private static int SourceStart(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(static location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue;

    private static SunderRpcCSharpCompilerDiagnostic FromRoslynDiagnostic(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        return new(
            "rpc-authoring.unsupported-source",
            $"{diagnostic.Id}: {diagnostic.GetMessage()}",
            diagnostic.Location.IsInSource ? lineSpan.Path : null,
            diagnostic.Location.IsInSource ? lineSpan.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? lineSpan.StartLinePosition.Character + 1 : null,
            diagnostic.Location.IsInSource ? lineSpan.EndLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? lineSpan.EndLinePosition.Character + 1 : null);
    }

    private static bool IsExpectedGeneratedMemberDiagnostic(
        Diagnostic diagnostic, SunderRpcCSharpCompilerInput input, CSharpCompilation compilation)
    {
        if (!diagnostic.Location.IsInSource)
        {
            return false;
        }
        var node = diagnostic.Location.SourceTree?.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        if (diagnostic.Id == "CS8795"
            && node?.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } declaration
            && compilation.GetSemanticModel(declaration.SyntaxTree).GetDeclaredSymbol(declaration) is { } method)
        {
            if (IsSdkGeneratedRegexDefinition(method, compilation)) return true;
            return !input.RequireGeneratedMembersResolved
                && input.ServiceHandlerFactoryTypes?.Contains(method.ContainingType.ToDisplayString()) == true
                && IsServiceHandlerFactoryDeclaration(method);
        }
        if (input.RequireGeneratedMembersResolved || input.ServiceHandlerFactoryTypes is not null || node is not SimpleNameSyntax name) return false;
        if (diagnostic.Id == "CS1061" && name.Identifier.ValueText == "CreateSunderRpcProviderRegistration") return true;
        if (diagnostic.Id != "CS0117" || name.Identifier.ValueText != "DeclareSunderRpcProvider"
            || name.Parent is not MemberAccessExpressionSyntax access) return false;
        var receiver = compilation.GetSemanticModel(access.SyntaxTree).GetSymbolInfo(access.Expression).Symbol;
        if (receiver is IAliasSymbol alias) receiver = alias.Target;
        return receiver is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type
            && (HasAttribute(type, ResolveAttribute<SunderServiceProviderAttribute>(compilation))
                || HasAttribute(type, ResolveAttribute<SunderServiceAttribute>(compilation)));
    }

    private static bool IsServiceHandlerFactoryDeclaration(IMethodSymbol method)
        => method is { Name: "CreateSunderRpcServiceHandler", IsPartialDefinition: true, IsStatic: false,
            DeclaredAccessibility: Accessibility.Internal, Arity: 0, Parameters.Length: 0,
            ReturnsByRef: false, ReturnsByRefReadonly: false, IsVirtual: false, IsAbstract: false, IsExtern: false }
            && method.ReturnNullableAnnotation != NullableAnnotation.Annotated
            && method.ReturnType.ToDisplayString() == "Sunder.Sdk.Rpc.ISunderRpcServiceHandler"
            && method.ReturnType.ContainingAssembly.Identity.ToString() == SdkAssemblyIdentity.FullName
            && method.DeclaringSyntaxReferences.All(reference => reference.GetSyntax() is MethodDeclarationSyntax
                { Body: null, ExpressionBody: null });

    private static CompilerFailure Failure(string id, string message, params ISymbol[] symbols)
        => FailureAtLocations(id, message, symbols.Select(SourceLocation));

    private static CompilerFailure FailureAtLocations(
        string id,
        string message,
        IEnumerable<Location> locations)
    {
        var spans = locations.Where(static location => location.IsInSource)
            .Select(ToSourceSpan)
            .ToArray();
        var primary = spans.FirstOrDefault();
        return new(new(
            id,
            message,
            primary?.Path,
            primary?.StartLine,
            primary?.StartColumn,
            primary?.EndLine,
            primary?.EndColumn,
            spans.Skip(1).ToArray()));
    }

    private static Location SourceLocation(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(static location => location.IsInSource) ?? Location.None;

    private static SunderRpcCSharpSourceSpan ToSourceSpan(Location location)
    {
        var lineSpan = location.GetLineSpan();
        return new(
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1);
    }

    private static Location? GetNamedArgumentValueLocation(AttributeData attribute, string argumentName)
    {
        if (attribute.ApplicationSyntaxReference?.GetSyntax() is not AttributeSyntax syntax)
        {
            return null;
        }
        var expression = syntax.ArgumentList?.Arguments
            .FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == argumentName)
            ?.Expression;
        return expression is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Name.GetLocation()
            : expression?.GetLocation();
    }

    private static Location DefinitionIdentityLocation(INamedTypeSymbol type, AttributeData? attribute)
    {
        if (attribute?.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax
            {
                ArgumentList.Arguments.Count: > 0,
            } syntax
            && syntax.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression)
            && literal.Span.Length >= 2)
        {
            return Location.Create(literal.SyntaxTree, new(literal.Span.Start + 1, literal.Span.Length - 2));
        }
        return SourceLocation(type);
    }

    private sealed record AttributeSymbols(
        INamedTypeSymbol Package,
        INamedTypeSymbol Service,
        INamedTypeSymbol AppUse,
        INamedTypeSymbol RuntimeUse,
        INamedTypeSymbol Provider,
        INamedTypeSymbol PlatformImport,
        INamedTypeSymbol Method,
        INamedTypeSymbol DomainError,
        INamedTypeSymbol Definition,
        INamedTypeSymbol ServiceMember,
        INamedTypeSymbol Transparent,
        INamedTypeSymbol NumericEnum,
        INamedTypeSymbol Union,
        INamedTypeSymbol Discriminator,
        INamedTypeSymbol Variant,
        INamedTypeSymbol Facet,
        INamedTypeSymbol? JsonPropertyName,
        INamedTypeSymbol? JsonStringEnumMemberName);

    private sealed record ErrorModel(string Code, string? Description, string? DetailsDefinition);

    private sealed partial class DefinitionGraph(
        IReadOnlySet<SyntaxTree> sourceTrees,
        AttributeSymbols attributes,
        IReadOnlyDictionary<string, Sunder.Package.Format.SunderRpcServiceReferenceProjection> serviceTargets,
        IReadOnlyDictionary<INamedTypeSymbol, SunderRpcCSharpImportedModel> importedModels,
        SunderServiceExposure exposure)
    {
        private readonly Dictionary<string, JsonObject> _definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<ITypeSymbol, string> _typeNames = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, ISymbol> _definitionOwners = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ITypeSymbol, SunderRpcCSharpSourceSyntheticTagModel> _taggedVariants = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, SunderRpcCSharpSourceTypeModel> _sourceTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Sunder.Package.Format.SunderRpcServiceReferenceProjection> _serviceReferences = new(StringComparer.Ordinal);
        internal IReadOnlyDictionary<string, Sunder.Package.Format.SunderRpcServiceReferenceProjection> ServiceReferences => _serviceReferences;

        public IReadOnlyDictionary<string, JsonObject> Definitions => _definitions;

        public bool UsesDateTimeOffset { get; private set; }

        public IReadOnlyList<SunderRpcCSharpSourceTypeModel> GetSourceTypes(SunderRpcContractDescriptor descriptor)
        {
            var nullValue = JsonSerializer.SerializeToElement<object?>(null);
            return _sourceTypes.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value with
                {
                    TransparentValueAcceptsNull = pair.Value.IsTransparent
                        && descriptor.IsValid($"#/$defs/{pair.Key}", nullValue, out _),
                    Union = pair.Value.Union is { } union
                        ? union with { AcceptsNull = descriptor.IsValid(pair.Value.DefinitionReference, nullValue, out _) }
                        : null,
                }).ToArray();
        }

        public string GetUnitDefinition()
        {
            if (!_definitions.ContainsKey("Unit"))
            {
                _definitionOwners.Add("Unit", attributes.Definition);
                _definitions.Add("Unit", ClosedRecord(new JsonObject(), []));
            }
            return "Unit";
        }

        public string AddSyntheticRequest(
            string definitionName,
            IReadOnlyList<IParameterSymbol> parameters,
            IMethodSymbol method)
        {
            ReserveDefinition(definitionName, method);
            var properties = new JsonObject();
            var required = new List<string>(parameters.Count);
            foreach (var parameter in parameters)
            {
                if (!SunderRpcCSharpNaming.TryCreate(parameter.Name, null, out var parameterName))
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"RPC parameter name '{parameter.Name}' is not portable.",
                        parameter);
                }
                var wireName = parameterName!.Camel;
                if (properties.ContainsKey(wireName))
                {
                    throw Failure(
                        "rpc-authoring.identity-collision",
                        $"RPC method '{method.Name}' has colliding parameter identity '{wireName}'.",
                        parameter);
                }
                properties.Add(
                    wireName,
                    MapValueSchema(parameter.Type, parameter, GetAttribute(parameter, attributes.Facet), depth: 2));
                required.Add(wireName);
            }
            _definitions.Add(definitionName, ClosedRecord(properties, required));
            return definitionName;
        }

        public string GetDefinition(ITypeSymbol type, ISymbol source, int depth = 1)
        {
            if (depth > MaximumTraversalDepth)
            {
                throw Failure(
                    "rpc-authoring.source-limit-exceeded",
                    $"RPC source type traversal exceeds the inclusive depth limit of {MaximumTraversalDepth}.",
                    source);
            }
            if (_typeNames.TryGetValue(type, out var existing))
            {
                return existing;
            }
            if (type is INamedTypeSymbol importedType && importedModels.TryGetValue(importedType, out var imported))
            {
                var key = ImportModel(imported, source);
                _typeNames.Add(type, key);
                return key;
            }
            if (type is not INamedTypeSymbol named
                || named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Enum)
                || named.IsGenericType
                || !IsOrdinarySourceSymbol(named, sourceTrees))
            {
                throw Failure(
                    "rpc-authoring.value-invalid",
                    $"RPC value type '{type.ToDisplayString()}' must be a non-generic ordinary source class, record, or struct.",
                    source);
            }

            // Resolve a listed variant's tagged union first, even for concrete-only RPC roots.
            // BuildUnion registers all tags before traversing any variant payloads.
            for (var current = named.BaseType; current is not null; current = current.BaseType)
            {
                if (_typeNames.ContainsKey(current) || !HasAttribute(current, attributes.Discriminator)
                    || GetAttribute(current, attributes.Union) is not { } ancestorUnion) continue;
                var variants = ancestorUnion.ConstructorArguments.FirstOrDefault();
                if (variants.Kind == TypedConstantKind.Array && !variants.IsNull
                    && variants.Values.Any(variant => SymbolEqualityComparer.Default.Equals(variant.Value as ITypeSymbol, named)))
                {
                    GetDefinition(current, source, depth);
                }
            }
            if (_typeNames.TryGetValue(type, out existing)) return existing;

            var definitionAttribute = GetAttribute(named, attributes.Definition);
            var definitionName = definitionAttribute?.ConstructorArguments.FirstOrDefault().Value as string;
            SunderRpcCSharpName? sourceName = null;
            if (definitionName is null && !SunderRpcCSharpNaming.TryCreate(named.Name, null, out sourceName))
            {
                throw Failure("rpc-authoring.value-invalid", $"RPC value type name '{named.Name}' is not portable.", named);
            }
            definitionName ??= sourceName!.Pascal;
            ReserveDefinition(definitionName, named);
            _typeNames.Add(named, definitionName);
            JsonObject schema;
            IReadOnlyList<SunderRpcCSharpSourcePropertyModel> properties = [];
            IReadOnlyList<SunderRpcCSharpSourceEnumMemberModel> enumMembers = [];
            SunderRpcCSharpSourceUnionModel? sourceUnion = null;
            SunderRpcCSharpSourceConstructorModel? construction = null;
            var isTransparent = false;
            var isNumericEnum = false;
            if (named.TypeKind == TypeKind.Enum)
            {
                schema = BuildEnum(named, out enumMembers);
                isNumericEnum = UsesNumericEnum(named);
            }
            else if (GetAttribute(named, attributes.Union) is { } union)
            {
                schema = BuildUnion(named, union, depth, out sourceUnion);
            }
            else if (HasAttribute(named, attributes.Transparent))
            {
                schema = BuildTransparent(named, depth, out var valueProperty);
                isTransparent = true;
                properties = [new(valueProperty, "Value", false, false)];
            }
            else
            {
                schema = BuildRecord(named, depth, out properties, out construction);
            }
            ApplyFacet(schema, GetAttribute(named, attributes.Facet), named);
            _definitions.Add(definitionName, schema);
            _sourceTypes.Add(definitionName, new(
                (INamedTypeSymbol)named.WithNullableAnnotation(NullableAnnotation.NotAnnotated),
                isTransparent,
                isNumericEnum,
                properties)
            {
                DefinitionReference = $"#/$defs/{definitionName}",
                EnumMembers = enumMembers,
                Union = sourceUnion,
                SyntheticTag = _taggedVariants.GetValueOrDefault(named),
                Construction = construction,
            });
            return definitionName;
        }

        public string GetRootDefinition(ITypeSymbol type, ISymbol source, string syntheticName)
        {
            var facet = source is IMethodSymbol method
                ? method.GetReturnTypeAttributes().FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributes.Facet))
                : GetAttribute(source, attributes.Facet);
            if (type.NullableAnnotation != NullableAnnotation.Annotated && type is INamedTypeSymbol { IsGenericType: false } named
                && (IsOrdinarySourceSymbol(named, sourceTrees) || importedModels.ContainsKey(named)) && facet is null) return GetDefinition(type, source);
            ReserveDefinition(syntheticName, source);
            var schema = MapValueSchema(type, source, facet, 1);
            _definitions.Add(syntheticName, schema);
            return syntheticName;
        }

        private JsonObject BuildRecord(
            INamedTypeSymbol named,
            int depth,
            out IReadOnlyList<SunderRpcCSharpSourcePropertyModel> sourceProperties,
            out SunderRpcCSharpSourceConstructorModel? construction)
        {
            if (named.IsAbstract)
            {
                throw Failure(
                    "rpc-authoring.value-invalid",
                    $"Abstract service value type '{named.Name}' must declare SunderUnion.",
                    named);
            }
            var knownProperties = named.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(property => property is { IsStatic: false, DeclaredAccessibility: Accessibility.Public }
                                   && property.GetMethod is not null
                                   && IsOrdinarySourceSymbol(property, sourceTrees))
                .OrderBy(SourcePath, StringComparer.Ordinal)
                .ThenBy(SourceStart)
                .ToArray();
            var properties = knownProperties.Where(IsServiceProperty).ToArray();
            construction = BindRecordConstructor(named, properties, knownProperties);
            var propertyObject = new JsonObject();
            var required = new List<string>();
            var propertyModels = new List<SunderRpcCSharpSourcePropertyModel>(properties.Length);
            if (_taggedVariants.TryGetValue(named, out var taggedVariant))
            {
                propertyObject.Add(
                    taggedVariant.PropertyName,
                    new JsonObject { ["const"] = taggedVariant.Tag, ["type"] = "string" });
                required.Add(taggedVariant.PropertyName);
            }
            foreach (var property in properties)
            {
                var explicitWireName = GetJsonPropertyName(property);
                var wireName = explicitWireName ?? GetDefaultPropertyName(property);
                if (propertyObject.ContainsKey(wireName))
                {
                    throw Failure(
                        "rpc-authoring.identity-collision",
                        $"RPC value type '{named.Name}' has colliding property identity '{wireName}'.",
                        property);
                }
                var (propertyType, isOptional) = UnwrapOptional(property.Type);
                if (!property.IsRequired && !isOptional
                    && !(construction is not null && (construction.Parameters.Contains(property, SymbolEqualityComparer.Default)
                         || named.IsRecord && IsAutoInitProperty(property) || IsExplicitServiceMember(property) && IsAutoInitProperty(property))))
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"RPC property '{named.Name}.{property.Name}' must be declared required, uniquely constructor-bound, or use SunderRpcOptional<T>.",
                        property);
                }
                if (property.IsRequired && isOptional)
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"RPC property '{named.Name}.{property.Name}' cannot be both required and SunderRpcOptional<T>.",
                        property);
                }
                propertyObject.Add(
                    wireName,
                    MapValueSchema(propertyType, property, GetAttribute(property, attributes.Facet), depth + 1));
                propertyModels.Add(new(property, wireName, isOptional, explicitWireName is not null));
                if (!isOptional)
                {
                    required.Add(wireName);
                }
            }
            if (construction is null && propertyModels.Any(static property => property.IsOptional))
            {
                // OnDeserializing is too late to reset values already bound to constructor parameters.
                var jsonConstructors = named.InstanceConstructors.Where(static constructor =>
                    HasSystemAttribute(constructor, "System.Text.Json.Serialization.JsonConstructorAttribute")).ToArray();
                var supportsDirectPopulation = jsonConstructors.Length == 0
                    ? named.IsValueType || named.InstanceConstructors.Any(static constructor =>
                        constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0)
                    : jsonConstructors.Length == 1 && jsonConstructors[0].Parameters.Length == 0;
                if (!supportsDirectPopulation)
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"RPC value type '{named.Name}' with optional members requires a parameterless JSON constructor so absence can be restored before property population; parameterized JSON constructors are not supported for optional members.",
                        named);
                }
                foreach (var property in propertyModels.Where(static property => property.IsOptional))
                {
                    var includesAccessors = HasSystemAttribute(property.Source, "System.Text.Json.Serialization.JsonIncludeAttribute");
                    if (property.Source.IsIndexer
                        || property.Source.GetMethod is not { } getter
                        || property.Source.SetMethod is not { } setter
                        || !includesAccessors && (getter.DeclaredAccessibility != Accessibility.Public
                            || setter.DeclaredAccessibility != Accessibility.Public))
                    {
                        throw Failure(
                            "rpc-authoring.value-invalid",
                            $"Optional RPC property '{named.Name}.{property.Source.Name}' requires a JSON-readable getter and writable setter or init accessor to preserve presence.",
                            property.Source);
                    }
                }
            }
            sourceProperties = propertyModels;
            return ClosedRecord(propertyObject, required);
        }

        private JsonObject BuildTransparent(INamedTypeSymbol named, int depth, out IPropertySymbol valueProperty)
        {
            var properties = named.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(property => property is { IsStatic: false, DeclaredAccessibility: Accessibility.Public }
                                   && property.GetMethod is not null
                                   && IsOrdinarySourceSymbol(property, sourceTrees))
                .ToArray();
            if (properties.Length != 1 || properties[0].Name != "Value" || !properties[0].IsRequired)
            {
                throw Failure(
                    "rpc-authoring.value-invalid",
                    $"Transparent RPC value '{named.Name}' must declare exactly one required public Value property.",
                    named);
            }
            if (named.IsAbstract
                || !named.InstanceConstructors.Any(static constructor => constructor.Parameters.All(static parameter => parameter.IsOptional || parameter.IsParams)
                    && IsAssemblyAccessible(constructor))
                || !IsAssemblyAccessible(properties[0].GetMethod!)
                || properties[0].SetMethod is not { } setter
                || !IsAssemblyAccessible(setter))
            {
                throw Failure(
                    "rpc-authoring.value-invalid",
                    $"Transparent RPC value '{named.Name}' must be concrete with an accessible constructor callable without arguments and accessible Value getter and setter or init accessor for its generated codec.",
                    named);
            }
            valueProperty = properties[0];
            return MapValueSchema(valueProperty.Type, valueProperty, GetAttribute(valueProperty, attributes.Facet), depth + 1);
        }

        private static bool IsAssemblyAccessible(ISymbol symbol)
            => symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

        private JsonObject BuildEnum(
            INamedTypeSymbol named,
            out IReadOnlyList<SunderRpcCSharpSourceEnumMemberModel> sourceMembers)
        {
            var fields = named.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(static field => !field.IsImplicitlyDeclared && field.HasConstantValue)
                .OrderBy(SourcePath, StringComparer.Ordinal)
                .ThenBy(SourceStart)
                .ToArray();
            if (UsesNumericEnum(named))
            {
                if (HasSystemAttribute(named, "System.FlagsAttribute") || fields.Length == 0)
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"Numeric RPC enum '{named.Name}' must be nonempty and cannot declare FlagsAttribute.",
                        named);
                }
                long[] numericValues;
                try
                {
                    numericValues = fields
                        .Select(field => Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture))
                        .ToArray();
                }
                catch (Exception exception) when (exception is OverflowException or InvalidCastException or FormatException)
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"Numeric RPC enum '{named.Name}' contains a value outside the supported integer range.",
                        named);
                }
                if (numericValues.Any(static value => value is < -MaximumSafeInteger or > MaximumSafeInteger)
                    || numericValues.Distinct().Count() != numericValues.Length)
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"Numeric RPC enum '{named.Name}' values must be unique RPC safe integers.",
                        named);
                }
                sourceMembers = fields.Select((field, index) => new SunderRpcCSharpSourceEnumMemberModel(
                    field, null, numericValues[index])).ToArray();
                var values = numericValues
                    .Order()
                    .Select(static value => JsonValue.Create(value))
                    .ToArray();
                var schema = new JsonObject
                {
                    ["enum"] = new JsonArray(values),
                    ["type"] = "integer",
                };
                if (GetAttribute(named, attributes.NumericEnum)?.NamedArguments.Any(pair =>
                        pair.Key == nameof(SunderNumericEnumAttribute.IncludeRangeBounds) && pair.Value.Value is false) != true)
                {
                    schema["maximum"] = values[^1]!.DeepClone();
                    schema["minimum"] = values[0]!.DeepClone();
                }
                return schema;
            }

            if (attributes.JsonStringEnumMemberName is null)
            {
                throw Failure("rpc-authoring.value-invalid", "JsonStringEnumMemberNameAttribute is unavailable.", named);
            }
            var names = new List<JsonNode?>(fields.Length);
            var members = new List<SunderRpcCSharpSourceEnumMemberModel>(fields.Length);
            foreach (var field in fields)
            {
                var attribute = GetAttribute(field, attributes.JsonStringEnumMemberName);
                var value = attribute?.ConstructorArguments.FirstOrDefault().Value as string;
                if (value is null)
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"String RPC enum member '{named.Name}.{field.Name}' requires JsonStringEnumMemberName.",
                        field);
                }
                names.Add(JsonValue.Create(value));
                members.Add(new(field, value, 0));
            }
            if (fields.Select(static field => field.ConstantValue).Distinct().Count() != fields.Length)
            {
                throw Failure(
                    "rpc-authoring.value-invalid",
                    $"String RPC enum '{named.Name}' requires unique CLR values so every explicit wire name can round-trip without aliasing.",
                    named);
            }
            sourceMembers = members;
            return new JsonObject
            {
                ["enum"] = new JsonArray(names.ToArray()),
                ["type"] = "string",
            };
        }

        private JsonObject BuildUnion(
            INamedTypeSymbol named,
            AttributeData union,
            int depth,
            out SunderRpcCSharpSourceUnionModel sourceUnion)
        {
            var declaredVariants = union.ConstructorArguments.FirstOrDefault();
            if (declaredVariants.Kind != TypedConstantKind.Array
                || declaredVariants.IsNull
                || declaredVariants.Values.Any(static value => value.Value is not INamedTypeSymbol))
            {
                throw Failure(
                    "rpc-authoring.value-invalid",
                    $"RPC union '{named.Name}' must declare a non-null list of ordinary sealed record variant types.",
                    named);
            }
            var variants = declaredVariants.Values
                .Select(static value => (INamedTypeSymbol)value.Value!)
                .ToArray();
            if (variants.Length == 0)
            {
                throw Failure("rpc-authoring.value-invalid", $"RPC union '{named.Name}' has no variants.", named);
            }
            if (!named.IsAbstract || !named.IsRecord)
            {
                throw Failure(
                    "rpc-authoring.value-invalid",
                    $"RPC union '{named.Name}' must be an abstract record.",
                    named);
            }
            if (variants.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).Count() != variants.Length)
            {
                throw Failure("rpc-authoring.value-invalid", $"RPC union '{named.Name}' contains duplicate variants.", named);
            }
            foreach (var variant in variants)
            {
                if (!variant.IsRecord
                    || !variant.IsSealed
                    || variant.IsAbstract
                    || variant.IsGenericType
                    || !IsOrdinarySourceSymbol(variant, sourceTrees)
                    || !InheritsFrom(variant, named))
                {
                    throw Failure(
                        "rpc-authoring.value-invalid",
                        $"RPC union variant '{variant.Name}' must be a sealed record derived from '{named.Name}'.",
                        variant);
                }
                // Record schemas describe declared members only. Do not silently drop or
                // duplicate inherited payload members in the typed variant codec.
                for (var current = variant.BaseType; current is not null; current = current.BaseType)
                {
                    var inherited = current.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(static property =>
                        property is { IsStatic: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null });
                    if (inherited is not null)
                    {
                        throw Failure(
                            "rpc-authoring.value-invalid",
                            $"RPC union variant '{variant.Name}' cannot inherit payload property '{current.Name}.{inherited.Name}'; declare payload members on the sealed variant only.",
                            variant,
                            inherited);
                    }
                }
            }
            var discriminator = GetAttribute(named, attributes.Discriminator)
                ?.ConstructorArguments.FirstOrDefault().Value as string;
            if (discriminator is not null)
            {
                var tags = new HashSet<string>(StringComparer.Ordinal);
                foreach (var variant in variants)
                {
                    if (HasAttribute(variant, attributes.Transparent))
                    {
                        throw Failure(
                            "rpc-authoring.value-invalid",
                            $"Tagged RPC union variant '{variant.Name}' cannot be transparent: its wire record must carry the synthesized discriminator.",
                            variant);
                    }
                    var tag = GetAttribute(variant, attributes.Variant)
                        ?.ConstructorArguments.FirstOrDefault().Value as string;
                    if (tag is null || !tags.Add(tag))
                    {
                        throw Failure(
                            "rpc-authoring.value-invalid",
                            $"Tagged RPC union '{named.Name}' requires one unique variant tag per variant.",
                            variant);
                    }
                    var tagged = new SunderRpcCSharpSourceSyntheticTagModel(discriminator, tag);
                    if (_taggedVariants.TryGetValue(variant, out var existingTag) && existingTag != tagged)
                    {
                        throw Failure(
                            "rpc-authoring.value-invalid",
                            $"RPC union variant '{variant.Name}' has conflicting discriminator declarations.",
                            variant);
                    }
                    _taggedVariants[variant] = tagged;
                }
            }

            var sourceVariants = variants.Select(variant => new SunderRpcCSharpSourceUnionVariantModel(
                (INamedTypeSymbol)variant.WithNullableAnnotation(NullableAnnotation.NotAnnotated),
                $"#/$defs/{GetDefinition(variant, named, depth + 1)}",
                discriminator is null ? null : _taggedVariants[variant].Tag)).ToArray();
            sourceUnion = new(discriminator, sourceVariants);
            return new JsonObject
            {
                ["oneOf"] = new JsonArray(sourceVariants
                    .Select(static variant => (JsonNode?)new JsonObject { ["$ref"] = variant.DefinitionReference })
                    .ToArray()),
            };
        }

        private bool UsesNumericEnum(INamedTypeSymbol type)
            => HasAttribute(type, attributes.NumericEnum)
                || !type.GetMembers().OfType<IFieldSymbol>().Any(field => attributes.JsonStringEnumMemberName is not null
                    && HasAttribute(field, attributes.JsonStringEnumMemberName));

        private JsonObject MapValueSchema(ITypeSymbol type, ISymbol source, AttributeData? facet, int depth)
        {
            var isNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
            if (type is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                type = nullable.TypeArguments[0];
                isNullable = true;
            }

            var schema = MapNonNullableSchema(type, source, depth);
            ApplyFacet(schema, facet, source);
            return isNullable
                ? new JsonObject
                {
                    ["oneOf"] = new JsonArray(
                        new JsonObject { ["type"] = "null" },
                        schema),
                }
                : schema;
        }

        private JsonObject MapNonNullableSchema(ITypeSymbol type, ISymbol source, int depth)
        {
            if (type.SpecialType == SpecialType.System_String)
            {
                return new() { ["type"] = "string" };
            }
            if (type.SpecialType == SpecialType.System_Boolean)
            {
                return new() { ["type"] = "boolean" };
            }
            if (type.SpecialType is SpecialType.System_Byte
                or SpecialType.System_SByte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_Int64)
            {
                return new() { ["type"] = "integer" };
            }
            if (type.SpecialType == SpecialType.System_Single)
            {
                return new() { ["format"] = "float32", ["type"] = "number" };
            }
            if (type.SpecialType == SpecialType.System_Double)
            {
                return new() { ["format"] = "float64", ["type"] = "number" };
            }
            if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
            {
                return new() { ["contentEncoding"] = "base64", ["type"] = "string" };
            }
            if (type is INamedTypeSymbol named)
            {
                var namespaceName = named.ContainingNamespace.ToDisplayString();
                if (namespaceName == "Sunder.Sdk.Services" && SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, attributes.Service.ContainingAssembly))
                {
                    if (named.Name == "SunderContent" && named.Arity == 0)
                        return new() { ["type"] = "object", ["logicalType"] = "content-reference" };
                    if (named.Name == "ServiceReference" && named.Arity == 1)
                    {
                        var targetName = named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                        if (!serviceTargets.TryGetValue(targetName, out var target))
                            throw Failure("rpc-authoring.model-projection-invalid", $"ServiceReference target '{targetName}' is not an exact generated service in the validated owned/imported set.", source);
                        var definition = Editor.GeneratedPackageNames.ContractSymbol(target.ContractId) + "_" + SunderRpcCSharpSyntax.Identifier(target.ServiceId).TrimStart('@') + "_ServiceReference";
                        if (!_serviceReferences.ContainsKey(definition))
                        {
                            ReserveDefinition(definition, source);
                            _definitions.Add(definition, Sunder.Package.Format.SunderRpcServiceReferenceSchema.Create());
                            _serviceReferences.Add(definition, target);
                        }
                        return Reference(definition);
                    }
                }
                if (named is { Name: "IReadOnlyList", Arity: 1 }
                    && namespaceName == "System.Collections.Generic")
                {
                    return new()
                    {
                        ["items"] = MapValueSchema(named.TypeArguments[0], source, null, depth + 1),
                        ["type"] = "array",
                    };
                }
                if (named is { Name: "IReadOnlyDictionary", Arity: 2 }
                    && namespaceName == "System.Collections.Generic"
                    && named.TypeArguments[0].SpecialType == SpecialType.System_String)
                {
                    return new()
                    {
                        ["additionalProperties"] = MapValueSchema(named.TypeArguments[1], source, null, depth + 1),
                        ["propertyNames"] = new JsonObject { ["type"] = "string" },
                        ["type"] = "object",
                    };
                }
                if (namespaceName == "System" && named.Name == nameof(Guid))
                {
                    return FormattedString("uuid");
                }
                if (namespaceName == "System" && named.Name == nameof(DateOnly))
                {
                    return FormattedString("date");
                }
                if (namespaceName == "System" && named.Name == nameof(DateTimeOffset)
                    && named.ContainingAssembly.Identity.Name is "System.Runtime" or "System.Private.CoreLib" or "mscorlib")
                {
                    UsesDateTimeOffset = true;
                    return FormattedString("date-time");
                }
                if (namespaceName == "System.Numerics" && named.Name == "BigInteger")
                {
                    return FormattedString("big-integer");
                }
                if (namespaceName == "System.Text.Json" && named.Name == nameof(JsonElement))
                {
                    return new();
                }
                if (namespaceName == "Sunder.Sdk.Rpc")
                {
                    return named.Name switch
                    {
                        nameof(SunderRpcOffsetDateTime) => FormattedString("date-time"),
                        nameof(SunderRpcDuration) => FormattedString("duration"),
                        nameof(SunderRpcUri) => FormattedString("uri"),
                        nameof(SunderRpcDecimal) => FormattedString("decimal"),
                        nameof(SunderRpcContentReference) => new()
                        {
                            ["logicalType"] = "content-reference",
                            ["type"] = "object",
                        },
                        _ => Reference(GetDefinition(type, source, depth)),
                    };
                }
            }
            return Reference(GetDefinition(type, source, depth));
        }

        private static JsonObject FormattedString(string format)
            => new() { ["format"] = format, ["type"] = "string" };

        private static void ApplyFacet(JsonObject schema, AttributeData? facet, ISymbol source)
        {
            if (facet is null)
            {
                return;
            }
            foreach (var argument in facet.NamedArguments)
            {
                switch (argument.Key)
                {
                    case nameof(SunderValueConstraintsAttribute.JsonObjectOnly):
                        if (argument.Value.Value is true)
                        {
                            if (schema.Any(pair => pair.Key is not ("type" or "title" or "description" or "minProperties" or "maxProperties"))
                                || schema["type"] is { } jsonType && jsonType.GetValue<string>() != "object")
                                throw Failure("rpc-authoring.value-invalid", "JsonObjectOnly requires an unconstrained JSON value or raw JSON object.", source);
                            schema["type"] = "object";
                        }
                        break;
                    case nameof(SunderValueConstraintsAttribute.MinLength):
                        AddLengthFacet(schema, "minLength", Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture));
                        break;
                    case nameof(SunderValueConstraintsAttribute.MaxLength):
                        AddLengthFacet(schema, "maxLength", Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture));
                        break;
                    case nameof(SunderValueConstraintsAttribute.MinItems):
                        schema["minItems"] = Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture);
                        break;
                    case nameof(SunderValueConstraintsAttribute.MaxItems):
                        schema["maxItems"] = Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture);
                        break;
                    case nameof(SunderValueConstraintsAttribute.MinProperties):
                        EnsureObjectSchema(schema);
                        schema["minProperties"] = Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture);
                        break;
                    case nameof(SunderValueConstraintsAttribute.MaxProperties):
                        EnsureObjectSchema(schema);
                        schema["maxProperties"] = Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture);
                        break;
                    case nameof(SunderValueConstraintsAttribute.MaxDecodedBytes):
                        schema["maxDecodedBytes"] = Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture);
                        break;
                    case nameof(SunderValueConstraintsAttribute.MaxDigits):
                        schema["maxDigits"] = Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture);
                        break;
                    case nameof(SunderValueConstraintsAttribute.MaxPrecision):
                        schema["maxPrecision"] = Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture);
                        break;
                    case nameof(SunderValueConstraintsAttribute.MaxScale):
                        schema["maxScale"] = Convert.ToInt64(argument.Value.Value, CultureInfo.InvariantCulture);
                        break;
                    case nameof(SunderValueConstraintsAttribute.Minimum):
                        AddNumericFacet(schema, "minimum", (string?)argument.Value.Value, source);
                        break;
                    case nameof(SunderValueConstraintsAttribute.Maximum):
                        AddNumericFacet(schema, "maximum", (string?)argument.Value.Value, source);
                        break;
                    case nameof(SunderValueConstraintsAttribute.ExclusiveMinimum):
                        AddNumericFacet(schema, "exclusiveMinimum", (string?)argument.Value.Value, source);
                        break;
                    case nameof(SunderValueConstraintsAttribute.ExclusiveMaximum):
                        AddNumericFacet(schema, "exclusiveMaximum", (string?)argument.Value.Value, source);
                        break;
                    case nameof(SunderValueConstraintsAttribute.ExactNumber):
                        schema["const"] = ParseNumber((string?)argument.Value.Value, source);
                        break;
                    case nameof(SunderValueConstraintsAttribute.ConstantString):
                        schema["const"] = (string?)argument.Value.Value;
                        break;
                    case nameof(SunderValueConstraintsAttribute.AllowedStrings):
                        if (schema["type"]?.GetValue<string>() != "string" || schema.ContainsKey("format")
                            || schema.ContainsKey("contentEncoding") || schema.ContainsKey("enum")
                            || argument.Value.Kind != TypedConstantKind.Array || argument.Value.IsNull
                            || argument.Value.Values.Length is < 1 or > 256)
                            throw Failure("rpc-authoring.value-invalid", "AllowedStrings requires a plain string and 1..256 distinct string values.", source);
                        var strings = argument.Value.Values.Select(value => value.Value as string).ToArray();
                        if (strings.Any(value => value is null) || strings.Distinct(StringComparer.Ordinal).Count() != strings.Length)
                            throw Failure("rpc-authoring.value-invalid", "AllowedStrings values must be non-null and distinct.", source);
                        foreach (var value in strings)
                        {
                            for (var index = 0; index < value!.Length; index++)
                            {
                                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) { index++; continue; }
                                if (char.IsSurrogate(value[index]))
                                    throw Failure("rpc-authoring.value-invalid", "AllowedStrings values must contain Unicode scalar values.", source);
                            }
                        }
                        schema["enum"] = new JsonArray(strings.Select(value => JsonValue.Create(value)).ToArray());
                        break;
                    case nameof(SunderValueConstraintsAttribute.Title):
                        schema["title"] = (string?)argument.Value.Value;
                        break;
                    case nameof(SunderValueConstraintsAttribute.Description):
                        schema["description"] = (string?)argument.Value.Value;
                        break;
                }
            }
        }

        private static void AddLengthFacet(JsonObject schema, string name, long value)
        {
            if (schema.ContainsKey("propertyNames"))
            {
                ((JsonObject)schema["propertyNames"]!)[name] = value;
            }
            else
            {
                schema[name] = value;
            }
        }

        private static void AddNumericFacet(JsonObject schema, string name, string? value, ISymbol source)
        {
            if (value is null)
            {
                throw Failure("rpc-authoring.value-invalid", $"RPC numeric facet '{name}' cannot be null.", source);
            }
            var format = schema["format"]?.GetValue<string>();
            schema[format is "big-integer" or "decimal" ? "value" + char.ToUpperInvariant(name[0]) + name[1..] : name]
                = format is "big-integer" or "decimal" ? value : ParseNumber(value, source);
        }

        private static JsonNode ParseNumber(string? value, ISymbol source)
        {
            try
            {
                return JsonNode.Parse(value ?? string.Empty)
                       ?? throw new JsonException("The numeric literal is null.");
            }
            catch (JsonException exception)
            {
                throw Failure("rpc-authoring.value-invalid", $"RPC numeric facet is invalid: {exception.Message}", source);
            }
        }

        private static void EnsureObjectSchema(JsonObject schema)
        {
            if (schema.Count == 0)
            {
                schema["type"] = "object";
            }
        }

        private void ReserveDefinition(string definitionName, ISymbol owner)
        {
            if (_definitionOwners.TryGetValue(definitionName, out var existing)
                && !SymbolEqualityComparer.Default.Equals(existing, owner))
            {
                var diagnosticId = existing is INamedTypeSymbol && owner is INamedTypeSymbol
                    ? "rpc-authoring.identity-collision"
                    : "rpc-authoring.generated-name-collision";
                var existingLocation = existing is INamedTypeSymbol existingType
                    ? DefinitionIdentityLocation(existingType, GetAttribute(existingType, attributes.Definition))
                    : SourceLocation(existing);
                var ownerLocation = owner is INamedTypeSymbol ownerType
                    ? DefinitionIdentityLocation(ownerType, GetAttribute(ownerType, attributes.Definition))
                    : SourceLocation(owner);
                throw FailureAtLocations(
                    diagnosticId,
                    $"RPC definition name '{definitionName}' is produced by both '{existing.Name}' and '{owner.Name}'.",
                    [existingLocation, ownerLocation]);
            }
            _definitionOwners.TryAdd(definitionName, owner);
        }

        private static (ITypeSymbol Type, bool IsOptional) UnwrapOptional(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named
                && named is { Name: "SunderRpcOptional", Arity: 1 }
                && named.ContainingNamespace.ToDisplayString() == "Sunder.Sdk.Rpc"
                && named.ContainingAssembly.Identity.Name == SdkAssemblyIdentity.Name
                && named.ContainingAssembly.Identity.Version == SdkAssemblyIdentity.Version)
            {
                return (named.TypeArguments[0], true);
            }
            return (type, false);
        }

        private string? GetJsonPropertyName(IPropertySymbol property)
        {
            if (attributes.JsonPropertyName is null)
            {
                return null;
            }
            var attribute = GetAttribute(property, attributes.JsonPropertyName);
            return attribute?.ConstructorArguments.FirstOrDefault().Value as string;
        }

        private static string GetDefaultPropertyName(IPropertySymbol property)
        {
            if (!SunderRpcCSharpNaming.TryCreate(property.Name, null, out var name))
            {
                throw Failure("rpc-authoring.value-invalid", $"RPC property name '{property.Name}' is not portable.", property);
            }
            return name!.Camel;
        }

        private static JsonObject ClosedRecord(JsonObject properties, IReadOnlyList<string> required)
            => new()
            {
                ["additionalProperties"] = false,
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(static item => JsonValue.Create(item)).ToArray()),
                ["type"] = "object",
            };

    }

    private static bool HasSystemAttribute(ISymbol symbol, string metadataName)
        => symbol.GetAttributes().Any(attribute => string.Equals(
            attribute.AttributeClass?.ToDisplayString(),
            metadataName,
            StringComparison.Ordinal));

    private sealed class CompilerFailure(SunderRpcCSharpCompilerDiagnostic diagnostic) : Exception
    {
        public SunderRpcCSharpCompilerDiagnostic Diagnostic { get; } = diagnostic;
    }
}
