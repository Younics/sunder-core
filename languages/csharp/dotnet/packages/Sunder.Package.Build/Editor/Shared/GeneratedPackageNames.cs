using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Editor;

// Shared by the portable editor and build compiler. This encoding depends only on
// canonical package identity, never aliases, source namespaces or the import set.
internal static class GeneratedPackageNames
{
    internal static string Services(string packageId) => Namespace(packageId, "Services");
    internal static string Models(string packageId) => Namespace(packageId, "Models");
    internal static string Internal(string packageId) => Namespace(packageId, "Internal");

    internal static string ContractSymbol(string contractId)
    {
        if (!IsCanonicalPackageId(contractId)) throw new ArgumentException("A contract symbol requires a canonical contract ID.", nameof(contractId));
        return "C" + string.Join("_", contractId.Split('.').Select(segment => char.ToUpperInvariant(segment[0]) + segment.Substring(1)));
    }

    internal static string Transport(string packageId, string contractId) => Internal(packageId) + "." + ContractSymbol(contractId);

    internal static string Namespace(string packageId, string area)
    {
        if (!IsCanonicalPackageId(packageId))
            throw new ArgumentException("Generated package names require a canonical Sunder package ID.", nameof(packageId));
        if (area != "Services" && area != "Models" && area != "Internal")
            throw new ArgumentException("Unknown generated package namespace area.", nameof(area));
        var stem = string.Join("_", packageId.Split('.').Select(segment => char.ToUpperInvariant(segment[0]) + segment.Substring(1)));
        // An underscore cannot occur in a canonical ID, so the digit escape and
        // segment separators are injective. The area suffix is always present.
        if (stem[0] >= '0' && stem[0] <= '9') stem = "_" + stem;
        return "Sunder.Generated.Packages." + stem + "_" + area;
    }

    internal static bool IsCanonicalPackageId(string? value)
        => !string.IsNullOrEmpty(value) && value!.Length <= 128
            && value.Split('.').All(segment => segment.Length != 0
                && segment.All(character => character >= 'a' && character <= 'z' || character >= '0' && character <= '9'));

    internal static string ServiceInterfaceName(INamedTypeSymbol service)
    {
        var declaration = service.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "Sunder.Sdk.Services.SunderServiceAttribute"
            && attribute.AttributeClass.ContainingAssembly.Identity.Name == "Sunder.Sdk");
        var serviceId = declaration?.NamedArguments.FirstOrDefault(argument => argument.Key == "ServiceId").Value.Value as string;
        if (serviceId == null)
        {
            var key = declaration?.NamedArguments.FirstOrDefault(argument => argument.Key == "Key").Value.Value as string;
            if (!SunderRpcCSharpNaming.TryCreate(key ?? service.Name, key == null ? "Service" : null, out var name))
                throw new ArgumentException("A service declaration must have a portable service key.", nameof(service));
            serviceId = name!.LowerKebab;
        }
        return NaturalInterfaceName(serviceId);
    }

    internal static string NaturalInterfaceName(string serviceId)
    {
        var stem = new StringBuilder();
        var capitalize = true;
        foreach (var character in serviceId)
        {
            if (!(character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' || character >= '0' && character <= '9'))
            { capitalize = true; continue; }
            stem.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }
        if (stem.Length == 0) stem.Append("Value");
        if (stem[0] >= '0' && stem[0] <= '9') stem.Insert(0, '_');
        var value = stem.ToString();
        return "I" + value + (value.EndsWith("Service", StringComparison.Ordinal) ? "" : "Service");
    }

    internal static string ServiceInterface(string packageId, INamedTypeSymbol service)
        => Services(packageId) + "." + ServiceInterfaceName(service);

    internal static string ServiceInterface(string packageId, string serviceId)
        => Services(packageId) + "." + NaturalInterfaceName(serviceId);

    internal static string ImportedServiceInterface(string packageId, string contractId, string serviceId)
        => Services(packageId) + "." + ContractSymbol(contractId) + "." + NaturalInterfaceName(serviceId);

    internal static bool TryResolvePackageId(Compilation compilation, INamedTypeSymbol packageAttribute,
        string? supplied, out string packageId, out string? error)
    {
        packageId = string.Empty;
        var declarations = compilation.Assembly.GetAttributes()
            .Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, packageAttribute)).ToArray();
        var declared = declarations.Length == 1
            ? declarations[0].NamedArguments.FirstOrDefault(argument => argument.Key == "Id").Value.Value as string : null;
        if (declarations.Length > 1 || declared != null && !IsCanonicalPackageId(declared))
        {
            error = "The source must declare one canonical Sunder package identity.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(supplied) && declared != null && supplied != declared)
        {
            error = "The supplied Sunder package ID does not match the source package identity.";
            return false;
        }
        var selected = string.IsNullOrWhiteSpace(supplied) ? declared : supplied;
        if (!IsCanonicalPackageId(selected))
        {
            error = "Service API generation requires an exact [assembly: SunderPackage] identity or SunderPackageId.";
            return false;
        }
        packageId = selected!;
        error = null;
        return true;
    }
}
