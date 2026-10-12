using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json.Serialization;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    private sealed partial class DefinitionGraph
    {
        private static bool IsAutoInitProperty(IPropertySymbol property)
            => property.GetMethod?.DeclaredAccessibility == Accessibility.Public
               && property.SetMethod is { IsInitOnly: true, DeclaredAccessibility: Accessibility.Public }
               && property.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is PropertyDeclarationSyntax
               {
                   ExpressionBody: null, AccessorList.Accessors.Count: 2,
               } declaration && declaration.AccessorList.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null));

        private SunderRpcCSharpSourceConstructorModel? BindRecordConstructor(
            INamedTypeSymbol type, IReadOnlyList<IPropertySymbol> properties, IReadOnlyList<IPropertySymbol> knownProperties)
        {
            // Already explicit required DTOs retain their qualified codec/metadata
            // path (including existing union handling). New inference is bounded to
            // values that actually need constructor presence or optional reset.
            var explicitMembers = properties.Any(IsExplicitServiceMember);
            if (properties.All(property => property.IsRequired) && !explicitMembers && properties.Count == knownProperties.Count) return null;
            var annotated = type.InstanceConstructors.Where(constructor =>
                HasSystemAttribute(constructor, "System.Text.Json.Serialization.JsonConstructorAttribute")).ToArray();
            if (annotated.Length > 1) throw Invalid("requires exactly one selected JSON constructor", type);
            // Preserve direct-population DTOs. A convenience overload is not permission
            // to infer presence for an otherwise ordinary parameterless POCO.
            var constructors = type.InstanceConstructors.Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public
                && !(constructor.IsImplicitlyDeclared && constructor.Parameters.Length == 0 && type.IsValueType)).ToArray();
            var selected = annotated.SingleOrDefault();
            if (selected is null)
            {
                var parameterless = constructors.FirstOrDefault(constructor => constructor.Parameters.Length == 0)
                    ?? (explicitMembers && constructors.Length == 0 && type.IsValueType
                        ? type.InstanceConstructors.FirstOrDefault(constructor => constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0) : null);
                if (parameterless is not null)
                {
                    if (!explicitMembers) return null;
                    selected = parameterless;
                }
                else if (constructors.Length != 1)
                {
                    if (properties.Any(property => !property.IsRequired && !UnwrapOptional(property.Type).IsOptional))
                        throw Invalid("requires a unique constructor binding or an explicit JsonConstructor", type);
                    return null;
                }
                else selected = constructors[0];
            }
            if (selected.Parameters.Length == 0 && !explicitMembers) return null;
            if (!IsAssemblyAccessible(selected) || selected.Parameters.Length > 256 || properties.Count > 256)
                throw Invalid("requires an accessible constructor and at most 256 constructor parameters/properties", selected);
            if (type.GetAttributes().Any(IsUnsupportedJsonAttribute)
                || properties.Any(property => property.GetAttributes().Any(attribute => IsUnsupportedJsonAttribute(attribute)
                    && !AllowedServiceIgnore(property, attribute))))
                throw Invalid("cannot combine constructor-bound source codecs with JSON representation overrides other than JsonPropertyName", type);
            if (type.GetMembers().OfType<IFieldSymbol>().Any(field => !field.IsStatic && field.DeclaredAccessibility == Accessibility.Public)
                || HasInheritedProperties(type))
                throw Invalid("cannot infer a constructor-bound value with public fields or inherited instance properties", type);

            var bound = new List<IPropertySymbol?>();
            var matched = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
            foreach (var parameter in selected.Parameters)
            {
                var matches = knownProperties.Where(property => string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
                    && CanPassPropertyToConstructor(property.Type, parameter.Type)).ToArray();
                if (parameter.RefKind != RefKind.None || parameter.IsParams || matches.Length != 1
                    || !matched.Add(matches[0]))
                    throw Invalid($"constructor parameter '{parameter.Name}' must bind exactly one same-typed source property by CLR name", parameter);
                if (!properties.Contains(matches[0], SymbolEqualityComparer.Default))
                {
                    if (!parameter.HasExplicitDefaultValue || !IsBoundedDefault(parameter))
                        throw Invalid($"ignored constructor parameter '{parameter.Name}' requires an explicit representable default; excluded persistence values cannot be supplied by the service payload", parameter);
                    bound.Add(null);
                }
                else bound.Add(matches[0]);
            }
            var setsRequired = HasSystemAttribute(selected, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
            if (!setsRequired && knownProperties.Any(property => property.IsRequired && !properties.Contains(property, SymbolEqualityComparer.Default)))
                throw Invalid("cannot construct an ignored required property without a constructor that sets required members", selected);
            var initializers = new List<IPropertySymbol>();
            foreach (var property in properties)
            {
                if (property.IsIndexer || property.GetMethod is not { } getter || !IsAssemblyAccessible(getter))
                    throw Invalid($"property '{property.Name}' requires an accessible non-indexed getter", property);
                // Explicit optional absence must survive authored constructor/default
                // initializers, including constructors that manufacture a present value.
                if (!bound.Contains(property, SymbolEqualityComparer.Default)
                    || UnwrapOptional(property.Type).IsOptional || property.IsRequired && !setsRequired)
                {
                    if (property.SetMethod is not { } setter || !IsAssemblyAccessible(setter))
                        throw Invalid($"property '{property.Name}' requires an accessible setter/init accessor to preserve construction and optional presence", property);
                    initializers.Add(property);
                }
            }
            return new(selected, bound, initializers);

            static CompilerFailure Invalid(string reason, ISymbol symbol)
                => Failure("rpc-authoring.value-invalid", $"RPC constructor-bound value {reason}.", symbol);
            static bool HasInheritedProperties(INamedTypeSymbol value)
            {
                for (var parent = value.BaseType; parent is not null && parent.SpecialType != SpecialType.System_Object; parent = parent.BaseType)
                    if (parent.GetMembers().OfType<IPropertySymbol>().Any(property => !property.IsStatic && property.DeclaredAccessibility == Accessibility.Public)) return true;
                return false;
            }
            static bool IsUnsupportedJsonAttribute(AttributeData attribute)
                => attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization"
                   && attribute.AttributeClass.Name != "JsonPropertyNameAttribute";
        }

        private bool IsExplicitServiceMember(IPropertySymbol property) => HasAttribute(property, attributes.ServiceMember);

        private bool IsServiceProperty(IPropertySymbol property)
        {
            var ignore = property.GetAttributes().FirstOrDefault(IsJsonIgnore);
            if (ignore is null) return true;
            var condition = IgnoreCondition(ignore);
            if (condition == JsonIgnoreCondition.Always) return IsExplicitServiceMember(property);
            if (condition == JsonIgnoreCondition.WhenWritingDefault && UnwrapOptional(property.Type).IsOptional) return true;
            if (condition != JsonIgnoreCondition.Never)
                throw Failure("rpc-authoring.value-invalid", $"RPC property '{property.Name}' permits JsonIgnore.WhenWritingDefault only with an explicit SunderRpcOptional<T> value; other conditional omission cannot change service presence.", property);
            return true;
        }

        private bool AllowedServiceIgnore(IPropertySymbol property, AttributeData attribute)
            => IsJsonIgnore(attribute) && (IgnoreCondition(attribute) == JsonIgnoreCondition.Always && IsExplicitServiceMember(property)
                || IgnoreCondition(attribute) == JsonIgnoreCondition.WhenWritingDefault && UnwrapOptional(property.Type).IsOptional);

        private static bool IsJsonIgnore(AttributeData attribute)
            => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute"
                && attribute.AttributeClass.ContainingAssembly.Identity.Name == "System.Text.Json";

        private static JsonIgnoreCondition IgnoreCondition(AttributeData attribute)
            => attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Condition").Value.Value is int value
                ? (JsonIgnoreCondition)value : JsonIgnoreCondition.Always;

        private static bool IsBoundedDefault(IParameterSymbol parameter)
            => parameter.ExplicitDefaultValue is null or string or char or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

        private static bool CanPassPropertyToConstructor(ITypeSymbol property, ITypeSymbol parameter)
            => SymbolEqualityComparer.IncludeNullability.Equals(property, parameter)
               // Only widen the outer reference annotation. The serialized property
               // still owns presence/nullability, and all nested type arguments must
               // match exactly. No variance, numeric/user conversion or null narrowing.
               || property.IsReferenceType && property.NullableAnnotation == NullableAnnotation.NotAnnotated
                  && parameter.NullableAnnotation == NullableAnnotation.Annotated
                  && SymbolEqualityComparer.IncludeNullability.Equals(property,
                      parameter.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
    }
}
