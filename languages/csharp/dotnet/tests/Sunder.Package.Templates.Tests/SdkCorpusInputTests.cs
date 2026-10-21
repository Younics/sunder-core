using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Xunit;
using static Sunder.Core.TestSupport.SdkCorpusInput;

namespace Sunder.Package.Templates.Tests;

// Synthetic parser/source-selection fixtures only. These are not retained release artifacts.
public sealed class SdkCorpusInputTests
{
    [Fact]
    public async Task CorpusProcessOutputIsBounded()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add("--list-runtimes");
        await using var process = CapturedProcess.Start(start, maximumOutputCharacters: 8);
        var error = await Assert.ThrowsAsync<ProcessFixtureException>(() => process.EnsureSuccessAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("capture bound", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedCopiesAreRequiredAndRecheckedIncludingContext()
    {
        using var fixture = new Fixture();
        var input = fixture.Load();
        input.VerifyUnchanged();
        foreach (var record in input.Inputs.Concat(input.Candidates))
        {
            var path = Text(record, "path");
            var bytes = File.ReadAllBytes(path);
            File.WriteAllText(path, "substitution");
            Assert.Throws<InvalidDataException>(input.VerifyUnchanged);
            File.WriteAllBytes(path, bytes);
        }
        File.AppendAllText(fixture.ContextPath, " ");
        Assert.Throws<InvalidDataException>(input.VerifyUnchanged);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("missing")]
    [InlineData("direction")]
    [InlineData("candidate-version")]
    [InlineData("rid")]
    [InlineData("duplicate")]
    [InlineData("ambiguous-layout")]
    [InlineData("layout-identity")]
    public void MisboundInputsCannotSelectAnUnrelatedPassingCorpus(string fault)
    {
        using var fixture = new Fixture();
        switch (fault)
        {
            case "redirect":
                var copy = fixture.Work.GetPath("outside.zip");
                File.Copy(fixture.SourceArchive, copy);
                fixture.Context["inputs"]![1]!["path"] = copy;
                break;
            case "missing": File.Delete(fixture.SourceArchive); break;
            case "direction": fixture.Context["corpusKind"] = "old-package-new-host"; break;
            case "candidate-version": fixture.Context["candidatePackages"]![0]!["assetName"] = "Sunder.Sdk.0.0.0.nupkg"; break;
            case "rid": fixture.Context["context"]!["actualRid"] = "wrong-rid"; break;
            case "duplicate": fixture.Context["inputs"]!.AsArray().Add(fixture.Context["inputs"]![1]!.DeepClone()); break;
            case "ambiguous-layout":
                var extra = fixture.Context["inputs"]![0]!.DeepClone();
                extra["id"] = "other-layout";
                extra["path"] = fixture.Work.WriteAllText("inputs/corpus/other-layout/corpus-layout.json", File.ReadAllText(extra["path"]!.GetValue<string>()));
                fixture.Context["inputs"]!.AsArray().Add(extra);
                break;
            case "layout-identity":
                var layoutRecord = fixture.Context["inputs"]![0]!;
                var layoutPath = layoutRecord["path"]!.GetValue<string>();
                var layout = JsonNode.Parse(File.ReadAllText(layoutPath))!;
                layout["corpusId"] = "other-corpus";
                File.WriteAllText(layoutPath, layout.ToJsonString());
                layoutRecord["size"] = new FileInfo(layoutPath).Length;
                layoutRecord["sha256"] = Hash(layoutPath);
                break;
        }
        fixture.Save();
        Assert.Throws<InvalidDataException>(() => fixture.Load());
    }

    [Fact]
    public void LinkedSelectedCopyAndCaseCollidingJsonAreRejected()
    {
        using var fixture = new Fixture();
        var original = fixture.Work.GetPath("linked-source.zip");
        File.Move(fixture.SourceArchive, original);
        File.CreateSymbolicLink(fixture.SourceArchive, original);
        Assert.Throws<InvalidDataException>(() => fixture.Load());
        File.Delete(fixture.SourceArchive);
        File.Move(original, fixture.SourceArchive);
        File.WriteAllText(fixture.ContextPath, fixture.Context.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"SchemaVersion\":1", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => fixture.Load());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("CON")]
    [InlineData("alias.")]
    [InlineData("A/file", "a/file")]
    [InlineData("link")]
    public void UnsafeArchiveMembersAreRejectedBeforeExtraction(string first, string? second = null)
    {
        using var work = TemporaryDirectoryScope.Create("sunder-corpus-zip-test");
        var path = work.GetPath("bad.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(first);
            if (first == "link") entry.ExternalAttributes = 0xa1ff << 16;
            if (second is not null) zip.CreateEntry(second);
        }
        Assert.Throws<InvalidDataException>(() => Extract(path, work.GetPath("out")));
        Assert.False(Directory.Exists(work.GetPath("out")));
    }

    [Fact]
    public void SourceSelectionChangesOnlyDisposableDependencyVersionsAndChecksLocks()
    {
        using var fixture = new Fixture();
        var before = Hash(fixture.SourceArchive);
        var source = new SdkCorpusSource(fixture.Load(), fixture.Work.CreateDirectory("build"));
        var project = XDocument.Load(source.Project);
        Assert.All(project.Descendants("PackageReference"), reference => Assert.Equal("[1.1.0]", reference.Attribute("Version")!.Value));
        Assert.Equal("public class Original { }", File.ReadAllText(Path.Combine(source.ProjectRoot, "Service.cs")));
        source.VerifyAuthoredInputs();
        File.AppendAllText(Path.Combine(source.ProjectRoot, "sunder-contracts.lock.json"), " ");
        Assert.Throws<InvalidDataException>(source.VerifyAuthoredInputs);
        File.WriteAllText(Path.Combine(source.ProjectRoot, "sunder-contracts.lock.json"), "{}");
        File.WriteAllText(Path.Combine(source.ProjectRoot, "packages.lock.json"), """{"dependencies":{"net10.0":{"Other":{"resolved":"2.0.0"}}}}""");
        Assert.Throws<InvalidDataException>(source.VerifyAuthoredInputs);
        Assert.Equal(before, Hash(fixture.SourceArchive));
    }

    [Fact]
    public void CandidateNuspecMustMatchSelectedIdentityAndVersion()
    {
        using var fixture = new Fixture();
        var record = fixture.Context["candidatePackages"]![0]!;
        var path = record["path"]!.GetValue<string>();
        File.Delete(path);
        Fixture.NuGet(path, "Sunder.Sdk", "0.0.0");
        record["sha256"] = Hash(path);
        record["size"] = new FileInfo(path).Length;
        fixture.Save();
        Assert.Throws<InvalidDataException>(() => new SdkCorpusSource(fixture.Load(), fixture.Work.CreateDirectory("build")));
    }

    [Fact]
    public void MissingOriginalNuGetClosureFailsEvenWhenCandidateFeedIsAvailable()
    {
        using var fixture = new Fixture();
        using (var zip = ZipFile.Open(fixture.SourceArchive, ZipArchiveMode.Update)) zip.GetEntry("feed/Other.1.0.0.nupkg")!.Delete();
        var record = fixture.Context["inputs"]![1]!;
        record["sha256"] = Hash(fixture.SourceArchive);
        record["size"] = new FileInfo(fixture.SourceArchive).Length;
        fixture.Save();
        var error = Assert.Throws<InvalidDataException>(() => new SdkCorpusSource(fixture.Load(), fixture.Work.CreateDirectory("build")));
        Assert.Contains("Missing retained original NuGet", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("candidate-host-")]
    [InlineData("candidate-app-")]
    public void CandidateHostMustUseCurrentSourceAndSeparateSelectedCopy(string prefix)
    {
        using var fixture = new Fixture();
        var id = prefix + RuntimeInformation.RuntimeIdentifier;
        var path = fixture.Work.WriteAllText($"inputs/candidate-host/{id}/host.zip", "synthetic Host input, never executed");
        fixture.Context["context"]!["artifactSourceCommit"] = "unit-current-source";
        var record = new JsonObject { ["id"] = id, ["kind"] = "host", ["path"] = path, ["size"] = new FileInfo(path).Length,
            ["sha256"] = Hash(path), ["source"] = new JsonObject { ["commit"] = "unit-old-source" } };
        fixture.Context["candidateHostInputs"]!.AsArray().Add(record);
        fixture.Save();
        Assert.Throws<InvalidDataException>(() => fixture.Load());
        record["source"]!["commit"] = "unit-current-source";
        fixture.Save();
        fixture.Load().VerifyUnchanged();
        // Equal bytes at another location cannot substitute for the selected copy.
        record["path"] = fixture.Work.WriteAllText("host.zip", "synthetic Host input, never executed");
        fixture.Save();
        Assert.Throws<InvalidDataException>(() => fixture.Load());
    }

    [Fact]
    public void AppProfileCannotSubstituteARuntimeHostSlotOrMissingCandidate()
    {
        using var fixture = new Fixture();
        var rid = RuntimeInformation.RuntimeIdentifier;
        fixture.Context["corpusKind"] = "old-package-new-host";
        fixture.Context["context"]!["lane"] = rid;
        var layoutRecord = fixture.Context["inputs"]![0]!;
        var path = layoutRecord["path"]!.GetValue<string>();
        var layout = JsonNode.Parse(File.ReadAllText(path))!;
        layout["profile"] = "avalonia-app-probe-v1";
        foreach (var field in new[] { "sourceArtifactId", "project", "packageOutput" }) layout.AsObject().Remove(field);
        layout["packageArtifactId"] = "app-package";
        layout["apps"] = new JsonArray(new JsonObject { ["rid"] = rid, ["candidateArtifactId"] = "candidate-host-" + rid });
        void Save()
        {
            File.WriteAllText(path, layout.ToJsonString());
            layoutRecord["size"] = new FileInfo(path).Length;
            layoutRecord["sha256"] = Hash(path);
            fixture.Save();
        }
        Save();
        Assert.Contains("slot", Assert.Throws<InvalidDataException>(() => SdkCorpusInput.Load(fixture.ContextPath, "old-package-new-host").AppArtifact()).Message, StringComparison.Ordinal);
        layout["apps"]![0]!["candidateArtifactId"] = "candidate-app-" + rid;
        Save();
        Assert.Contains("Missing selected candidate", Assert.Throws<InvalidDataException>(() => SdkCorpusInput.Load(fixture.ContextPath, "old-package-new-host").AppArtifact()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionCannotUseAnotherSdkOrAmbientCacheEvenWithAValidSelectedFeed()
    {
        using var fixture = new Fixture();
        var source = new SdkCorpusSource(fixture.Load(), fixture.Work.CreateDirectory("build"));
        var obj = Path.Combine(source.ProjectRoot, "obj");
        Directory.CreateDirectory(obj);
        var assets = new JsonObject { ["packageFolders"] = new JsonObject { [source.Packages] = new JsonObject() },
            ["libraries"] = new JsonObject { ["Sunder.Sdk/0.0.0"] = new JsonObject { ["type"] = "package" } } };
        var file = Path.Combine(obj, "project.assets.json");
        File.WriteAllText(file, assets.ToJsonString());
        Assert.Contains("another Sunder", Assert.Throws<InvalidDataException>(source.VerifyCandidateResolution).Message, StringComparison.Ordinal);
        assets["packageFolders"] = new JsonObject { [fixture.Work.RootPath] = new JsonObject() };
        File.WriteAllText(file, assets.ToJsonString());
        Assert.Contains("ambient NuGet", Assert.Throws<InvalidDataException>(source.VerifyCandidateResolution).Message, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        public TemporaryDirectoryScope Work { get; } = TemporaryDirectoryScope.Create("sunder-corpus-input-test");
        public string ContextPath => Work.GetPath("context.json");
        public string SourceArchive { get; }
        public JsonObject Context { get; }

        public Fixture()
        {
            var inputs = new JsonArray();
            var candidates = new JsonArray();
            SourceArchive = Work.GetPath("inputs", "corpus", "source", "source.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(SourceArchive)!);
            var originalDependency = Work.GetPath("Other.1.0.0.nupkg");
            NuGet(originalDependency, "Other", "1.0.0");
            new PackageArchiveFixtureBuilder()
                .AddText("source/Consumer.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Sunder.Sdk.Worker" Version="[1.0.0]"/><PackageReference Include="Sunder.Package.Build" Version="[1.0.0]"/></ItemGroup></Project>""")
                .AddText("source/Service.cs", "public class Original { }")
                .AddText("source/packages.lock.json", """{"dependencies":{"net10.0":{"Other":{"resolved":"1.0.0"}}}}""")
                .AddText("source/sunder-contracts.lock.json", "{}")
                .AddBytes("feed/Other.1.0.0.nupkg", File.ReadAllBytes(originalDependency))
                .WriteArchive(SourceArchive);
            // Retain the directory too, so the missing-dependency case fails on the closure.
            using (var zip = ZipFile.Open(SourceArchive, ZipArchiveMode.Update)) zip.CreateEntry("feed/");
            var layoutPath = Work.WriteAllText("inputs/corpus/corpus-layout/corpus-layout.json", """
                {"schemaVersion":1,"referenceId":"unit-fixture","corpusId":"source-fixture","profile":"dotnet-worker-v1",
                 "packageId":"unit.fixture","packageVersion":"1.0.0","sourceArtifactId":"source","project":"Consumer.csproj",
                 "packageOutput":"bin/Release/net10.0/Consumer.1.0.0.sunderpkg"}
                """);
            inputs.Add(Record(layoutPath, "corpus-layout", "other"));
            inputs.Add(Record(SourceArchive, "source", "source"));
            foreach (var id in NuGetIds)
            {
                var name = id + ".1.1.0.nupkg";
                var path = Work.GetPath("inputs", "candidate", id, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                NuGet(path, id, "1.1.0");
                candidates.Add(Candidate(path, id, name));
            }
            foreach (var id in new[] { "sunder-sdk", "sunder-package-tool", "create-sunder-package" })
            {
                var name = id + "-1.1.0.tgz";
                var path = Work.WriteAllText($"inputs/candidate/{id}/{name}", "synthetic unused npm bytes");
                candidates.Add(Candidate(path, id, name));
            }
            Context = new JsonObject { ["schemaVersion"] = 1, ["executionId"] = Guid.NewGuid().ToString(),
                ["referenceId"] = "unit-fixture", ["corpusId"] = "source-fixture", ["corpusKind"] = "old-source-new-sdk",
                ["context"] = new JsonObject { ["sdkVersion"] = "1.1.0", ["actualRid"] = RuntimeInformation.RuntimeIdentifier, ["lane"] = "portable" },
                ["bindings"] = new JsonObject(), ["inputs"] = inputs, ["candidatePackages"] = candidates, ["candidateHostInputs"] = new JsonArray() };
            Save();
        }

        private static JsonObject Record(string path, string id, string kind) => new() {
            ["id"] = id, ["kind"] = kind, ["path"] = path, ["size"] = new FileInfo(path).Length, ["sha256"] = Hash(path) };
        private static JsonObject Candidate(string path, string id, string name) => new() {
            ["artifactId"] = id, ["assetName"] = name, ["path"] = path, ["size"] = new FileInfo(path).Length, ["sha256"] = Hash(path) };
        public void Save() => File.WriteAllText(ContextPath, Context.ToJsonString());
        public SdkCorpusInput Load() => SdkCorpusInput.Load(ContextPath, "old-source-new-sdk");
        public void Dispose() => Work.Dispose();
        public static void NuGet(string path, string id, string version) => new PackageArchiveFixtureBuilder()
            .AddText(id + ".nuspec", $"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>").WriteArchive(path);
    }
}
