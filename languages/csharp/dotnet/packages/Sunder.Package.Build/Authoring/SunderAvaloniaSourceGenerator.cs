using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;

namespace Sunder.Package.Build.Authoring;

/// <summary>Runs the evaluated Avalonia name generator, without loading authored package assemblies.</summary>
internal sealed class SunderAvaloniaSourceGenerator : IDisposable
{
    private readonly GeneratorContext _context;
    private readonly ISourceGenerator _generator;
    private readonly ImmutableArray<AdditionalText> _texts;
    private readonly Options _options;
    internal SunderPackageFileEvidence AssemblyInput { get; }

    internal SunderAvaloniaSourceGenerator(string assemblyPath, IReadOnlyList<SunderPackageResourceInput> resources, ITaskItem[] properties)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(assemblyPath, 64 * 1024 * 1024, "Avalonia generator");
        AssemblyInput = new(assemblyPath, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        _context = new GeneratorContext(Path.GetDirectoryName(assemblyPath)!);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var assembly = _context.LoadFromStream(stream);
            if (assembly.GetName().Name != "Avalonia.Generators") throw new InvalidDataException("package-authoring.xaml-generator-invalid: Expected the evaluated Avalonia.Generators assembly.");
            var type = assembly.GetType("Avalonia.Generators.NameGenerator.AvaloniaNameIncrementalGenerator", throwOnError: true)!;
            _generator = ((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator();
        }
        catch { _context.Unload(); throw; }
        _texts = resources.Where(resource => resource.Kind == "AvaloniaXaml").Select(resource => (AdditionalText)new Text(resource)).ToImmutableArray();
        _options = new(properties);
    }

    internal IReadOnlyList<SunderRpcCSharpSourceFile> Generate(CSharpCompilation compilation)
    {
        var parse = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? new(LanguageVersion.CSharp14);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([_generator], _texts, parse, _options);
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        foreach (var diagnostic in result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("package-authoring.xaml-generator-invalid: " + diagnostic);
        if (result.Results.Any(result => result.Exception is not null))
            throw new InvalidDataException("package-authoring.xaml-generator-invalid: The evaluated Avalonia generator failed.", result.Results.First(result => result.Exception is not null).Exception);
        var sources = result.Results.SelectMany(result => result.GeneratedSources).OrderBy(source => source.HintName, StringComparer.Ordinal)
            .Select(source => new SunderRpcCSharpSourceFile("avalonia-generated/" + source.HintName, source.SourceText.ToString())).ToArray();
        if (sources.Length > 8192 || sources.Sum(source => (long)System.Text.Encoding.UTF8.GetByteCount(source.Text)) > 64L * 1024 * 1024)
            throw new InvalidDataException("package-authoring.xaml-generator-invalid: Generated Avalonia source exceeds the output budget.");
        return sources;
    }

    public void Dispose() => _context.Unload();

    private sealed class Text(SunderPackageResourceInput resource) : AdditionalText
    {
        public override string Path => resource.SourcePath;
        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(resource.Contents, writable: false);
            return SourceText.From(stream, System.Text.Encoding.UTF8);
        }
    }

    private sealed class Options(ITaskItem[] properties) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global = new Values(properties.ToDictionary(property => "build_property." + property.ItemSpec,
            property => property.GetMetadata("Value"), StringComparer.OrdinalIgnoreCase));
        private static readonly AnalyzerConfigOptions Xaml = new Values(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["build_metadata.AdditionalFiles.SourceItemGroup"] = "AvaloniaXaml" });
        private static readonly AnalyzerConfigOptions Empty = new Values(new Dictionary<string, string>());
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Xaml;
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
