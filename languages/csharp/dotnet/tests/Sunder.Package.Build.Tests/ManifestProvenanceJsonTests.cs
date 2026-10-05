using System.Collections;
using Microsoft.Build.Framework;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class ManifestProvenanceJsonTests
{
    [Theory]
    [InlineData("""{"packageArtifactImports":null}""", "Explicit JSON null")]
    [InlineData("""{"packageApiImports":null}""", "Explicit JSON null")]
    [InlineData("""{"providerImports":null}""", "Explicit JSON null")]
    [InlineData("""{"platformImports":null}""", "Explicit JSON null")]
    [InlineData("""{"contractBundles":[{"source":"owned","selectedAuthorityRelease":null}]}""", "Explicit JSON null")]
    [InlineData("""{"packageArtifactImports":[{"authorityConstraints":[{"authorityKind":"package","catalogTrustFingerprint":null}]}]}""", "Explicit JSON null")]
    [InlineData("""{"dependsOn":[{"presence":null}]}""", "Explicit JSON null")]
    [InlineData("""{"platformImports":[],"platformImports":[]}""", "Duplicate JSON property")]
    [InlineData("""{"contractBundles":[{"source":"imported","source":"owned"}]}""", "Duplicate JSON property")]
    public void DevOutputReader_RejectsJsonBeforeCopyingPayload(string json, string expectedError)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-build-manifest-provenance-json");
        var manifestPath = temporary.WriteAllText("sunder-package.json", json);
        var outputPath = temporary.GetPath("sunder-dev");
        var engine = new RecordingBuildEngine();
        var task = new EmitSunderDevOutputTask
        {
            BuildEngine = engine,
            ManifestPath = manifestPath,
            DevPackagePath = outputPath,
            DiscoveryRoot = temporary.RootPath,
            TargetDirectory = temporary.RootPath,
        };

        Assert.False(task.Execute());

        Assert.StartsWith(expectedError, Assert.Single(engine.Errors), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputPath));
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
            => throw new NotSupportedException();

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? string.Empty);
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogWarningEvent(BuildWarningEventArgs e) { }
    }
}
