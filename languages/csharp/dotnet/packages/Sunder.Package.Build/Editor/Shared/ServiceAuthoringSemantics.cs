using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Sunder.Package.Build.Editor;

// Shared ordinary-method eligibility for the compiler and editor projection.
internal static class ServiceAuthoringSemantics
{
    internal static bool IsImplicitMethod(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.DeclaredAccessibility != Accessibility.Public
            || method.OverriddenMethod?.ContainingType.SpecialType == SpecialType.System_Object || method.IsImplicitlyDeclared) return false;
        foreach (var contract in method.ContainingType.AllInterfaces)
        {
            var name = contract.ToDisplayString();
            if (name.StartsWith("Sunder.Sdk.", StringComparison.Ordinal)
                && (contract.ContainingAssembly.Identity.Name is not ("Sunder.Sdk" or "Sunder.Sdk.Worker")
                    || contract.ContainingAssembly.Identity.Version != new Version(1, 1, 0, 0))) continue;
            if (name != "System.IDisposable" && name != "System.IAsyncDisposable"
                && name != "Sunder.Sdk.Abstractions.IPackageCallbackHandler" && name != "Sunder.Sdk.Abstractions.IPackageAuthHandler"
                && !name.StartsWith("Sunder.Sdk.Worker.ISunderWorker", StringComparison.Ordinal)) continue;
            if (contract.GetMembers().OfType<IMethodSymbol>().Any(member => SymbolEqualityComparer.Default.Equals(
                    method.ContainingType.FindImplementationForInterfaceMember(member), method))) return false;
        }
        if (method.IsAsync) return true; // Includes invalid async-void, diagnosed by the owning compiler.
        if (method.ReturnType is not INamedTypeSymbol type) return false;
        var ns = type.ContainingNamespace.ToDisplayString();
        return ns == "System.Threading.Tasks" && (type.Name == "Task" || type.Name == "ValueTask")
            || ns == "System.Collections.Generic" && type.Name == "IAsyncEnumerable";
    }
}
