using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using static Sunder.Core.TestSupport.SdkCorpusInput;

namespace Sunder.Core.TestSupport;

/// <summary>Bounded single-project C# source profile. Only NuGet selection may change.</summary>
public sealed class SdkCorpusSource
{
    private readonly Dictionary<string, string> _authored;
    private readonly JsonElement _originalLock;
    private readonly Dictionary<string, string> _selectedProjectFiles = new(StringComparer.Ordinal);

    public SdkCorpusSource(SdkCorpusInput input, string work)
    {
        work = WorkPath(work);
        Input = input;
        var archive = input.Artifact(Text(input.Layout, "sourceArtifactId"), "source");
        var extracted = Path.Combine(work, "retained");
        Extract(archive, extracted);
        Require(Directory.GetFileSystemEntries(extracted).Select(Path.GetFileName).Order().SequenceEqual(new[] { "feed", "source" }),
            "Source ZIP must have exactly source/ and feed/ roots.");
        SourceRoot = Path.Combine(work, "source");
        Feed = Path.Combine(work, "feed");
        Packages = Path.Combine(work, "packages");
        Directory.CreateDirectory(SourceRoot);
        Directory.CreateDirectory(Feed);
        _authored = [];
        foreach (var file in Directory.GetFiles(Path.Combine(extracted, "source"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(extracted, "source"), file).Replace('\\', '/');
            Require(!relative.Split('/').Any(x => x is "bin" or "obj" or ".git" or "node_modules"), "Retained source includes generated outputs.");
            var destination = Contained(SourceRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            _authored.Add(relative, Hash(file));
        }
        Project = Contained(SourceRoot, Text(input.Layout, "project"));
        Require(Directory.GetFiles(SourceRoot, "*.csproj", SearchOption.AllDirectories).SequenceEqual(new[] { Project }),
            "This profile requires exactly one canonical authored C# project.");
        ProjectRoot = Path.GetDirectoryName(Project)!;
        _originalLock = ReadJson(Path.Combine(ProjectRoot, "packages.lock.json"));
        Require(File.Exists(Path.Combine(ProjectRoot, "sunder-contracts.lock.json")), "Missing retained contract lock.");
        foreach (var file in Directory.GetFiles(SourceRoot, "*", SearchOption.AllDirectories)
            .Where(x => Path.GetExtension(x) is ".csproj" or ".props" or ".targets"))
        {
            var xml = XDocument.Load(file, LoadOptions.PreserveWhitespace);
            Require(!xml.Descendants("ProjectReference").Any() && !xml.Descendants("PackageVersion").Any(),
                "Source profile requires direct NuGet selections, without project/central-package references.");
            var changed = false;
            foreach (var reference in xml.Descendants("PackageReference"))
            {
                var id = reference.Attribute("Include")?.Value;
                if (id is null || !id.StartsWith("Sunder.", StringComparison.OrdinalIgnoreCase)) continue;
                Require(NuGetIds.Contains(id) && reference.Attribute("Version") is not null
                    && !reference.Elements("Version").Any() && reference.Attribute("VersionOverride") is null,
                    "Unsupported Sunder dependency-selection syntax.");
                reference.SetAttributeValue("Version", $"[{input.Version}]");
                changed = true;
            }
            if (!changed) continue;
            xml.Save(file, SaveOptions.DisableFormatting);
            _selectedProjectFiles.Add(Path.GetRelativePath(SourceRoot, file).Replace('\\', '/'), Hash(file));
        }
        Require(_selectedProjectFiles.Count > 0, "No authored SDK dependency selection found.");
        var feedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(Path.Combine(extracted, "feed"), "*", SearchOption.AllDirectories))
        {
            var (id, version) = NuGetIdentity(file);
            Require(feedIdentities.Add(id + "/" + version), "Duplicate retained NuGet identity.");
            if (id.StartsWith("Sunder.", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(Feed, $"{id}.{version}.nupkg"));
        }
        foreach (var framework in _originalLock.GetProperty("dependencies").EnumerateObject())
            foreach (var dependency in framework.Value.EnumerateObject())
                Require(feedIdentities.Contains(dependency.Name + "/" + Text(dependency.Value, "resolved")),
                    "Missing retained original NuGet dependency: " + dependency.Name);
        foreach (var item in input.Candidates.Where(x => Text(x, "assetName").EndsWith(".nupkg", StringComparison.Ordinal)))
        {
            var path = Text(item, "path");
            var (id, version) = NuGetIdentity(path);
            Require(NuGetIds.Contains(id) && version == input.Version && Path.GetFileName(path) == $"{id}.{version}.nupkg",
                "Candidate NuGet filename/content identity differs.");
            File.Copy(path, Path.Combine(Feed, Path.GetFileName(path)));
        }
        // Only this disposable control file is new. Explicit sources also apply to generated role restores.
        NuGetConfig = Path.Combine(work, "NuGet.Config");
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "selected"), new XAttribute("value", Feed))),
            new XElement("fallbackPackageFolders", new XElement("clear")))).Save(NuGetConfig);
    }

    public SdkCorpusInput Input { get; }
    public string SourceRoot { get; }
    public string ProjectRoot { get; }
    public string Project { get; }
    public string Feed { get; }
    public string Packages { get; }
    public string NuGetConfig { get; }

    public void VerifyAuthoredInputs()
    {
        foreach (var (relative, hash) in _authored)
        {
            if (relative == Path.GetRelativePath(SourceRoot, Path.Combine(ProjectRoot, "packages.lock.json")).Replace('\\', '/')) continue;
            Require(Hash(Contained(SourceRoot, relative)) == _selectedProjectFiles.GetValueOrDefault(relative, hash),
                "Authored source/contract lock changed during rebuild: " + relative);
        }
        var current = ReadJson(Path.Combine(ProjectRoot, "packages.lock.json"));
        Require(NonSunderLock(_originalLock).SequenceEqual(NonSunderLock(current)), "Non-Sunder dependency lock semantics changed.");
        var actual = Directory.GetFiles(SourceRoot, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(SourceRoot, x).Replace('\\', '/'))
            .Where(x => !x.Split('/').Any(part => part is "bin" or "obj")).Order().ToArray();
        Require(actual.SequenceEqual(_authored.Keys.Order()), "Build wrote new authored inputs outside bin/obj.");
        Input.VerifyUnchanged();
    }

    private static IEnumerable<string> NonSunderLock(JsonElement value) => value.GetProperty("dependencies").EnumerateObject()
        .SelectMany(tfm => tfm.Value.EnumerateObject().Where(x => !x.Name.StartsWith("Sunder.", StringComparison.OrdinalIgnoreCase))
            .Select(x => tfm.Name + "/" + x.Name + ":" + JsonSerializer.Serialize(x.Value))).Order();

    public void VerifyCandidateResolution()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var assetsFiles = Directory.GetFiles(Path.Combine(ProjectRoot, "obj"), "project.assets.json", SearchOption.AllDirectories);
        Require(assetsFiles.Length > 0, "No actual restore assets were produced.");
        foreach (var file in assetsFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var assets = document.RootElement;
            Require(assets.GetProperty("packageFolders").EnumerateObject().All(x => Path.GetFullPath(x.Name).TrimEnd(Path.DirectorySeparatorChar)
                == Path.GetFullPath(Packages).TrimEnd(Path.DirectorySeparatorChar)), "Restore used an ambient NuGet cache.");
            foreach (var library in assets.GetProperty("libraries").EnumerateObject()
                .Where(x => x.Name.StartsWith("Sunder.", StringComparison.OrdinalIgnoreCase)))
            {
                var identity = library.Name.Split('/');
                Require(identity.Length == 2 && NuGetIds.Contains(identity[0]) && identity[1] == Input.Version
                    && Text(library.Value, "type") == "package", "Build resolved another Sunder SDK/tool version or source project.");
                var selected = Input.Candidates.Single(x => Text(x, "assetName") == $"{identity[0]}.{identity[1]}.nupkg");
                var cacheRoot = Contained(Packages, Text(library.Value, "path"));
                var cachePackage = Path.Combine(cacheRoot, $"{identity[0]}.{identity[1]}.nupkg".ToLowerInvariant());
                Require(Hash(cachePackage) == Text(selected, "sha256"), "Restored package differs from the selected candidate bytes.");
                using var archive = ZipFile.OpenRead(Text(selected, "path"));
                foreach (var entry in archive.Entries.Where(x => Path.GetExtension(x.FullName) is ".dll" or ".props" or ".targets"))
                {
                    using var stream = entry.Open();
                    var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
                    Require(Hash(Contained(cacheRoot, entry.FullName)) == digest, "Resolved SDK/tool payload differs from the candidate: " + entry.FullName);
                }
                seen.Add(identity[0]);
            }
        }
        Require(new[] { "Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Package.Build" }.All(seen.Contains),
            "Candidate SDK, Worker and Build were not all consumed.");
        var build = Input.Candidates.Single(x => Text(x, "assetName") == $"Sunder.Package.Build.{Input.Version}.nupkg");
        using var package = ZipFile.OpenRead(Text(build, "path"));
        using var task = package.GetEntry("tasks/net10.0/Sunder.Package.Build.dll")!.Open();
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(task));
        var markers = Directory.GetFiles(Path.Combine(ProjectRoot, "obj"), ".sunder-task-set", SearchOption.AllDirectories);
        Require(markers.Length > 0 && markers.All(marker => Hash(Path.Combine(Path.GetDirectoryName(marker)!, "Sunder.Package.Build.dll")) == expected),
            "Actual staged Build task bytes differ from the candidate.");
        Require(Directory.GetFiles(Path.Combine(ProjectRoot, "obj"), "*.g.cs", SearchOption.AllDirectories)
            .Any(x => x.Contains("sunder-rpc", StringComparison.Ordinal)), "Candidate tools did not generate RPC bindings.");
    }

    public static (string Id, string Version) NuGetIdentity(string path)
    {
        Require(path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase), "Non-NuGet source feed entry.");
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.Where(x => x.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(entries.Length == 1 && entries[0].Length <= 1024 * 1024, "Missing/ambiguous NuGet metadata.");
        using var stream = entries[0].Open();
        var metadata = XDocument.Load(stream).Root!.Elements().Single(x => x.Name.LocalName == "metadata");
        var id = metadata.Elements().Single(x => x.Name.LocalName == "id").Value;
        var version = metadata.Elements().Single(x => x.Name.LocalName == "version").Value;
        _ = Contained("/", id + "." + version + ".nupkg");
        return (id, version);
    }
}
