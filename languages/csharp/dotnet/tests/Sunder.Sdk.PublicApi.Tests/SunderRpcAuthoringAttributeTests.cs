using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcAuthoringAttributeTests
{
    private static readonly Type[] AuthoringTypes =
    [
        typeof(SunderServiceExposure),
        typeof(SunderServiceAudience),
        typeof(SunderServiceIdempotency),
        typeof(SunderServiceAttribute),
        typeof(SunderServiceProviderAttribute),
        typeof(SunderPlatformImportAttribute),
        typeof(SunderMethodAttribute),
        typeof(SunderOptionalServiceReferenceAttribute),
        typeof(SunderServiceErrorAttribute),
        typeof(SunderValueAttribute),
        typeof(SunderTransparentValueAttribute),
        typeof(SunderNumericEnumAttribute),
        typeof(SunderUnionAttribute),
        typeof(SunderDiscriminatorAttribute),
        typeof(SunderVariantAttribute),
        typeof(SunderValueConstraintsAttribute),
        typeof(SunderAppUseAttribute),
        typeof(SunderRuntimeUseAttribute),
    ];

    [Fact]
    public void AuthoringTypes_HaveFrozenCapabilityAndAttributeUsage()
    {
        Assert.All(AuthoringTypes, type =>
        {
            Assert.Equal("Sunder.Sdk.Services", type.Namespace);
            Assert.Null(type.Assembly.GetType("Sunder.Sdk.Rpc." + type.Name));
            var capability = Assert.Single(type.GetCustomAttributes<SunderSdkCapabilityAttribute>());
            Assert.Equal(SunderSdkCapabilities.RpcV1, capability.Capability);
        });

        AssertUsage<SunderServiceAttribute>(AttributeTargets.Class | AttributeTargets.Interface);
        AssertUsage<SunderServiceProviderAttribute>(AttributeTargets.Class, allowMultiple: true);
        AssertUsage<SunderPlatformImportAttribute>(AttributeTargets.Interface);
        AssertUsage<SunderMethodAttribute>(AttributeTargets.Method);
        AssertUsage<SunderOptionalServiceReferenceAttribute>(AttributeTargets.Property);
        AssertUsage<SunderServiceErrorAttribute>(AttributeTargets.Class | AttributeTargets.Interface, allowMultiple: true);
        AssertUsage<SunderValueAttribute>(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum);
        AssertUsage<SunderTransparentValueAttribute>(AttributeTargets.Class | AttributeTargets.Struct);
        AssertUsage<SunderNumericEnumAttribute>(AttributeTargets.Enum);
        AssertUsage<SunderUnionAttribute>(AttributeTargets.Class);
        AssertUsage<SunderDiscriminatorAttribute>(AttributeTargets.Class);
        AssertUsage<SunderVariantAttribute>(AttributeTargets.Class);
        AssertUsage<SunderAppUseAttribute>(AttributeTargets.Class | AttributeTargets.Interface);
        AssertUsage<SunderRuntimeUseAttribute>(AttributeTargets.Class | AttributeTargets.Interface, allowMultiple: true);
        AssertUsage<SunderValueConstraintsAttribute>(
            AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property
            | AttributeTargets.Parameter | AttributeTargets.ReturnValue);
    }

    [Fact]
    public void AuthoringAttributes_PreserveFrozenInputsAndDefaults()
    {
        Assert.Equal(new[] { SunderServiceExposure.Package, SunderServiceExposure.Public }, Enum.GetValues<SunderServiceExposure>());
        Assert.Equal(
            new[] { SunderServiceAudience.Inferred, SunderServiceAudience.Owner, SunderServiceAudience.Public },
            Enum.GetValues<SunderServiceAudience>());

        var service = new SunderServiceAttribute(SunderServiceExposure.Public);
        Assert.Equal(SunderServiceExposure.Public, service.Exposure);
        Assert.Null(service.ContractId);
        Assert.Null(service.ServiceId);

        var inferredProvider = new SunderServiceProviderAttribute();
        Assert.Null(inferredProvider.ServiceType);
        Assert.Null(inferredProvider.ProviderId);
        Assert.Equal(SunderServiceAudience.Inferred, inferredProvider.Audience);
        Assert.Equal(typeof(IDisposable), new SunderServiceProviderAttribute(typeof(IDisposable)).ServiceType);
        Assert.Null(new SunderRuntimeUseAttribute().ServiceType);
        var typedUse = new SunderRuntimeUseAttribute(typeof(IDisposable));
        Assert.Equal(typeof(IDisposable), typedUse.ServiceType);
        Assert.False(typedUse.Required);
        Assert.Null(typedUse.Actions);
        var platform = new SunderPlatformImportAttribute("sunder.stack.contributor", typeof(object));
        Assert.Equal("sunder.stack.contributor", platform.ContractId);
        Assert.Equal(typeof(object), platform.AdapterType);

        var method = new SunderMethodAttribute();
        Assert.Null(method.MethodId);
        Assert.Null(method.RequestDefinitionName);
        Assert.Null(method.OutputDefinitionName);
        Assert.Equal(SunderServiceIdempotency.None, method.Idempotency);
        Assert.Empty(method.Errors);
        Assert.False(method.CompleteOnResponseContentRetirement);

        var error = new SunderServiceErrorAttribute("value-invalid");
        Assert.Equal("value-invalid", error.Code);
        Assert.Null(error.Description);
        Assert.Null(error.DetailsType);
        Assert.Equal("Payload", new SunderValueAttribute("Payload").Name);
        Assert.Equal("kind", new SunderDiscriminatorAttribute("kind").PropertyName);
        Assert.Equal("ready", new SunderVariantAttribute("ready").Tag);

        var variants = new[] { typeof(string), typeof(long) };
        var union = new SunderUnionAttribute(variants);
        variants[0] = typeof(bool);
        Assert.Equal(new[] { typeof(string), typeof(long) }, union.Variants);
        Assert.Throws<NotSupportedException>(() => ((IList<Type>)union.Variants).Clear());

        var facet = new SunderValueConstraintsAttribute();
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
    public void CurrentCSharpDeclarationsCompileAgainstTheCurrentSdk()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "current-parity", "csharp", "valid");
        Assert.True(Directory.Exists(fixtureRoot), $"Authoring fixture directory is missing: {fixtureRoot}");

        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        var references = trustedPlatformAssemblies
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(SunderServiceAttribute).Assembly.Location))
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

            Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        }
    }

    [Fact]
    public void RetiredAuthoringTypesAreAbsentWhileWireContractsRemain()
    {
        var assembly = typeof(SunderServiceAttribute).Assembly;
        foreach (var name in new[] { "Service", "Provider", "PlatformImport", "Method", "DomainError", "Definition", "Transparent",
                     "NumericEnum", "Union", "Discriminator", "Variant", "Facet", "AppUse", "RuntimeUse" })
            Assert.Null(assembly.GetType("Sunder.Sdk.Rpc.SunderRpc" + name + "Attribute"));
        Assert.Null(assembly.GetType("Sunder.Sdk.Rpc.SunderRpcExposure"));
        Assert.Null(assembly.GetType("Sunder.Sdk.Rpc.SunderRpcProviderAudience"));
        Assert.NotNull(assembly.GetType("Sunder.Sdk.Rpc.SunderRpcMethodIdempotency"));
        Assert.NotNull(assembly.GetType("Sunder.Sdk.Rpc.SunderRpcInvocationContext"));
        Assert.NotNull(assembly.GetType("Sunder.Sdk.Rpc.ISunderRpcServiceHandler"));
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
