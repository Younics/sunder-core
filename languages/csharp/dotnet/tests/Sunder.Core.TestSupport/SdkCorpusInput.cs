using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.Core.TestSupport;

/// <summary>Test-only reader for sdk_compatibility_execution.py's selected-input context.</summary>
public sealed class SdkCorpusInput
{
    public const string ContextVariable = "SUNDER_COMPATIBILITY_CONTEXT";
    public const string RequiredVariable = "SUNDER_REQUIRE_SDK_CORPUS";
    public static readonly string[] NuGetIds = ["Sunder.Sdk", "Sunder.Sdk.Avalonia", "Sunder.Sdk.Stacks",
        "Sunder.Sdk.Worker", "Sunder.Package.Build", "Sunder.Package.Templates"];
    private readonly string _contextPath;
    private readonly string _contextHash;
    private readonly JsonElement[] _records;

    private SdkCorpusInput(string path, string kind)
    {
        _contextPath = Path.GetFullPath(path);
        Root = Path.GetDirectoryName(_contextPath)!;
        NoLinks(Root, _contextPath);
        var context = ReadJson(_contextPath);
        Members(context, "schemaVersion", "executionId", "context", "referenceId", "corpusId", "corpusKind", "bindings", "inputs", "candidatePackages", "candidateHostInputs");
        Require(context.GetProperty("schemaVersion").GetInt32() == 1 && Text(context, "corpusKind") == kind,
            "Wrong selected corpus direction/schema.");
        Require(Guid.TryParse(Text(context, "executionId"), out _), "Missing execution identity.");
        Context = context.GetProperty("context");
        Version = Text(Context, "sdkVersion");
        Require(Regex.IsMatch(Version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$"), "Invalid candidate version.");
        var rid = Text(Context, "actualRid");
        Require(rid == RuntimeInformation.RuntimeIdentifier, "Selected input RID differs from actual process RID.");
        Require(Text(Context, "lane") == (kind == "old-source-new-sdk" ? "portable" : rid), "Wrong corpus lane.");
        Inputs = context.GetProperty("inputs").EnumerateArray().ToArray();
        Candidates = context.GetProperty("candidatePackages").EnumerateArray().ToArray();
        CandidateHosts = context.GetProperty("candidateHostInputs").EnumerateArray().ToArray();
        Require(CandidateHosts.Length <= 12 && CandidateHosts.Select(x => Text(x, "id")).Distinct(StringComparer.OrdinalIgnoreCase).Count() == CandidateHosts.Length,
            "Invalid candidate Host inventory.");
        Require(Inputs.Length is > 0 and <= 64 && Candidates.Length == 9, "Wrong selected input inventory.");
        Require(Inputs.Select(x => Text(x, "id")).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Inputs.Length,
            "Duplicate input identity.");
        var names = NuGetIds.Select(x => $"{x}.{Version}.nupkg").Concat(new[] {
            $"sunder-sdk-{Version}.tgz", $"sunder-package-tool-{Version}.tgz", $"create-sunder-package-{Version}.tgz" });
        Require(names.Order().SequenceEqual(Candidates.Select(x => Text(x, "assetName")).Order()), "Candidate inventory/version differs.");
        Require(Candidates.Select(x => Text(x, "artifactId")).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 9,
            "Duplicate candidate identity.");
        foreach (var item in Inputs) ValidateRecord(item, "corpus", Text(item, "id"));
        foreach (var item in CandidateHosts)
        {
            Require(Text(item, "kind") == "host" && (Text(item, "id").StartsWith("candidate-host-", StringComparison.Ordinal)
                || Text(item, "id").StartsWith("candidate-app-", StringComparison.Ordinal))
                && Text(item.GetProperty("source"), "commit") == Text(Context, "artifactSourceCommit"), "Candidate Host source/slot differs.");
            ValidateRecord(item, "candidate-host", Text(item, "id"));
        }
        foreach (var item in Candidates)
        {
            ValidateRecord(item, "candidate", Text(item, "artifactId"));
            Require(Path.GetFileName(Text(item, "path")) == Text(item, "assetName"), "Candidate filename differs.");
        }
        _records = [.. Inputs, .. Candidates, .. CandidateHosts];
        Require(_records.Sum(x => x.GetProperty("size").GetInt64()) <= 2L * 1024 * 1024 * 1024, "Selected inputs exceed collector bound.");
        _contextHash = Hash(_contextPath);
        var layouts = Inputs.Where(x => Text(x, "kind") == "other" && Path.GetFileName(Text(x, "path")) == "corpus-layout.json").ToArray();
        Require(layouts.Length == 1, "Missing/ambiguous selected corpus-layout.json.");
        Layout = ReadJson(Text(layouts[0], "path"));
        var profile = Text(Layout, "profile");
        var app = profile == "avalonia-app-probe-v1";
        var rpc = profile is "dotnet-rpc-probe-v1" or "node-rpc-probe-v1";
        var common = new[] { "schemaVersion", "referenceId", "corpusId", "profile", "packageId", "packageVersion" };
        Members(Layout, [.. common, .. kind switch
        {
            "old-source-new-sdk" => new[] { "sourceArtifactId", "project", "packageOutput" },
            "old-package-new-host" when app => ["packageArtifactId", "apps"],
            "old-package-new-host" when rpc => ["packageArtifactId", "hosts", "probe"],
            "old-package-new-host" => ["packageArtifactId", "hosts"],
            "new-feature-old-host" => ["packageArtifactId", "hosts", "rejection"],
            _ => throw new InvalidDataException("Unsupported corpus direction."),
        }]);
        Require(Layout.GetProperty("schemaVersion").GetInt32() == 1 && (profile == "dotnet-worker-v1"
            || kind == "old-package-new-host" && (app || rpc)
            || kind == "old-source-new-sdk" && profile is "node-ts-source-v1" or "node-checked-js-source-v1"),
            "Unsupported owning adapter profile.");
        Require(Text(Layout, "referenceId") == Text(context, "referenceId") && Text(Layout, "corpusId") == Text(context, "corpusId"),
            "Layout belongs to another selected corpus.");
    }

    public string Root { get; }
    public string Version { get; }
    public JsonElement Context { get; }
    public JsonElement Layout { get; }
    public JsonElement[] Inputs { get; }
    public JsonElement[] Candidates { get; }
    public JsonElement[] CandidateHosts { get; }
    public string PackageId => Text(Layout, "packageId");
    public string PackageVersion => Text(Layout, "packageVersion");

    public static SdkCorpusInput Load(string kind) => Load(Environment.GetEnvironmentVariable(ContextVariable)
        ?? throw new InvalidDataException($"Required corpus input missing: {ContextVariable}."), kind);
    public static SdkCorpusInput Load(string path, string kind) => new(path, kind);

    public string Artifact(string id, string kind)
    {
        var matches = Inputs.Where(x => Text(x, "id") == id && Text(x, "kind") == kind).ToArray();
        Require(matches.Length == 1, $"Missing exact selected {kind} artifact '{id}'.");
        Verify(matches[0]);
        return Text(matches[0], "path");
    }

    public string HostArtifact(bool candidate)
    {
        var selected = HostSelection();
        var id = Text(selected, candidate ? "candidateArtifactId" : "referenceArtifactId");
        if (!candidate) return Artifact(id, "host");
        return CandidateBinary(id, "candidate-host-");
    }

    public string AppArtifact()
    {
        var apps = Layout.GetProperty("apps").EnumerateArray().ToArray();
        Require(apps.Length is > 0 and <= 6 && apps.Select(x => Text(x, "rid")).Distinct().Count() == apps.Length, "Invalid App RID inventory.");
        foreach (var app in apps) Members(app, "rid", "candidateArtifactId");
        return CandidateBinary(Text(apps.Single(x => Text(x, "rid") == Text(Context, "actualRid")), "candidateArtifactId"), "candidate-app-");
    }

    private string CandidateBinary(string id, string prefix)
    {
        Require(id == prefix + Text(Context, "actualRid"), "Wrong native candidate binary slot.");
        var matches = CandidateHosts.Where(x => Text(x, "id") == id).ToArray();
        Require(matches.Length == 1, "Missing selected candidate Host binary.");
        Verify(matches[0]);
        return Text(matches[0], "path");
    }

    public JsonElement HostSelection()
    {
        var hosts = Layout.GetProperty("hosts").EnumerateArray().ToArray();
        Require(hosts.Length is > 0 and <= 6 && hosts.Select(x => Text(x, "rid")).Distinct().Count() == hosts.Length,
            "Duplicate/oversized Host RID inventory.");
        foreach (var host in hosts) Members(host, "rid", "candidateArtifactId", "referenceArtifactId", "runtimeArtifactId");
        return hosts.Single(x => Text(x, "rid") == Text(Context, "actualRid"));
    }

    public void VerifyUnchanged()
    {
        NoLinks(Root, _contextPath);
        Require(Hash(_contextPath) == _contextHash, "Collector context changed.");
        foreach (var item in _records)
        {
            NoLinks(Root, Text(item, "path"));
            Verify(item);
        }
    }

    private void ValidateRecord(JsonElement item, string group, string id)
    {
        Require(Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]*$"), "Unsafe artifact identity.");
        var path = Text(item, "path");
        var name = Path.GetFileName(path);
        Require(Path.IsPathFullyQualified(path) && path == Contained(Root, $"inputs/{group}/{id}/{name}"),
            "Input does not bind to the collector's isolated selected copy.");
        Require(File.Exists(path), "Missing selected artifact: " + path);
        NoLinks(Root, path);
        Verify(item);
    }

    private static void Verify(JsonElement item)
    {
        var path = Text(item, "path");
        var size = item.GetProperty("size").GetInt64();
        Require(size is > 0 and <= 2L * 1024 * 1024 * 1024 && File.Exists(path) && new FileInfo(path).Length == size
            && Hash(path) == Text(item, "sha256"), "Selected artifact bytes changed/missing: " + path);
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // Match the existing template/process fixtures: macOS /var is an alias for
    // /private/var, while MSBuild and native process identity use physical paths.
    public static string WorkPath(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsMacOS() && full.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + full : full;
    }

    public static string Contained(string root, string relative)
    {
        Require(Regex.IsMatch(relative, @"^[A-Za-z0-9_@.+-]+(?:/[A-Za-z0-9_@.+-]+)*$")
            && relative.Split('/').All(x => !x.EndsWith('.')
                && !Regex.IsMatch(x, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)),
            "Unsafe artifact member path: " + relative);
        return Path.Combine(Path.GetFullPath(root), relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public static void NoLinks(string root, string path)
    {
        for (var current = path; current.Length >= root.Length; current = Path.GetDirectoryName(current)!)
        {
            Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Linked corpus input/member.");
            if (current == root) break;
        }
    }

    /// <summary>ZIP only, no links/special files, portable unique paths, bounded expansion.</summary>
    public static void Extract(string archivePath, string output)
    {
        Require(!Directory.Exists(output), "Extraction requires a new disposable directory.");
        using var archive = ZipFile.OpenRead(archivePath);
        Require(archive.Entries.Count is > 0 and <= 8192, "Oversized/empty corpus archive.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            _ = Contained(output, name);
            Require(paths.Add(name), "Duplicate/case-colliding archive member.");
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            Require(type is 0 or 0x8000 or 0x4000, "Linked/special archive member.");
            Require((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) == 0, "Linked archive member.");
            total = checked(total + entry.Length);
            Require(total <= 2L * 1024 * 1024 * 1024, "Corpus archive expansion exceeds bound.");
        }
        Directory.CreateDirectory(output);
        foreach (var entry in archive.Entries)
        {
            var path = Contained(output, entry.FullName.TrimEnd('/'));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using (var stream = new FileStream(path, FileMode.CreateNew))
            {
                var buffer = new byte[65536];
                long copied = 0;
                int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    copied += count;
                    Require(copied <= entry.Length, "Archive member exceeds declared size.");
                    stream.Write(buffer, 0, count);
                }
                Require(copied == entry.Length, "Truncated archive member.");
            }
            if (!OperatingSystem.IsWindows() && ((entry.ExternalAttributes >> 16) & 0x49) != 0)
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static JsonElement ReadJson(string path)
    {
        Require(new FileInfo(path).Length <= 1024 * 1024, "Oversized corpus JSON.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 32 });
        CheckMembers(doc.RootElement);
        return doc.RootElement.Clone();
    }

    private static void CheckMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "Duplicate JSON member.");
                CheckMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckMembers(item);
    }

    public static void Members(JsonElement value, params string[] names) => Require(value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(names.Order()), "Unexpected/missing corpus members.");
    public static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()
        ?? throw new InvalidDataException("Missing corpus string: " + name);
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
