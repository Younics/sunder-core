using Microsoft.CodeAnalysis;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Authoring;

internal static class SunderPackageCompositionPlanner
{
    internal static SunderPackageCompositionPlan Plan(SunderPackageAuthoringAnalysis analysis)
    {
        var compilation = analysis.Compilation.SourceCompilation!;
        var sourceTypes = Types(compilation.Assembly.GlobalNamespace).Where(type => type.DeclaringSyntaxReferences
            .Any(reference => analysis.OrdinarySourcePaths.Contains(reference.SyntaxTree.FilePath))).OrderBy(Name, StringComparer.Ordinal).ToArray();
        if (sourceTypes.Length > 8192)
            return new([], [], [new("package-authoring.di-limit-exceeded", "Default composition exceeds its source type budget.", null, null, null)]);
        var roles = analysis.RolePlan.Types.ToDictionary(type => type.MetadataName, type => type.Roles, StringComparer.Ordinal);
        var apis = analysis.Compilation.Contracts.Where(contract => contract.ServiceType.TypeKind == TypeKind.Class)
            .ToDictionary(SunderRpcCSharpServiceApiEmitter.ApiType, StringComparer.Ordinal);
        var diagnostics = new List<SunderRpcCSharpCompilerDiagnostic>();
        var settingsProviders = sourceTypes.Where(type => !type.IsAbstract && SunderPackageDependencyDiscovery.IsSettingsSchemaProvider(type)).ToArray();
        if (settingsProviders.Length > 1)
            foreach (var type in settingsProviders)
                Add("settings-schema-duplicate", "A package may declare only one Worker settings schema provider.", type.Locations.FirstOrDefault() ?? Location.None);
        var authHandlers = sourceTypes.Where(type => !type.IsAbstract && SunderPackageDependencyDiscovery.IsAuthHandler(type)).ToArray();
        if (authHandlers.Length > 1)
            foreach (var type in authHandlers)
                Add("auth-handler-duplicate", "A package may declare only one Worker authentication handler.", type.Locations.FirstOrDefault() ?? Location.None);
        var worker = ForRole(SunderPackageRoleMembership.Worker);
        var app = ForRole(SunderPackageRoleMembership.App);
        return new(worker, app, diagnostics.Distinct().OrderBy(value => value.Path, StringComparer.Ordinal)
            .ThenBy(value => value.Line).ThenBy(value => value.Column).ThenBy(value => value.Id, StringComparer.Ordinal).ToArray());

        IReadOnlyList<SunderPackageServiceConstruction> ForRole(SunderPackageRoleMembership role)
        {
            var result = new List<SunderPackageServiceConstruction>();
            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var visiting = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var transient = analysis.RolePlan.Views.SelectMany(view => new[] { view.MetadataName, view.ViewModelMetadataName })
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            var roots = role == SunderPackageRoleMembership.Worker
                ? analysis.Compilation.Contracts.SelectMany(contract => contract.Providers).Select(provider => provider.SourceType)
                    .Concat(analysis.Compilation.ImportedProviders.Select(provider => provider.Provider.SourceType))
                    .Concat(sourceTypes.Where(type => !type.IsAbstract && (SunderPackageDependencyDiscovery.IsLifecycle(type)
                        || SunderPackageDependencyDiscovery.IsActivation(type)
                        || SunderPackageDependencyDiscovery.IsCallbackHandler(type)
                        || SunderPackageDependencyDiscovery.IsSettingsSchemaProvider(type))))
                : sourceTypes.Where(type => transient.Contains(Name(type)));
            // All exposed default Worker APIs must be resolvable, even when only
            // package-local code obtains them after activation.
            if (role == SunderPackageRoleMembership.Worker)
                roots = roots.Concat(apis.Values.Where(contract => !SunderRpcCSharpServiceApiEmitter.RequiresContext(contract)
                    || contract.RuntimeUse is not null).Select(contract => contract.ServiceType));
            foreach (var root in roots.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).OrderBy(Name, StringComparer.Ordinal))
                Visit(root, root.Locations.FirstOrDefault() ?? Location.None);
            return result;

            void Visit(INamedTypeSymbol type, Location location)
            {
                if (visiting.Count >= 128)
                {
                    Add("di-limit-exceeded", "Default composition exceeds its constructor dependency depth budget.", location);
                    return;
                }
                if (visiting.Contains(type))
                {
                    Add("di-cycle", $"The constructor dependency graph contains a cycle at '{Name(type)}'.", location);
                    return;
                }
                if (!visited.Add(type)) return;
                if (!roles.TryGetValue(Name(SunderPackageDependencyDiscovery.Owner(type)), out var membership) || (membership & role) == 0)
                {
                    Add("di-role-conflict", $"'{Name(type)}' is not available to {role} composition.", location);
                    return;
                }
                if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || type.IsGenericType || !compilation.IsSymbolAccessibleWithin(type, compilation.Assembly))
                {
                    Add("di-constructor-invalid", $"Default composition requires an accessible concrete, non-generic source class: '{Name(type)}'.", location);
                    return;
                }
                var preferred = type.InstanceConstructors.Where(SunderPackageDependencyDiscovery.IsPreferredConstructor).ToArray();
                var constructors = preferred.Length == 0
                    ? type.InstanceConstructors.Where(constructor => compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly)).ToArray()
                    : preferred;
                if (constructors.Length != 1 || !compilation.IsSymbolAccessibleWithin(constructors[0], compilation.Assembly))
                {
                    Add("di-constructor-invalid", $"'{Name(type)}' requires one accessible constructor or one accessible ActivatorUtilitiesConstructor selection; found {constructors.Length} candidates.", location);
                    return;
                }
                var constructor = constructors[0];
                if (HasRequiredMembers(type) && !constructor.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))
                {
                    Add("di-constructor-invalid", $"'{Name(type)}' has required members that its constructor does not initialize.", location);
                    return;
                }
                var arguments = new List<SunderPackageConstructorArgument>();
                visiting.Add(type);
                foreach (var parameter in constructor.Parameters)
                {
                    var at = parameter.Locations.FirstOrDefault() ?? location;
                    if (parameter.RefKind != RefKind.None || parameter.IsParams)
                    {
                        Add("di-parameter-invalid", $"'{Name(type)}.{parameter.Name}' cannot use ref/out/in/params in default composition.", at);
                        continue;
                    }
                    if (parameter.Type is not INamedTypeSymbol requested)
                    {
                        Missing(parameter, at); continue;
                    }
                    var deferred = SymbolEqualityComparer.Default.Equals(requested.OriginalDefinition,
                        compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly.GetTypeByMetadataName("System.Lazy`1"));
                    if (deferred)
                    {
                        if (requested.TypeArguments[0] is not INamedTypeSymbol value) { Missing(parameter, at); continue; }
                        requested = value;
                    }
                    var name = Name(requested);
                    if (IsHostService(requested, role)) { arguments.Add(new(parameter, requested, deferred)); continue; }
                    if (apis.TryGetValue(name, out var api))
                    {
                        var hasContext = SunderRpcCSharpServiceApiEmitter.RequiresContext(api);
                        if (role == SunderPackageRoleMembership.App && SunderRpcAuthoringDocuments.ResolveAppUse(api.Exposure, api.Descriptor, api.AppUse) is null)
                        {
                            Add("di-use-required", $"App injection of '{name}' requires explicit App-use intent for this public service.", at); continue;
                        }
                        if (role == SunderPackageRoleMembership.Worker && hasContext && api.RuntimeUse is null)
                        {
                            Add("di-context-required", $"'{name}' has Host-context methods. Declare Runtime-use intent for broker-backed calls or use a context-free local service.", at); continue;
                        }
                        arguments.Add(new(parameter, requested, deferred));
                        if (role == SunderPackageRoleMembership.Worker && SunderRpcCSharpServiceApiEmitter.HasLocalMethods(api)) Visit(api.ServiceType, at);
                        continue;
                    }
                    INamedTypeSymbol? dependency = null;
                    if (requested.TypeKind == TypeKind.Class && sourceTypes.Contains(requested, SymbolEqualityComparer.Default)) dependency = requested;
                    else if (requested.TypeKind == TypeKind.Interface && sourceTypes.Contains(requested, SymbolEqualityComparer.Default)
                        && !SunderPackageDependencyDiscovery.IsRpcDeclaration(requested))
                    {
                        var candidates = SunderPackageDependencyDiscovery.Implementations(requested, sourceTypes);
                        if (candidates.Length > 1)
                        {
                            Add("di-ambiguous", $"'{name}' has multiple source implementations: {string.Join(", ", candidates.Select(Name))}.", at); continue;
                        }
                        dependency = candidates.SingleOrDefault();
                    }
                    if (dependency is null) { Missing(parameter, at); continue; }
                    if (!transient.Contains(Name(type)) && transient.Contains(Name(dependency)))
                    {
                        Add("di-lifetime-conflict", $"Singleton '{Name(type)}' cannot capture transient view/view-model '{Name(dependency)}'.", at); continue;
                    }
                    arguments.Add(new(parameter, dependency, deferred));
                    Visit(dependency, at);
                }
                visiting.Remove(type);
                result.Add(new(type, constructor, transient.Contains(Name(type)), SunderPackageDependencyDiscovery.IsLifecycle(type), arguments));
            }

            void Missing(IParameterSymbol parameter, Location location)
            {
                if (!parameter.HasExplicitDefaultValue)
                    Add("di-unresolved", $"No default constructor binding exists for '{Name(parameter.Type)} {parameter.Name}'. Use an unambiguous source dependency or explicit composition.", location);
            }
        }

        void Add(string id, string message, Location location)
        {
            var span = location.IsInSource ? location.GetLineSpan() : default;
            diagnostics.Add(new("package-authoring." + id, message, location.IsInSource ? span.Path : null,
                location.IsInSource ? span.StartLinePosition.Line + 1 : null, location.IsInSource ? span.StartLinePosition.Character + 1 : null));
        }
    }

    private static bool IsHostService(INamedTypeSymbol type, SunderPackageRoleMembership role)
        => (type.ToDisplayString() is "Sunder.Sdk.Abstractions.IPackageContext" or "Sunder.Sdk.Rpc.ISunderRpcClient"
                || role == SunderPackageRoleMembership.App && type.ToDisplayString() is "Sunder.Sdk.Notifications.IPackageNotificationService"
                    or "Sunder.Sdk.Abstractions.IBackgroundProcessQueue" or "Sunder.Sdk.Abstractions.IPackageSettingsNavigationService"
                    or "Sunder.Sdk.Abstractions.IPackageShellViewService")
            && type.ContainingAssembly.Identity.Name == "Sunder.Sdk"
            && type.ContainingAssembly.Identity.Version == typeof(Sunder.Sdk.Abstractions.IPackageContext).Assembly.GetName().Version;

    private static string Name(ITypeSymbol type) => SunderPackageDependencyDiscovery.Name(type);

    private static bool HasRequiredMembers(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true })) return true;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol owner)
    {
        foreach (var type in owner.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in Types(type)) yield return nested;
        }
        if (owner is INamespaceSymbol ns)
            foreach (var child in ns.GetNamespaceMembers())
                foreach (var type in Types(child)) yield return type;
    }
}
