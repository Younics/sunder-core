using Microsoft.CodeAnalysis;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Authoring;

internal sealed record SunderPackageConstructorArgument(IParameterSymbol Parameter, ITypeSymbol ServiceType, bool Deferred = false);

internal sealed record SunderPackageServiceConstruction(
    INamedTypeSymbol Type,
    IMethodSymbol Constructor,
    bool Transient,
    bool Lifecycle,
    IReadOnlyList<SunderPackageConstructorArgument> Arguments)
{
    internal bool Activation => !Lifecycle && SunderPackageDependencyDiscovery.IsActivation(Type);
}

internal sealed record SunderPackageCompositionPlan(
    IReadOnlyList<SunderPackageServiceConstruction> Worker,
    IReadOnlyList<SunderPackageServiceConstruction> App,
    IReadOnlyList<SunderRpcCSharpCompilerDiagnostic> Diagnostics)
{
    internal bool Success => Diagnostics.Count == 0;
}
