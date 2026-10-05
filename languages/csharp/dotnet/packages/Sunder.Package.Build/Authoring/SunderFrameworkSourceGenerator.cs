using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;

namespace Sunder.Package.Build.Authoring;

/// <summary>Runs the evaluated SDK regex or MVVM Toolkit generators independently for each role.</summary>
internal sealed class SunderFrameworkSourceGenerator : IDisposable
{
    internal const string RegexAssemblyFileName = "System.Text.RegularExpressions.Generator.dll";
    internal const string MvvmAssemblyFileName = "CommunityToolkit.Mvvm.SourceGenerators.dll";
    internal const string RegexSourcePrefix = "regex-generated/";
    internal const string MvvmSourcePrefix = "mvvm-generated/";
    private readonly GeneratorContext _context;
    private readonly ISourceGenerator[] _generators;
    private readonly string _sourcePrefix;
    private readonly string _diagnostic;
    private readonly Options _options;
    internal SunderPackageFileEvidence AssemblyInput { get; }

    internal SunderFrameworkSourceGenerator(string path, ITaskItem[] properties)
    {
        path = Path.GetFullPath(path);
        var name = Path.GetFileName(path);
        (_sourcePrefix, _diagnostic) = name switch
        {
            RegexAssemblyFileName => (RegexSourcePrefix, "package-authoring.regex-generator-invalid"),
            MvvmAssemblyFileName => (MvvmSourcePrefix, "package-authoring.mvvm-generator-invalid"),
            _ => throw new InvalidDataException("package-authoring.framework-generator-invalid: Unsupported framework generator."),
        };
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, 64 * 1024 * 1024, "framework generator");
        AssemblyInput = new(path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        _options = new(properties);
        _context = new GeneratorContext(Path.GetDirectoryName(path)!);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var assembly = _context.LoadFromStream(stream);
            if (assembly.GetName().Name != Path.GetFileNameWithoutExtension(name))
                throw new InvalidDataException(_diagnostic + ": Expected the evaluated framework generator assembly.");
            // The toolkit assembly also contains IDE code fixes. Read generator
            // declarations from metadata so builds never load their Workspaces dependencies.
            var types = GeneratorTypeNames(bytes).Select(name => assembly.GetType(name, throwOnError: true)!).ToArray();
            if (types.Length is 0 or > 32) throw new InvalidDataException(_diagnostic + ": Invalid framework generator set.");
            if (types.Any(type => type.IsAbstract || !typeof(IIncrementalGenerator).IsAssignableFrom(type) || !type.IsDefined(typeof(GeneratorAttribute), inherit: false)))
                throw new InvalidDataException(_diagnostic + ": Invalid incremental generator declaration.");
            _generators = types.Select(type => ((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator()).ToArray();
        }
        catch { _context.Unload(); throw; }
    }

    internal IReadOnlyList<SunderRpcCSharpSourceFile> Generate(CSharpCompilation compilation)
    {
        // The strict prepass rechecks the same source after generated members have
        // resolved. Previous output is not a new input to the SDK generator.
        compilation = compilation.RemoveSyntaxTrees(compilation.SyntaxTrees.Where(tree => IsRoleGenerated(tree.FilePath)));
        var parse = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? new(LanguageVersion.CSharp14);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(_generators, parseOptions: parse, optionsProvider: _options);
        var result = driver.RunGenerators(compilation).GetRunResult();
        foreach (var diagnostic in result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(_diagnostic + ": " + diagnostic);
        if (result.Results.Any(value => value.Exception is not null))
            throw new InvalidDataException(_diagnostic + ": The evaluated framework generator failed.",
                result.Results.First(value => value.Exception is not null).Exception);
        var sources = result.Results.SelectMany((value, index) => value.GeneratedSources.Select(source =>
                new SunderRpcCSharpSourceFile(_sourcePrefix + (_generators.Length == 1 ? "" : index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/")
                    + source.HintName, source.SourceText.ToString())))
            .OrderBy(value => value.Path, StringComparer.Ordinal).ToArray();
        if (sources.Length > 8192 || sources.Sum(source => (long)System.Text.Encoding.UTF8.GetByteCount(source.Text)) > 64L * 1024 * 1024)
            throw new InvalidDataException(_diagnostic + ": Framework-generated source exceeds the output budget.");
        return sources;
    }

    public void Dispose() => _context.Unload();

    internal static bool IsRoleGenerated(string path)
        => path.StartsWith(RegexSourcePrefix, StringComparison.Ordinal) || path.StartsWith(MvvmSourcePrefix, StringComparison.Ordinal);

    private static string[] GeneratorTypeNames(byte[] bytes)
    {
        using var reader = new PEReader(new MemoryStream(bytes, writable: false));
        var metadata = reader.GetMetadataReader();
        return metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Where(type => type.GetCustomAttributes().Any(handle =>
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) return false;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) return false;
            var reference = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            return metadata.GetString(reference.Namespace) == "Microsoft.CodeAnalysis" && metadata.GetString(reference.Name) == "GeneratorAttribute";
        })).Select(type => metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class Options(ITaskItem[] properties) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global = new Values(properties.ToDictionary(property => "build_property." + property.ItemSpec,
            property => property.GetMetadata("Value"), StringComparer.OrdinalIgnoreCase));
        private static readonly AnalyzerConfigOptions Empty = new Values(new Dictionary<string, string>());
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }

    private sealed class Values(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }

    private sealed class GeneratorContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(Compilation).Assembly.GetName().Name) return typeof(Compilation).Assembly;
            if (name.Name == typeof(CSharpCompilation).Assembly.GetName().Name) return typeof(CSharpCompilation).Assembly;
            var path = System.IO.Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
