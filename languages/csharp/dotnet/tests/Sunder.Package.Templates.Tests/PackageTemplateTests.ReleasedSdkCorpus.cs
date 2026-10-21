using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Xunit;
using static Sunder.Core.TestSupport.SdkCorpusInput;

namespace Sunder.Package.Templates.Tests;

public sealed partial class PackageTemplateTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_SDK_CORPUS", "SUNDER_COMPATIBILITY_CONTEXT")]
    [Trait("Category", "ExternalArtifact")]
    public async Task RetainedAuthoredSourceBuildsWithSelectedCandidateSdk()
    {
        var input = Load("old-source-new-sdk");
        Require(Text(input.Layout, "profile") == "dotnet-worker-v1", "Wrong C# source profile.");
        using var work = TemporaryDirectoryScope.Create("sunder-sdk-source-corpus");
        try
        {
            var source = new SdkCorpusSource(input, work.RootPath);
            var common = new[] { "-p:RestoreLockedMode=false", "-p:RestorePackagesPath=" + source.Packages,
                "-p:RestoreSources=" + source.Feed, "-p:RestoreAdditionalProjectSources=", "-p:RestoreFallbackFolders=",
                "-p:NuGetAudit=false", "-p:GeneratePackageOnBuild=false", "-p:Configuration=Release" };
            async Task RunAsync(params string[] arguments)
            {
                var start = new ProcessStartInfo("dotnet") { WorkingDirectory = source.ProjectRoot, UseShellExecute = false };
                foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
                    || x.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)
                    || x.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
                start.Environment["NUGET_PACKAGES"] = source.Packages;
                start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                foreach (var argument in arguments.Concat(common)) start.ArgumentList.Add(argument);
                await using var process = CapturedProcess.Start(start, maximumOutputCharacters: 8 * 1024 * 1024);
                await process.EnsureSuccessAsync(TimeSpan.FromMinutes(8));
            }
            await RunAsync("restore", source.Project, "--configfile", source.NuGetConfig, "--force-evaluate", "--disable-parallel");
            source.VerifyAuthoredInputs();
            await RunAsync("build", source.Project, "--no-restore", "--disable-build-servers");
            await RunAsync("msbuild", source.Project, "-t:PackSunderPackage", "-nr:false", "-nologo");
            source.VerifyAuthoredInputs();
            source.VerifyCandidateResolution();
            var output = Contained(source.ProjectRoot, Text(input.Layout, "packageOutput"));
            using var archive = ZipFile.OpenRead(output);
            using var manifest = JsonDocument.Parse(archive.GetEntry("manifest/sunder-package.json")!.Open());
            Assert.Equal(input.PackageId, Text(manifest.RootElement, "id"));
            Assert.Equal(input.PackageVersion, Text(manifest.RootElement, "version"));
            var targets = manifest.RootElement.GetProperty("targets").EnumerateArray().ToArray();
            Assert.NotEmpty(targets);
            Assert.All(targets, target => Assert.Equal(input.Version, Text(target, "sdkVersion")));
            Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/Sunder.Sdk.Worker.dll", StringComparison.Ordinal));
            Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith(".rpc.json", StringComparison.Ordinal));
            foreach (var id in new[] { "Sunder.Sdk", "Sunder.Sdk.Worker" })
            {
                using var candidate = ZipFile.OpenRead(Text(input.Candidates.Single(x => Text(x, "assetName") == $"{id}.{input.Version}.nupkg"), "path"));
                using var library = candidate.GetEntry($"lib/net10.0/{id}.dll")!.Open();
                var expected = System.Security.Cryptography.SHA256.HashData(library);
                var payloads = archive.Entries.Where(x => x.FullName.EndsWith($"/{id}.dll", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(payloads);
                foreach (var payload in payloads)
                {
                    using var stream = payload.Open();
                    Assert.Equal(expected, System.Security.Cryptography.SHA256.HashData(stream));
                }
            }
        }
        finally { input.VerifyUnchanged(); }
    }
}

internal sealed class ExternalArtifactFactAttribute : FactAttribute
{
    public ExternalArtifactFactAttribute(string requirementVariable, params string[] inputVariables)
    {
        var selected = Environment.GetEnvironmentVariable(requirementVariable) == "1"
            || requirementVariable == RequiredVariable && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ContextVariable));
        if (!selected)
        {
            Skip = $"External artifact lane is not selected. Set {requirementVariable}=1 to require it.";
            return;
        }
        foreach (var variable in inputVariables)
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            {
                if (requirementVariable == RequiredVariable) return; // Load() emits a failed required test.
                throw new InvalidOperationException($"Selected external artifact lane requires {variable}.");
            }
    }
}
