using Microsoft.CodeAnalysis;

namespace Sunder.Package.Build.Authoring;

internal static class SunderPackageDependencyDiscovery
{
    internal static string Name(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    internal static INamedTypeSymbol Owner(INamedTypeSymbol type)
    {
        while (type.ContainingType is not null) type = type.ContainingType;
        return type.OriginalDefinition;
    }

    internal static INamedTypeSymbol[] Implementations(INamedTypeSymbol contract, IEnumerable<INamedTypeSymbol> types)
        => contract.TypeKind != TypeKind.Interface ? [] : types
            .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract && !type.IsStatic && !type.IsGenericType
                && type.AllInterfaces.Contains(contract, SymbolEqualityComparer.Default))
            .OrderBy(Name, StringComparer.Ordinal).ToArray();

    internal static bool IsLifecycle(INamedTypeSymbol type)
        => IsWorkerContract(type, "Sunder.Sdk.Worker.ISunderWorkerLifecycleService");

    internal static bool IsActivation(INamedTypeSymbol type)
        => IsWorkerContract(type, "Sunder.Sdk.Worker.ISunderWorkerActivationService");
    internal static bool IsAppActivation(INamedTypeSymbol type)
        => IsSdkContract(type, "Sunder.Sdk.Abstractions.IPackageAppActivationService");

    internal static bool IsSettingsSchemaProvider(INamedTypeSymbol type)
        => IsWorkerContract(type, "Sunder.Sdk.Worker.ISunderWorkerSettingsSchemaProvider");

    internal static bool IsStorageMigrationProvider(INamedTypeSymbol type)
        => IsWorkerContract(type, "Sunder.Sdk.Worker.ISunderWorkerStorageMigrationProvider");

    internal static bool IsStorageMigrationPreparation(INamedTypeSymbol type)
        => IsWorkerContract(type, "Sunder.Sdk.Worker.ISunderWorkerStorageMigrationPreparation");

    internal static bool IsAuthHandler(INamedTypeSymbol type)
        => IsSdkContract(type, "Sunder.Sdk.Abstractions.IPackageAuthHandler");

    internal static bool IsCallbackHandler(INamedTypeSymbol type)
        => IsSdkContract(type, "Sunder.Sdk.Abstractions.IPackageCallbackHandler");

    internal static bool IsPreferredConstructor(IMethodSymbol constructor)
        => constructor.GetAttributes().Any(attribute => attribute.AttributeClass is { } type
            && type.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute"
            && type.ContainingAssembly.Identity.Name == "Microsoft.Extensions.DependencyInjection.Abstractions"
            && type.ContainingAssembly.Identity.Version == typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute).Assembly.GetName().Version
            && type.ContainingAssembly.Identity.PublicKeyToken.SequenceEqual(
                typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute).Assembly.GetName().GetPublicKeyToken()!));

    private static bool IsWorkerContract(INamedTypeSymbol type, string name)
        => type.AllInterfaces.Any(value => value.ToDisplayString() == name
            && value.ContainingAssembly.Identity.Name == "Sunder.Sdk.Worker"
            && value.ContainingAssembly.Identity.Version == typeof(Sunder.Sdk.Abstractions.IPackageContext).Assembly.GetName().Version);

    private static bool IsSdkContract(INamedTypeSymbol type, string name)
        => type.AllInterfaces.Any(value => value.ToDisplayString() == name
            && value.ContainingAssembly.Identity.Name == "Sunder.Sdk"
            && value.ContainingAssembly.Identity.Version == typeof(Sunder.Sdk.Abstractions.IPackageContext).Assembly.GetName().Version);

    internal static bool IsRpcDeclaration(INamedTypeSymbol type)
        => type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "Sunder.Sdk.Services.SunderServiceAttribute"
            && attribute.AttributeClass.ContainingAssembly.Identity.Name == "Sunder.Sdk"
            && attribute.AttributeClass.ContainingAssembly.Identity.Version == typeof(Sunder.Sdk.Services.SunderServiceAttribute).Assembly.GetName().Version);
}
