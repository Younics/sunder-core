using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcAuthoringAttributeTests
{
    private static readonly Type[] AuthoringTypes =
    [
        typeof(SunderRpcExposure),
        typeof(SunderRpcProviderAudience),
        typeof(SunderRpcServiceAttribute),
        typeof(SunderRpcProviderAttribute),
        typeof(SunderRpcPlatformImportAttribute),
        typeof(SunderRpcMethodAttribute),
        typeof(SunderRpcDomainErrorAttribute),
        typeof(SunderRpcDefinitionAttribute),
        typeof(SunderRpcTransparentAttribute),
        typeof(SunderRpcNumericEnumAttribute),
        typeof(SunderRpcUnionAttribute),
        typeof(SunderRpcDiscriminatorAttribute),
        typeof(SunderRpcVariantAttribute),
        typeof(SunderRpcFacetAttribute),
    ];

    [Fact]
    public void AuthoringTypes_HaveFrozenCapabilityAndAttributeUsage()
    {
        Assert.All(AuthoringTypes, type =>
        {
            var capability = Assert.Single(type.GetCustomAttributes<SunderSdkCapabilityAttribute>());
            Assert.Equal(SunderSdkCapabilities.RpcV1, capability.Capability);
        });

        AssertUsage<SunderRpcServiceAttribute>(AttributeTargets.Class | AttributeTargets.Interface);
        AssertUsage<SunderRpcProviderAttribute>(AttributeTargets.Class);
        AssertUsage<SunderRpcPlatformImportAttribute>(AttributeTargets.Interface);
        AssertUsage<SunderRpcMethodAttribute>(AttributeTargets.Method);
        AssertUsage<SunderRpcDomainErrorAttribute>(AttributeTargets.Class | AttributeTargets.Interface, allowMultiple: true);
        AssertUsage<SunderRpcDefinitionAttribute>(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum);
        AssertUsage<SunderRpcTransparentAttribute>(AttributeTargets.Class | AttributeTargets.Struct);
        AssertUsage<SunderRpcNumericEnumAttribute>(AttributeTargets.Enum);
        AssertUsage<SunderRpcUnionAttribute>(AttributeTargets.Class);
        AssertUsage<SunderRpcDiscriminatorAttribute>(AttributeTargets.Class);
        AssertUsage<SunderRpcVariantAttribute>(AttributeTargets.Class);
        AssertUsage<SunderRpcFacetAttribute>(
            AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property
            | AttributeTargets.Parameter | AttributeTargets.ReturnValue);
    }

    [Fact]
    public void AuthoringAttributes_PreserveFrozenInputsAndDefaults()
    {
        Assert.Equal(new[] { SunderRpcExposure.Package, SunderRpcExposure.Public }, Enum.GetValues<SunderRpcExposure>());
        Assert.Equal(
            new[] { SunderRpcProviderAudience.Inferred, SunderRpcProviderAudience.Owner, SunderRpcProviderAudience.Public },
            Enum.GetValues<SunderRpcProviderAudience>());

        var service = new SunderRpcServiceAttribute(SunderRpcExposure.Public);
        Assert.Equal(SunderRpcExposure.Public, service.Exposure);
        Assert.Null(service.ContractId);
        Assert.Null(service.ServiceId);

        var inferredProvider = new SunderRpcProviderAttribute();
        Assert.Null(inferredProvider.ServiceType);
        Assert.Null(inferredProvider.ProviderId);
        Assert.Equal(SunderRpcProviderAudience.Inferred, inferredProvider.Audience);
        Assert.Equal(typeof(IDisposable), new SunderRpcProviderAttribute(typeof(IDisposable)).ServiceType);
        var platform = new SunderRpcPlatformImportAttribute("sunder.stack.contributor", typeof(object));
        Assert.Equal("sunder.stack.contributor", platform.ContractId);
        Assert.Equal(typeof(object), platform.AdapterType);

        var method = new SunderRpcMethodAttribute();
        Assert.Null(method.MethodId);
        Assert.Null(method.RequestDefinitionName);
        Assert.Null(method.OutputDefinitionName);
        Assert.Equal(SunderRpcMethodIdempotency.None, method.Idempotency);
        Assert.Empty(method.Errors);

        var error = new SunderRpcDomainErrorAttribute("value-invalid");
        Assert.Equal("value-invalid", error.Code);
        Assert.Null(error.Description);
        Assert.Null(error.DetailsType);
        Assert.Equal("Payload", new SunderRpcDefinitionAttribute("Payload").Name);
        Assert.Equal("kind", new SunderRpcDiscriminatorAttribute("kind").PropertyName);
        Assert.Equal("ready", new SunderRpcVariantAttribute("ready").Tag);

        var variants = new[] { typeof(string), typeof(long) };
        var union = new SunderRpcUnionAttribute(variants);
        variants[0] = typeof(bool);
        Assert.Equal(new[] { typeof(string), typeof(long) }, union.Variants);
        Assert.Throws<NotSupportedException>(() => ((IList<Type>)union.Variants).Clear());

        var facet = new SunderRpcFacetAttribute();
        Assert.All(
            new[]
            {
                facet.MinLength,
                facet.MaxLength,
                facet.MinItems,
                facet.MaxItems,
                facet.MinProperties,
                facet.MaxProperties,
                facet.MaxDecodedBytes,
                facet.MaxDigits,
                facet.MaxPrecision,
                facet.MaxScale,
            },
            value => Assert.Equal(-1, value));
        Assert.All(
            new[]
            {
                facet.Minimum,
                facet.Maximum,
                facet.ExclusiveMinimum,
                facet.ExclusiveMaximum,
                facet.ExactNumber,
                facet.ConstantString,
                facet.Title,
                facet.Description,
            },
            Assert.Null);
    }

    [Fact]
    public void FrozenValidCSharpFixtures_CompileAgainstThePublicAuthoringSurface()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "parity", "valid");
        Assert.True(Directory.Exists(fixtureRoot), $"Authoring fixture directory is missing: {fixtureRoot}");

        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        var references = trustedPlatformAssemblies
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(SunderRpcServiceAttribute).Assembly.Location))
            .ToArray();

        foreach (var path in Directory.EnumerateFiles(fixtureRoot, "*.cs").Order(StringComparer.Ordinal))
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(
                File.ReadAllText(path),
                new CSharpParseOptions(LanguageVersion.CSharp14),
                path);
            var compilation = CSharpCompilation.Create(
                $"AuthoringFixture_{Path.GetFileNameWithoutExtension(path)}",
                [syntaxTree],
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));
            var errors = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();

            Assert.True(errors.Length == 0, $"{Path.GetFileName(path)}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    private static void AssertUsage<TAttribute>(AttributeTargets targets, bool allowMultiple = false)
        where TAttribute : Attribute
    {
        var usage = typeof(TAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(targets, usage.ValidOn);
        Assert.Equal(allowMultiple, usage.AllowMultiple);
        Assert.False(usage.Inherited);
    }
}
