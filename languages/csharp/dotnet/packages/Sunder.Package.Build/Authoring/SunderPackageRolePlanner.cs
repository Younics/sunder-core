using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Authoring;

internal static class SunderPackageRolePlanner
{
    private const int MaximumSourceBytes = 4_194_304;
    private const int MaximumTypes = 4096;
    private const int MaximumEdges = 65_536;

    internal static SunderPackageRolePlan Plan(
        CSharpCompilation compilation,
        IReadOnlyList<SyntaxTree> ordinaryTrees,
        IEnumerable<INamedTypeSymbol>? portableValues = null,
        IReadOnlyList<SunderPackageMarkupType>? markupTypes = null,
        IReadOnlySet<INamedTypeSymbol>? trustedImportedModelTypes = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(ordinaryTrees);
        var diagnostics = new List<SunderRpcCSharpCompilerDiagnostic>();
        var views = new List<SunderPackageRoleView>();
        var trees = ordinaryTrees.Distinct().OrderBy(tree => tree.FilePath, StringComparer.Ordinal).ToArray();
        if (trees.Any(tree => !compilation.ContainsSyntaxTree(tree)))
        {
            Add("source-invalid", "Every ordinary source tree must belong to the analyzed compilation.", Location.None);
            return Failed();
        }
        if (trees.Sum(tree => (long)Encoding.UTF8.GetByteCount(tree.GetText().ToString())) > MaximumSourceBytes)
        {
            Add("source-limit-exceeded", "Package role analysis exceeds the source byte budget.", Location.None);
            return Failed();
        }
        foreach (var error in compilation.GetDiagnostics().Where(error => error.Severity == DiagnosticSeverity.Error))
            Add("source-invalid", error.ToString(), error.Location);
        if (diagnostics.Count != 0) return Failed();

        var sdk = compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>()
            .SingleOrDefault(assembly => assembly.Identity.Name == typeof(SunderPackageCodeRoleAttribute).Assembly.GetName().Name
                && assembly.Identity.Version == typeof(SunderPackageCodeRoleAttribute).Assembly.GetName().Version);
        if (sdk is null)
        {
            Add("source-invalid", "Package role analysis requires the evaluated Sunder.Sdk reference.", Location.None);
            return Failed();
        }
        var roleAttribute = sdk.GetTypeByMetadataName(typeof(SunderPackageCodeRoleAttribute).FullName!)!;
        var viewAttribute = sdk.GetTypeByMetadataName(typeof(SunderPackageViewAttribute).FullName!)!;
        var settingsAttribute = sdk.GetTypeByMetadataName(typeof(SunderPackageSettingsViewAttribute).FullName!)!;
        var serviceAttribute = sdk.GetTypeByMetadataName(typeof(SunderServiceAttribute).FullName!)!;
        var providerAttribute = sdk.GetTypeByMetadataName(typeof(SunderServiceProviderAttribute).FullName!)!;
        var controls = compilation.GetTypeByMetadataName("Avalonia.Controls.Control");
        if (controls?.ContainingAssembly.Identity.Name != "Avalonia.Controls") controls = null;

        var declarations = new Dictionary<INamedTypeSymbol, List<SyntaxNode>>(SymbolEqualityComparer.Default);
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type) continue;
                if (type.ContainingType is not null)
                {
                    if (Find(type, roleAttribute) is not null || Find(type, viewAttribute) is not null || Find(type, settingsAttribute) is not null)
                        Add("role-invalid", "Nested source moves with its containing type; declare role/view intent on a top-level type.", declaration.GetLocation());
                    continue;
                }
                if (!declarations.TryGetValue(type, out var parts)) declarations.Add(type, parts = []);
                parts.Add(declaration);
            }
            if (tree.GetRoot().DescendantNodes().Any(node => node is GlobalStatementSyntax))
                Add("source-invalid", "Single-project package entry points are generated; move top-level behavior into a declared service or lifecycle component.", tree.GetRoot().GetLocation());
        }
        if (declarations.Count > MaximumTypes)
        {
            Add("source-limit-exceeded", "Package role analysis exceeds the source type budget.", Location.None);
            return Failed();
        }

        var nodes = new Dictionary<INamedTypeSymbol, Node>(SymbolEqualityComparer.Default);
        foreach (var (type, parts) in declarations.OrderBy(pair => Name(pair.Key), StringComparer.Ordinal))
            nodes.Add(type, new(type, parts));
        var values = (portableValues ?? []).Select(Owner).ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var viewIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            var explicitRole = Find(node.Type, roleAttribute);
            if (explicitRole is not null)
            {
                node.Explicit = true;
                var argument = explicitRole.ConstructorArguments.SingleOrDefault();
                if (argument.Value is not int role || !Enum.IsDefined(typeof(SunderPackageCodeRole), role))
                    Add("role-invalid", $"'{Name(node.Type)}' declares an unknown package code role.", At(node));
                else Fix(node, role switch
                {
                    (int)SunderPackageCodeRole.Worker => SunderPackageRoleMembership.Worker,
                    (int)SunderPackageCodeRole.App => SunderPackageRoleMembership.App,
                    _ => SunderPackageRoleMembership.Shared,
                }, "explicit source role");
            }
            node.Portable = values.Contains(node.Type);
            if (node.Portable || node.Type.TypeKind == TypeKind.Interface && Find(node.Type, serviceAttribute) is not null)
                Fix(node, SunderPackageRoleMembership.Shared, "portable RPC value/declaration");
            else if (Find(node.Type, serviceAttribute) is not null || Find(node.Type, providerAttribute) is not null)
                Fix(node, SunderPackageRoleMembership.Worker, "RPC implementation");
            if (SunderPackageDependencyDiscovery.IsLifecycle(node.Type))
                Fix(node, SunderPackageRoleMembership.Worker, "Worker lifecycle implementation");
            if (SunderPackageDependencyDiscovery.IsActivation(node.Type))
                Fix(node, SunderPackageRoleMembership.Worker, "Worker activation implementation");
            if (SunderPackageDependencyDiscovery.IsSettingsSchemaProvider(node.Type))
                Fix(node, SunderPackageRoleMembership.Worker, "Worker settings schema");
            if (SunderPackageDependencyDiscovery.IsStorageMigrationProvider(node.Type)
                || SunderPackageDependencyDiscovery.IsStorageMigrationPreparation(node.Type))
                Fix(node, SunderPackageRoleMembership.Worker, "Worker storage migration implementation");
            if (SunderPackageDependencyDiscovery.IsCallbackHandler(node.Type))
                Fix(node, SunderPackageRoleMembership.Worker, "Worker authentication/callback implementation");

            var view = Find(node.Type, viewAttribute);
            var settings = Find(node.Type, settingsAttribute);
            if (Derives(node.Type, controls)) Fix(node, SunderPackageRoleMembership.App, "Avalonia control");
            if (view is null && settings is null) continue;
            if (view is not null && settings is not null)
                Add("view-invalid", $"'{Name(node.Type)}' cannot be both a package view and its settings view.", At(node));
            if (node.Type.TypeKind != TypeKind.Class || node.Type.IsAbstract || node.Type.IsGenericType || !Derives(node.Type, controls))
                Add("view-invalid", $"'{Name(node.Type)}' must be a concrete, non-generic Avalonia control.", At(node));
            Fix(node, SunderPackageRoleMembership.App, "registered view");
            var metadata = view ?? settings!;
            var modelType = metadata.NamedArguments.FirstOrDefault(pair => pair.Key == "ViewModel").Value.Value as INamedTypeSymbol;
            if (modelType is not null)
            {
                if (modelType.TypeKind != TypeKind.Class || modelType.IsAbstract || modelType.IsGenericType || modelType.ContainingType is not null
                    || !nodes.TryGetValue(modelType, out var modelNode))
                    Add("view-invalid", $"The view model for '{Name(node.Type)}' must be a concrete, top-level ordinary source class.", At(node));
                else Fix(modelNode, SunderPackageRoleMembership.App, "registered view model");
            }
            string? id = null, display = null, icon = null;
            var placement = (int)PackageViewPlacement.Middle;
            if (view is not null)
            {
                id = view.ConstructorArguments.ElementAtOrDefault(0).Value as string;
                display = view.ConstructorArguments.ElementAtOrDefault(1).Value as string;
                icon = view.NamedArguments.FirstOrDefault(pair => pair.Key == "IconPath").Value.Value as string;
                if (view.NamedArguments.FirstOrDefault(pair => pair.Key == "DefaultPlacement").Value.Value is int declaredPlacement)
                    placement = declaredPlacement;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(display) || !Enum.IsDefined(typeof(PackageViewPlacement), placement))
                    Add("view-invalid", $"'{Name(node.Type)}' requires nonblank view identity/name and a supported placement.", At(node));
                else if (!viewIds.Add(id)) Add("view-id-duplicate", $"View identity '{id}' is declared more than once.", At(node));
                if (icon is not null && (string.IsNullOrWhiteSpace(icon) || Path.IsPathRooted(icon) || icon.Contains('\\')
                    || icon.Contains(':') || icon.Split('/').Any(segment => segment is "" or "." or "..")))
                    Add("view-invalid", $"'{Name(node.Type)}' has an invalid package-relative icon path.", At(node));
            }
            views.Add(new(Name(node.Type), modelType is null ? null : Name(modelType), settings is not null, id, display, icon, placement));
        }
        if (views.Count(view => view.IsSettings) > 1)
            Add("settings-view-duplicate", "A package may declare at most one settings view.", Location.None);

        foreach (var use in markupTypes ?? [])
        {
            var type = compilation.Assembly.GetTypeByMetadataName(use.MetadataName);
            // These symbols were proven against regenerated, pinned model source
            // in this compilation. Binding to their shape is not source ownership
            // or permission to consume their authority's service API.
            if (use.IsBindingDataType && type is not null && trustedImportedModelTypes?.Contains(type) == true) continue;
            if (type is null || !nodes.TryGetValue(Owner(type), out var node))
                diagnostics.Add(new("package-authoring.xaml-type-invalid", $"Markup type '{use.MetadataName}' is not ordinary package source.", use.Path, use.Line, use.Column));
            else if (node.Fixed is { } fixedRole && (fixedRole & SunderPackageRoleMembership.App) == 0)
                diagnostics.Add(new("package-authoring.role-conflict", $"Markup cannot reference Worker implementation '{use.MetadataName}'.", use.Path, use.Line, use.Column));
            else if (node.Fixed is null) node.Fixed = SunderPackageRoleMembership.App;
        }

        var edgeCount = 0;
        foreach (var node in nodes.Values)
        {
            // Constructor-interface binding is a semantic dependency too. Without
            // this edge an App helper's sole implementation would default to Worker.
            foreach (var parameter in node.Type.InstanceConstructors.SelectMany(constructor => constructor.Parameters))
            {
                if (parameter.Type is not INamedTypeSymbol { TypeKind: TypeKind.Interface } contract
                    || !nodes.ContainsKey(contract) || SunderPackageDependencyDiscovery.IsRpcDeclaration(contract)) continue;
                var implementations = SunderPackageDependencyDiscovery.Implementations(contract, nodes.Keys);
                if (implementations.Length == 1 && nodes.TryGetValue(implementations[0], out var implementation)
                    && !ReferenceEquals(node, implementation) && node.Edges.TryAdd(implementation, parameter.Locations.FirstOrDefault() ?? At(node))) edgeCount++;
            }
            foreach (var part in node.Parts)
            {
                var model = compilation.GetSemanticModel(part.SyntaxTree);
                foreach (var syntax in part.DescendantNodesAndSelf())
                {
                    if (syntax is TypeSyntax or ExpressionSyntax)
                    {
                        var info = model.GetTypeInfo(syntax);
                        Type(info.Type, syntax.GetLocation());
                        Type(info.ConvertedType, syntax.GetLocation());
                    }
                    if (syntax is ExpressionSyntax or AttributeSyntax)
                    {
                        var symbol = model.GetSymbolInfo(syntax).Symbol;
                        Type(symbol?.ContainingType, syntax.GetLocation());
                        if (symbol is IMethodSymbol method)
                        {
                            Type(method.ReturnType, syntax.GetLocation());
                            foreach (var argument in method.TypeArguments) Type(argument, syntax.GetLocation());
                        }
                    }
                }
            }

            void Type(ITypeSymbol? type, Location location)
            {
                if (type is IArrayTypeSymbol array) { Type(array.ElementType, location); return; }
                if (type is IPointerTypeSymbol pointer) { Type(pointer.PointedAtType, location); return; }
                if (type is not INamedTypeSymbol named) return;
                foreach (var argument in named.TypeArguments) Type(argument, location);
                var owner = Owner(named);
                if (!SymbolEqualityComparer.Default.Equals(owner, node.Type) && nodes.TryGetValue(owner, out var target) && !node.Edges.ContainsKey(target))
                {
                    node.Edges.Add(target, location);
                    edgeCount++;
                }
            }
            if (edgeCount > MaximumEdges)
            {
                Add("source-limit-exceeded", "Package role analysis exceeds the source dependency budget.", At(node));
                return Failed();
            }
        }

        var pending = new Queue<Node>();
        foreach (var node in nodes.Values.Where(node => node.Fixed is not null))
        { node.Roles = node.Fixed!.Value; pending.Enqueue(node); }
        var conflicts = new HashSet<(Node, Node, SunderPackageRoleMembership)>();
        Propagate();
        foreach (var node in nodes.Values.Where(node => node.Roles == SunderPackageRoleMembership.None))
        { node.Roles = SunderPackageRoleMembership.Worker; pending.Enqueue(node); }
        Propagate();
        foreach (var node in nodes.Values.Where(node => node.Roles == SunderPackageRoleMembership.Shared && node.Fixed is null))
            Add("role-ambiguous", $"'{Name(node.Type)}' is needed by both App and Worker. Declare a portable value/shared helper or depend on the generated service API.", At(node));

        return new(nodes.Values.Select(node => new SunderPackageRoleType(Name(node.Type), node.Roles,
                node.Parts.Select(part => part.SyntaxTree.FilePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), node.Portable, node.Explicit)).ToArray(),
            views.OrderBy(view => view.MetadataName, StringComparer.Ordinal).ToArray(), OrderedDiagnostics());

        void Propagate()
        {
            while (pending.TryDequeue(out var source))
            {
                foreach (var (target, location) in source.Edges.OrderBy(pair => Name(pair.Key.Type), StringComparer.Ordinal))
                {
                    if (target.Fixed is { } allowed && (source.Roles & ~allowed) != 0)
                    {
                        if (conflicts.Add((source, target, source.Roles)))
                            Add("role-conflict", $"'{Name(source.Type)}' ({source.Roles}) depends on role-exclusive '{Name(target.Type)}' ({allowed}). Use the generated service API across roles.", location);
                        continue;
                    }
                    var combined = target.Roles | source.Roles;
                    if (combined != target.Roles) { target.Roles = combined; pending.Enqueue(target); }
                }
            }
        }
        void Fix(Node node, SunderPackageRoleMembership role, string reason)
        {
            if (node.Fixed is { } declared && declared != role)
                Add("role-conflict", $"'{Name(node.Type)}' has incompatible role intent ({declared}) and {reason} ownership ({role}).", At(node));
            else node.Fixed = role;
        }
        void Add(string id, string message, Location location)
        {
            var span = location.IsInSource ? location.GetLineSpan() : default;
            diagnostics.Add(new("package-authoring." + id, message, location.IsInSource ? span.Path : null,
                location.IsInSource ? span.StartLinePosition.Line + 1 : null,
                location.IsInSource ? span.StartLinePosition.Character + 1 : null));
        }
        SunderRpcCSharpCompilerDiagnostic[] OrderedDiagnostics() => diagnostics.Distinct()
            .OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Line).ThenBy(value => value.Column)
            .ThenBy(value => value.Id, StringComparer.Ordinal).ThenBy(value => value.Message, StringComparer.Ordinal).ToArray();
        SunderPackageRolePlan Failed() => new([], [], OrderedDiagnostics());
    }

    private static AttributeData? Find(INamedTypeSymbol type, INamedTypeSymbol attribute)
        => type.GetAttributes().FirstOrDefault(value => SymbolEqualityComparer.Default.Equals(value.AttributeClass, attribute));
    private static bool Derives(INamedTypeSymbol type, INamedTypeSymbol? parent)
    {
        if (parent is null) return false;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, parent)) return true;
        return false;
    }
    private static INamedTypeSymbol Owner(INamedTypeSymbol type)
    {
        while (type.ContainingType is not null) type = type.ContainingType;
        return type.OriginalDefinition;
    }
    private static string Name(INamedTypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static Location At(Node node) => node.Parts.OrderBy(part => part.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(part => part.SpanStart).First().GetLocation();

    private sealed class Node(INamedTypeSymbol type, List<SyntaxNode> parts)
    {
        internal INamedTypeSymbol Type { get; } = type;
        internal List<SyntaxNode> Parts { get; } = parts;
        internal Dictionary<Node, Location> Edges { get; } = [];
        internal SunderPackageRoleMembership? Fixed;
        internal SunderPackageRoleMembership Roles;
        internal bool Explicit;
        internal bool Portable;
    }
}
