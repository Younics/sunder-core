using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.ExecutionRuntime.Build;

// The private request carries evaluated MSBuild paths, not an additional source or SDK trust root.
internal static class CoreStackTransportInputs
{
    internal static void Validate(string project, ILookup<string, string> fields, string[] sources, string[] references,
        SunderRpcInputSnapshot inputs)
    {
        var intermediate = Path.TrimEndingDirectorySeparator(SinglePath("intermediate-output"));
        var artifacts = fields.Contains("artifacts-path") ? Path.TrimEndingDirectorySeparator(SinglePath("artifacts-path")) : null;
        var sdkProjectDirectory = Path.GetFullPath(Path.Combine(project, "..", "Sunder.Sdk"));
        var intermediateRoot = artifacts is null ? Path.Combine(project, "obj") : Path.Combine(artifacts, "obj", "Sunder.Sdk.Stacks");
        if (intermediate != intermediateRoot && !GeneratedOutputPathSafety.IsDescendantOf(intermediate, intermediateRoot))
            throw new InvalidDataException("Core transport intermediate output must belong to the evaluated Stack project obj layout.");
        GeneratedOutputPathSafety.EnsureRegularDirectory(intermediate, "Core evaluated intermediate output");
        // The CLI may relocate the artifacts root, but cannot make a language source tree an output root.
        if (artifacts is not null)
        {
            var core = Path.GetFullPath(Path.Combine(project, "../../../../.."));
            var languages = Path.Combine(core, "languages");
            var comparison = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (artifacts == Path.GetPathRoot(artifacts) || artifacts.Equals(languages, comparison)
                || artifacts.StartsWith(languages + Path.DirectorySeparatorChar, comparison)
                || languages.StartsWith(artifacts + Path.DirectorySeparatorChar, comparison))
                throw new InvalidDataException("Core transport artifacts output must be outside Core language source trees and their ancestors.");
            GeneratedOutputPathSafety.EnsureRegularDirectory(artifacts, "Core evaluated artifacts root");
        }

        var generated = fields["generated-source"].Select(Absolute).ToArray();
        if (generated.Length > 3 || generated.Distinct(StringComparer.Ordinal).Count() != generated.Length
            || generated.Any(path => Path.GetDirectoryName(path) != intermediate || !sources.Contains(path, StringComparer.Ordinal)
                || Path.GetFileName(path) is not ("Sunder.Sdk.Stacks.GlobalUsings.g.cs" or "Sunder.Sdk.Stacks.AssemblyInfo.cs"
                    or ".NETCoreApp,Version=v10.0.AssemblyAttributes.cs")))
            throw new InvalidDataException("Core transport requires the exact evaluated SDK-generated Compile inputs within its intermediate output.");
        if (sources.Any(path => !GeneratedOutputPathSafety.IsDescendantOf(path, project) && !generated.Contains(path, StringComparer.Ordinal)))
            throw new InvalidDataException("Core transport source inputs must belong to the Stack project or its exact evaluated SDK-generated Compile set.");

        var sdkReferences = references.Where(path => Path.GetFileName(path) == "Sunder.Sdk.dll").ToArray();
        if (sdkReferences.Length != 1) throw new InvalidDataException("Core transport requires exactly one current Sunder.Sdk reference.");
        var sdkReference = sdkReferences[0];
        var currentSdk = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(typeof(SunderServiceAttribute).Assembly.Location);
        if (fields.Contains("sdk-project") || fields.Contains("sdk-reference"))
        {
            var sdkProject = SinglePath("sdk-project");
            var sdkObj = artifacts is null ? Path.Combine(sdkProjectDirectory, "obj") : Path.Combine(artifacts, "obj", "Sunder.Sdk");
            var sdkBin = artifacts is null ? Path.Combine(sdkProjectDirectory, "bin") : Path.Combine(artifacts, "bin", "Sunder.Sdk");
            if (sdkProject != Path.Combine(sdkProjectDirectory, "Sunder.Sdk.csproj") || SinglePath("sdk-reference") != sdkReference
                || !GeneratedOutputPathSafety.IsDescendantOf(sdkReference, sdkObj) && !GeneratedOutputPathSafety.IsDescendantOf(sdkReference, sdkBin))
                throw new InvalidDataException("Core transport SDK reference must have current Core SDK project provenance and its evaluated obj/bin layout.");
            _ = inputs.Read(sdkProject, 1_048_576, "Core SDK source project");
        }
        else if (sdkReference != currentSdk)
            throw new InvalidDataException("A relocated Core SDK reference requires evaluated SDK project provenance.");

        // A file name, assembly version, or project-reference label alone cannot identify the SDK.
        // Compare compiler-visible metadata with this producer's current SDK without loading candidate code.
        var expected = inputs.Read(currentSdk, 64 * 1024 * 1024, "current Core SDK identity");
        var actual = inputs.Read(sdkReference, 64 * 1024 * 1024, "evaluated Core SDK reference");
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            var dependencies = PlatformContractSourceOperations.ReferencePaths().Where(path => path != currentSdk)
                .Select(path => MetadataReference.CreateFromImage(inputs.Read(path, 64 * 1024 * 1024, "Core SDK metadata dependency"))).ToArray();
            var actualShape = SdkShape(actual, dependencies);
            var expectedShape = SdkShape(expected, dependencies);
            if (!actualShape.SequenceEqual(expectedShape, StringComparer.Ordinal))
                throw new InvalidDataException("Core transport reference differs from the current Sunder.Sdk identity or public shape.");
        }

