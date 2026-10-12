using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Microsoft.CodeAnalysis.CSharp;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpOwnedClientPrepassResult(
    SunderRpcCSharpCompilationResult Compilation,
    IReadOnlyList<SunderRpcCSharpSourceFile> Sources)
{
    internal IReadOnlyList<SunderRpcCSharpSourceFile> FrameworkSources { get; init; } = [];
}

internal static class SunderRpcCSharpOwnedClientPrepass
{
    internal static SunderRpcCSharpOwnedClientPrepassResult Compile(
        SunderRpcCSharpSourceCompiler compiler, SunderRpcCSharpCompilerInput input,
        Func<CSharpCompilation, IReadOnlyList<SunderRpcCSharpSourceFile>>? frameworkGenerator = null)
    {
        // Derive tentative contracts from ordinary declarations, not old baselines
        // or prior output. Consumer bodies may name clients that do not exist yet.
        var discovered = compiler.DiscoverForOwnedClientPrepass(input);
        if (!discovered.Success) return new(discovered, []);
        var sources = Generate(discovered, input.PackageVersion, input.GenerateServiceInterfaces);
        var sourceCompilation = discovered.SourceCompilation!;
        sourceCompilation = sourceCompilation.RemoveSyntaxTrees(sourceCompilation.SyntaxTrees.Where(tree => tree.FilePath == "service-forward-types.g.cs"));
        var framework = frameworkGenerator is null ? [] : frameworkGenerator(sourceCompilation.AddSyntaxTrees(sources.Select(source => CSharpSyntaxTree.ParseText(source.Text,
            new CSharpParseOptions(LanguageVersion.CSharp14, preprocessorSymbols: input.DefineConstants ?? []), source.Path))));
        var compiled = compiler.Compile(input with
        {
            SupplementalSources = (input.SupplementalSources ?? []).Concat(sources).Concat(framework).ToArray(),
        });
        if (!compiled.Success) return new(compiled, []);
        var verified = Generate(compiled, input.PackageVersion, input.GenerateServiceInterfaces);
        if (!sources.SequenceEqual(verified) || discovered.ResolvedPackageId != compiled.ResolvedPackageId)
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Owned client source changed after strict source compilation.");
        if (SunderRpcCSharpImplementationEmitter.Emit(discovered.Contracts, discovered.ImportedProviders)
            != SunderRpcCSharpImplementationEmitter.Emit(compiled.Contracts, compiled.ImportedProviders))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: Regenerated authoring.g.cs changed after resolving owned client source.");
        if (!Projection(discovered).SequenceEqual(Projection(compiled), StringComparer.Ordinal))
            throw new InvalidDataException("rpc-authoring.evidence-mismatch: The complete authoring receipt changed after resolving owned client source.");
        if (frameworkGenerator is not null && !framework.SequenceEqual(frameworkGenerator(compiled.SourceCompilation!)))
            throw new InvalidDataException("package-authoring.evidence-mismatch: Framework-generated members changed after strict source compilation.");
        return new(compiled, verified) { FrameworkSources = framework };
    }

    private static IEnumerable<string> Projection(SunderRpcCSharpCompilationResult result)
        => result.Contracts.Select(contract => string.Join('\0', "contract", contract.ContractKey, contract.Exposure.ToString(),
                Policy(contract.AppUse), Policy(contract.RuntimeUse), Convert.ToBase64String(contract.ModelProjection.ToCanonicalUtf8()), Convert.ToBase64String(contract.MethodPolicies.ToCanonicalUtf8())))
            .Concat(result.Contracts.SelectMany(contract => contract.Providers.Select(provider => string.Join('\0', "owned-provider",
                contract.ContractKey, provider.ProviderId, provider.Audience.ToString(), provider.ServiceId, provider.SourceType.ToDisplayString()))))
            .Concat(result.ImportedProviders.Select(provider => string.Join('\0', "imported-provider", provider.Surface.Key,
                provider.Provider.ProviderId, provider.Provider.Audience.ToString(), provider.Provider.ServiceId, provider.Provider.SourceType.ToDisplayString())))
            .Concat(result.ImportedUses.Select(use => "typed-runtime-use\0" + SunderRpcAuthoringDocuments.RoleUseIdentity(use.Evidence)))
            .Order(StringComparer.Ordinal);

    private static string Policy(SunderRpcOwnedUsePolicy? value)
        => value is null ? "absent" : value.Required + ":" + string.Join(';', value.Actions);

    private static IReadOnlyList<SunderRpcCSharpSourceFile> Generate(SunderRpcCSharpCompilationResult compilation, string version, bool serviceInterfaces)
    {
        var sources = new List<SunderRpcCSharpSourceFile>();
        sources.AddRange(SunderRpcCSharpImportPrepass.PrepareTypedUseClients(compilation));
        foreach (var contract in compilation.Contracts)
        {
            // These mechanical bindings are internal; ordinary owned calls use
            // their source-shaped interface and authored model symbols directly.
            sources.Add(new("bindings/" + contract.ContractKey + ".g.cs", SunderRpcCSharpGenerator.Generate(contract.Descriptor,
                contract.GeneratedLogicalName, SunderRpcCSharpAccessibility.Internal)));
            sources.Add(new("bindings/" + contract.ContractKey + ".schema.g.cs", SunderRpcCSharpProjectedBindings.SchemaSource(contract.GeneratedLogicalName, contract.Descriptor)));
        }
        if (compilation.Contracts.Count != 0)
        {
            if (serviceInterfaces) sources.Add(new("service-apis.g.cs", SunderRpcCSharpServiceApiEmitter.EmitInterfaces(compilation.Contracts)));
            var contracts = compilation.Contracts.Select(contract => new SunderRpcPackageProjectExportContract(
                contract.ContractKey, contract.Exposure, contract.GeneratedLogicalName, contract.Descriptor, contract.ModelProjection) { MethodPolicies = contract.MethodPolicies }).ToArray();
            sources.AddRange(SunderRpcCSharpImportPrepass.PrepareOwnedSourcePackageApi(compilation.ResolvedPackageId, contracts, version));
        }
        return sources.OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
    }
}
