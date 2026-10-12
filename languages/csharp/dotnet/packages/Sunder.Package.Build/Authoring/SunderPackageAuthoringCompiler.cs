using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Authoring;

internal sealed record SunderPackageAuthoringAnalysis(
    SunderRpcCSharpCompilationResult Compilation,
    SunderPackageRolePlan RolePlan,
    IReadOnlyList<SunderRpcCSharpSourceFile> GeneratedSources)
{
    internal bool Success => Compilation.Success && RolePlan.Success;
    internal IReadOnlySet<string> OrdinarySourcePaths { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    internal SunderPackageCompositionPlan? Composition { get; init; }
}

internal static class SunderPackageAuthoringCompiler
{
    internal static SunderPackageAuthoringAnalysis Analyze(SunderRpcCSharpCompilerInput input)
    {
        var compiler = new SunderRpcCSharpSourceCompiler();
        var augmented = input with { GenerateServiceInterfaces = true };
        var prepass = SunderRpcCSharpOwnedClientPrepass.Compile(compiler, augmented);
        if (!prepass.Compilation.Success) return new(prepass.Compilation, new([], [], []), []);
        var generated = prepass.Sources.Append(new SunderRpcCSharpSourceFile("authoring.g.cs",
            SunderRpcCSharpImplementationEmitter.Emit(prepass.Compilation.Contracts, prepass.Compilation.ImportedProviders))).ToArray();
        var verified = compiler.Compile(augmented with
        {
            SupplementalSources = (input.SupplementalSources ?? []).Concat(generated).ToArray(),
            RequireGeneratedMembersResolved = true,
        });
        if (!verified.Success) return new(verified, new([], [], []), []);
        if (SunderRpcCSharpServiceApiEmitter.EmitInterfaces(verified.Contracts)
                != SunderRpcCSharpServiceApiEmitter.EmitInterfaces(prepass.Compilation.Contracts)
            || SunderRpcCSharpImplementationEmitter.Emit(verified.Contracts, verified.ImportedProviders) != generated[^1].Text)
            throw new InvalidDataException("package-authoring.evidence-mismatch: Service API/composition changed after strict generated-source compilation.");
        var sourcePaths = input.Sources.Select(source => source.Path).ToHashSet(StringComparer.Ordinal);
        var compilation = verified.SourceCompilation!;
        var trees = compilation.SyntaxTrees.Where(tree => sourcePaths.Contains(tree.FilePath)).ToArray();
        var plan = SunderPackageRolePlanner.Plan(compilation, trees, verified.Contracts.SelectMany(contract => contract.SourceTypes).Select(value => value.SourceType),
            trustedImportedModelTypes: verified.TrustedImportedModelTypes);
        return Complete(new(verified, plan, generated) { OrdinarySourcePaths = sourcePaths });
    }

    internal static SunderPackageAuthoringAnalysis Complete(SunderPackageAuthoringAnalysis analysis)
    {
        if (!analysis.Success) return analysis;
        var composition = SunderPackageCompositionPlanner.Plan(analysis);
        return analysis with { Composition = composition, RolePlan = analysis.RolePlan with
        {
            Diagnostics = analysis.RolePlan.Diagnostics.Concat(composition.Diagnostics).ToArray(),
        } };
    }
}