        string SinglePath(string name) => fields[name].Count() == 1 ? Absolute(fields[name].Single())
            : throw new InvalidDataException($"Core transport request requires exactly one '{name}'.");
    }

    private static string Absolute(string path)
        => Path.IsPathFullyQualified(path) ? GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(path)
            : throw new InvalidDataException("Core transport evaluated paths must be absolute.");

    private static string[] SdkShape(byte[] image, IReadOnlyList<PortableExecutableReference> dependencies)
    {
        try
        {
            var reference = MetadataReference.CreateFromImage(image);
            var compilation = CSharpCompilation.Create("CoreSdkIdentity", references: [.. dependencies, reference]);
            var assembly = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol
                ?? throw new InvalidDataException("Core SDK input must be an assembly.");
            var result = new List<string> { assembly.Identity.ToString() };
            var pending = new Stack<INamespaceOrTypeSymbol>();
            var visited = 0;
            pending.Push(assembly.GlobalNamespace);
            while (pending.TryPop(out var container))
            {
                foreach (var symbol in container.GetMembers())
                {
                    if (++visited > 32_768) throw new InvalidDataException("Core SDK public shape exceeds its member budget.");
                    if (symbol is INamespaceSymbol ns) { pending.Push(ns); continue; }
                    if (symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) continue;
                    var shape = symbol.Kind + ":" + symbol.ToDisplayString(ShapeFormat);
                    shape += ":" + Attributes(symbol.GetAttributes());
                    if (symbol is INamedTypeSymbol type)
                    {
                        shape += $":{type.TypeKind}:{type.IsAbstract}:{type.IsSealed}:{type.IsStatic}:{type.IsReadOnly}:{type.IsRefLikeType}:"
                            + type.EnumUnderlyingType?.ToDisplayString(ShapeFormat) + ":" + type.BaseType?.ToDisplayString(ShapeFormat) + ":"
                            + string.Join(";", type.Interfaces.Select(value => value.ToDisplayString(ShapeFormat)).Order(StringComparer.Ordinal));
                        pending.Push(type);
                    }
                    if (symbol is IMethodSymbol method)
                        shape += ":" + Attributes(method.GetReturnTypeAttributes())
                            + ":" + string.Join(";", method.Parameters.Select(parameter => Attributes(parameter.GetAttributes())));
                    result.Add(shape);
                }
            }
            return result.Order(StringComparer.Ordinal).ToArray();
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException("Core SDK reference has invalid metadata.", exception);
        }
    }

    private static string Attributes(IEnumerable<AttributeData> attributes)
        // Debug/Release and reference/implementation assemblies legitimately differ in method-body annotations.
        => string.Join(";", attributes.Where(attribute => attribute.AttributeClass?.ToDisplayString() is not
                ("System.Diagnostics.DebuggerStepThroughAttribute" or "System.Diagnostics.DebuggerHiddenAttribute"
                or "System.Diagnostics.DebuggerNonUserCodeAttribute" or "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                or "System.Runtime.CompilerServices.IteratorStateMachineAttribute" or "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute"))
            .Select(attribute => attribute.ToString()).Order(StringComparer.Ordinal));

    private static readonly SymbolDisplayFormat ShapeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue
            | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeOptionalBrackets,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
}
