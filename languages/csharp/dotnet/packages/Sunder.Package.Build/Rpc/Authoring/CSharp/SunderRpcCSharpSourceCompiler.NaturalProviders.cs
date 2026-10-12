using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal enum SunderRpcCSharpProviderArgument { Request, Context, Cancellation }

internal sealed record SunderRpcCSharpNaturalProviderMethod(
    string ServiceId, string MethodId, IMethodSymbol WireMethod, IMethodSymbol InvocationMethod,
    IReadOnlyList<SunderRpcCSharpProviderArgument> Arguments, bool Completion, bool Stream)
{
    internal bool CompleteOnResponseContentRetirement { get; init; }
}

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    private static IReadOnlyList<SunderRpcCSharpNaturalProviderMethod> BindNaturalProvider(CSharpCompilation compilation,
        INamedTypeSymbol provider, SunderRpcCSharpImportedProviderSurface surface, IReadOnlySet<SyntaxTree> sourceTrees,
        AttributeSymbols attributes)
    {
        ValidateProviderClass(provider, sourceTrees);
        var contract = surface.Contract;
        var model = SunderRpcCSharpImportPrepass.GeneratorModel(contract);
        var supplemental = compilation.SyntaxTrees.Where(tree => !sourceTrees.Contains(tree)).ToHashSet();
        var context = attributes.Service.ContainingAssembly.GetTypeByMetadataName(typeof(SunderRpcInvocationContext).FullName!)!;
        var cancellation = context.GetMembers(nameof(SunderRpcInvocationContext.CancellationToken)).OfType<IPropertySymbol>().Single().Type;
        var result = new List<SunderRpcCSharpNaturalProviderMethod>();
        foreach (var service in model.Services)
        {
            var natural = contract.NaturalServices.Single(value => value.ServiceId == service.ServiceId);
            var api = compilation.GetTypeByMetadataName(natural.InterfaceMetadataName);
            var wire = compilation.GetTypeByMetadataName(contract.GeneratedLogicalName + "." + service.ProviderTypeName);
            if (api is null || wire is null || !IsOrdinarySourceSymbol(api, supplemental) || !IsOrdinarySourceSymbol(wire, supplemental)
                || api.AllInterfaces.Length != 0 || api.GetMembers().Length != service.Methods.Count)
                throw Failure("rpc-authoring.handler-incomplete", $"Natural provider interface '{natural.InterfaceMetadataName}' must exactly project the locked descriptor.", provider);
            if (provider.GetMembers(natural.AdapterTypeName).Any(member => HasOrdinarySourceDeclaration(member, sourceTrees)))
                throw Failure("rpc-authoring.generated-name-collision", $"Provider '{provider.Name}' declares reserved generated adapter '{natural.AdapterTypeName}'.", provider);
            foreach (var method in service.Methods)
            {
                var declaration = api.GetMembers(method.MethodName).OfType<IMethodSymbol>().SingleOrDefault();
                var wireMethod = wire.GetMembers(method.MethodName).OfType<IMethodSymbol>().SingleOrDefault();
                var descriptor = contract.Descriptor.Services.Single(value => value.ServiceId == service.ServiceId).Methods.Single(value => value.MethodId == method.MethodId);
                var empty = SunderRpcCSharpImportPrepass.IsEmptyRoot(contract.Descriptor, descriptor.RequestSchemaReference);
                var completion = method.Kind == SunderRpcMethodKind.Unary && SunderRpcCSharpImportPrepass.IsEmptyRoot(contract.Descriptor, descriptor.OutputSchemaReference);
                if (declaration is null || wireMethod is null || wireMethod.Parameters.Length != 3
                    || !SymbolEqualityComparer.Default.Equals(wireMethod.Parameters[0].Type, context)
                    || !SymbolEqualityComparer.Default.Equals(wireMethod.Parameters[2].Type, cancellation)
                    || declaration.IsStatic || declaration.IsGenericMethod || declaration.MethodKind != MethodKind.Ordinary
                    || declaration.Parameters.Length != (empty ? 1 : 2)
                    || declaration.Parameters.Any(parameter => parameter.RefKind != RefKind.None || parameter.IsParams)
                    || !SymbolEqualityComparer.Default.Equals(declaration.Parameters[^1].Type, cancellation)
                    || !empty && !SymbolEqualityComparer.IncludeNullability.Equals(declaration.Parameters[0].Type, wireMethod.Parameters[1].Type)
                    || !(completion ? IsCompletionReturn(declaration.ReturnType, wireMethod.ReturnType.ContainingAssembly, valueTaskOnly: true)
                        : SymbolEqualityComparer.IncludeNullability.Equals(declaration.ReturnType, wireMethod.ReturnType)))
                    throw Failure("rpc-authoring.handler-incomplete", $"Natural method '{service.ServiceId}/{method.MethodId}' differs from the exact locked descriptor signature.", provider);

                IReadOnlyList<IParameterSymbol> expectedValues = empty ? [] : [declaration.Parameters[0]];
                var candidates = NaturalProviderCandidates(provider, declaration).Select(candidate => (Method: candidate,
                        Arguments: MatchNaturalProviderMethod(candidate, declaration, expectedValues, context, cancellation)))
                    .Where(candidate => candidate.Arguments is not null).ToArray();
                if (candidates.Length != 1)
                    throw Failure("rpc-authoring.handler-incomplete",
                        candidates.Length == 0
                            ? $"Provider '{provider.Name}' requires '{declaration.Name}' matching locked method '{contract.Descriptor.ContractId}/{service.ServiceId}/{method.MethodId}', with optional invocation context and cancellation. No other API or wire-name fallback is used."
                            : $"Provider '{provider.Name}' has ambiguous source methods for locked method '{service.ServiceId}/{method.MethodId}'; keep one exact value signature after removing context and cancellation.",
                        candidates.Length == 0 ? [provider] : candidates.Select(candidate => (ISymbol)candidate.Method).ToArray());
                var selected = candidates[0];
                var metadata = selected.Method.GetAttributes().SingleOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributes.Method));
                if (metadata is not null && (metadata.NamedArguments.Length == 0
                    || metadata.NamedArguments.Any(pair => pair.Key != nameof(Sunder.Sdk.Services.SunderMethodAttribute.CompleteOnResponseContentRetirement))))
                    throw Failure("rpc-authoring.handler-incomplete", $"Imported method '{selected.Method.Name}' takes its wire identity and semantics from the locked descriptor; remove SunderMethod from the implementation.", selected.Method);
                var invocation = selected.Method.MethodKind == MethodKind.ExplicitInterfaceImplementation ? declaration : selected.Method;
                result.Add(new(service.ServiceId, method.MethodId, wireMethod, invocation, selected.Arguments!, completion,
                    method.Kind == SunderRpcMethodKind.ServerStream)
                { CompleteOnResponseContentRetirement = ResponseContentCompletion(selected.Method, contract.Descriptor, service.ServiceId, method.MethodId) });
            }
        }
        return result;
    }

    private static IEnumerable<IMethodSymbol> NaturalProviderCandidates(INamedTypeSymbol provider, IMethodSymbol contractMethod)
    {
        if (provider.FindImplementationForInterfaceMember(contractMethod) is IMethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation } implementation)
        {
            yield return implementation;
            yield break;
        }
        var visible = new List<IMethodSymbol>();
        for (var current = provider; current is not null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers(contractMethod.Name).OfType<IMethodSymbol>())
            {
                // Match ordinary C# hiding/overriding before considering return
                // shapes. A wrong derived return must not fall back to a base API.
                if (visible.Any(nearer => nearer.Arity == method.Arity && nearer.Parameters.Length == method.Parameters.Length
                    && nearer.Parameters.Zip(method.Parameters).All(pair => pair.First.RefKind == pair.Second.RefKind
                        && SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type)))) continue;
                visible.Add(method);
            }
        }
        foreach (var method in visible.Where(method => method.MethodKind == MethodKind.Ordinary && method.DeclaredAccessibility == Accessibility.Public)) yield return method;
    }

    private static IReadOnlyList<SunderRpcCSharpProviderArgument>? MatchNaturalProviderMethod(IMethodSymbol method,
        IMethodSymbol declaration, IReadOnlyList<IParameterSymbol> expectedValues, INamedTypeSymbol context, ITypeSymbol cancellation)
    {
        if (method.IsStatic || method.IsAbstract || method.IsGenericMethod || method.ReturnsByRef || method.ReturnsByRefReadonly
            || method.Parameters.Any(parameter => parameter.RefKind != RefKind.None || parameter.IsParams)
            || !MatchesNaturalReturn(method.ReturnType, declaration.ReturnType)) return null;
        var arguments = new List<SunderRpcCSharpProviderArgument>();
        var values = 0; var hasContext = false; var hasCancellation = false;
        foreach (var parameter in method.Parameters)
        {
            if (hasCancellation) return null;
            if (SymbolEqualityComparer.Default.Equals(parameter.Type, context))
            {
                if (hasContext) return null;
                hasContext = true; arguments.Add(SunderRpcCSharpProviderArgument.Context);
            }
            else if (SymbolEqualityComparer.Default.Equals(parameter.Type, cancellation))
            {
                hasCancellation = true; arguments.Add(SunderRpcCSharpProviderArgument.Cancellation);
            }
            else
            {
                if (values >= expectedValues.Count || !SymbolEqualityComparer.IncludeNullability.Equals(parameter.Type, expectedValues[values++].Type)) return null;
                arguments.Add(SunderRpcCSharpProviderArgument.Request);
            }
        }
        return values == expectedValues.Count ? arguments : null;
    }

    private static bool MatchesNaturalReturn(ITypeSymbol actual, ITypeSymbol expected)
    {
        if (actual is not INamedTypeSymbol value || expected is not INamedTypeSymbol target
            || value.NullableAnnotation == NullableAnnotation.Annotated) return false;
        if (target.Name == "IAsyncEnumerable") return SymbolEqualityComparer.IncludeNullability.Equals(value, target);
        if (target.Arity == 0) return IsCompletionReturn(value, target.ContainingAssembly, valueTaskOnly: false);
        return value.Arity == 1 && value.Name is "Task" or "ValueTask" && value.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
            && SymbolEqualityComparer.Default.Equals(value.ContainingAssembly, target.ContainingAssembly)
            && SymbolEqualityComparer.IncludeNullability.Equals(value.TypeArguments[0], target.TypeArguments[0]);
    }

    private static bool IsCompletionReturn(ITypeSymbol type, IAssemblySymbol assembly, bool valueTaskOnly)
        => type is INamedTypeSymbol { Arity: 0, NullableAnnotation: not NullableAnnotation.Annotated } named
            && (named.Name == "ValueTask" || !valueTaskOnly && named.Name == "Task")
            && named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
            && SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly);
}
