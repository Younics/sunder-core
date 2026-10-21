using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Xunit;
using static Sunder.Core.TestSupport.SdkCorpusInput;

namespace Sunder.Package.Templates.Tests;

public sealed partial class PackageTemplateTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_SDK_CORPUS", "SUNDER_COMPATIBILITY_CONTEXT")]
    [Trait("Category", "ExternalArtifact")]
    public Task RetainedTypeScriptBuildsWithSelectedCandidateTools() => RunNodeCorpusAsync("node-ts-source-v1", ".ts");

    [ExternalArtifactFact("SUNDER_REQUIRE_SDK_CORPUS", "SUNDER_COMPATIBILITY_CONTEXT")]
    [Trait("Category", "ExternalArtifact")]
    public Task RetainedCheckedJavaScriptBuildsWithSelectedCandidateTools() => RunNodeCorpusAsync("node-checked-js-source-v1", ".js");

    private static async Task RunNodeCorpusAsync(string profile, string extension)
    {
        var input = Load("old-source-new-sdk");
        using var work = TemporaryDirectoryScope.Create("sunder-node-source-corpus");
        try
        {
            Require(Text(input.Layout, "profile") == profile && Text(input.Layout, "project") == "package.json", "Wrong selected Node source profile.");
            var root = WorkPath(work.RootPath);
            var unpacked = Path.Combine(root, "retained");
            Extract(input.Artifact(Text(input.Layout, "sourceArtifactId"), "source"), unpacked);
            Require(Directory.GetFileSystemEntries(unpacked).Select(Path.GetFileName).Order().SequenceEqual(new[] { "feed", "source" }), "Node source requires source/ and feed/.");
            var source = Path.Combine(unpacked, "source"); // This extraction is already a disposable copy, not a retained input.
            var originalFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(x => x, Hash);
            Require(originalFiles.Keys.All(x => !Path.GetRelativePath(source, x).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "node_modules" or "dist" or ".sunder" or ".git")), "Retained Node source contains generated outputs.");
            var config = ReadJson(Path.Combine(source, "sunder.package.json"));
            var module = Text(config.GetProperty("services"), "module");
            Require(module.EndsWith(extension, StringComparison.Ordinal), "Selected source language differs.");
            if (extension == ".js")
            {
                Require(!originalFiles.Keys.Any(x => x.EndsWith(".ts", StringComparison.Ordinal)), "Checked-JS corpus must contain genuine JS, not renamed TypeScript.");
                Require(File.ReadAllText(Contained(source, module)).Contains("@ts-check", StringComparison.Ordinal), "Checked-JS source must opt into checking.");
            }
            var npmLock = ReadJson(Path.Combine(source, "package-lock.json"));
            Require(npmLock.GetProperty("lockfileVersion").GetInt32() == 3, "Expected retained npm lock v3.");
            Require(File.Exists(Path.Combine(source, "sunder-contracts.lock.json")), "Missing retained contract lock.");
            var feed = new Dictionary<string, (string Archive, string Root)>(StringComparer.Ordinal);
            var index = 0;
            long expanded = 0;
            var feedFiles = Directory.GetFiles(Path.Combine(unpacked, "feed"), "*", SearchOption.AllDirectories);
            Require(feedFiles.Length is > 0 and <= 128 && feedFiles.All(x => x.EndsWith(".tgz", StringComparison.Ordinal)), "Invalid/oversized npm closure inventory.");
            foreach (var archive in feedFiles)
            {
                var directory = Path.Combine(root, "feed-expanded", (index++).ToString());
                SdkCorpusNpm.ExtractPackage(archive, directory);
                expanded += Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
                Require(expanded <= 512L * 1024 * 1024, "Retained npm closure expansion exceeds bound.");
                var metadata = ReadJson(Path.Combine(directory, "package.json"));
                Require(feed.TryAdd(Text(metadata, "name") + "/" + Text(metadata, "version"), (archive, directory)), "Duplicate retained npm identity.");
            }
            var candidate = new Dictionary<string, (string Archive, string Root)>(StringComparer.Ordinal);
            foreach (var (asset, id) in new[] { ("sunder-sdk", "@sunder/sdk"), ("sunder-package-tool", "@sunder/package-tool") })
            {
                var record = input.Candidates.Single(x => Text(x, "assetName") == $"{asset}-{input.Version}.tgz");
                var directory = Path.Combine(root, asset);
                SdkCorpusNpm.ExtractPackage(Text(record, "path"), directory);
                var metadata = ReadJson(Path.Combine(directory, "package.json"));
                Require(Text(metadata, "name") == id && Text(metadata, "version") == input.Version, "Candidate npm identity mismatch.");
                candidate.Add(id, (Text(record, "path"), directory));
            }
            var os = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux";
            var cpu = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var resolved = new HashSet<string>();
            foreach (var package in npmLock.GetProperty("packages").EnumerateObject().Where(x => x.Name != ""))
            {
                var name = SdkCorpusNpm.PackageName(package.Name);
                if (!SdkCorpusNpm.Applies(package.Value, os, cpu)) continue;
                Require(feed.TryGetValue(name + "/" + Text(package.Value, "version"), out var original), "Missing retained npm closure: " + name);
                SdkCorpusNpm.RequireIntegrity(original.Archive, Text(package.Value, "integrity"));
                var selected = candidate.GetValueOrDefault(name, original);
                var destination = Contained(source, package.Name);
                SdkCorpusNpm.ExtractPackage(selected.Archive, destination);
                resolved.Add(name);
            }
            Require(candidate.Keys.All(resolved.Contains), "Original source must consume both @sunder/sdk and @sunder/package-tool.");
            foreach (var selected in candidate.Values)
            {
                var metadata = ReadJson(Path.Combine(selected.Root, "package.json"));
                if (!metadata.TryGetProperty("dependencies", out var dependencies)) continue;
                foreach (var dependency in dependencies.EnumerateObject().Where(x => !candidate.ContainsKey(x.Name)))
                {
                    var installed = ReadJson(Contained(source, "node_modules/" + dependency.Name + "/package.json"));
                    Require(Text(installed, "version") == dependency.Value.GetString(),
                        "This offline profile requires the candidate tool's exact dependency versions in the retained closure: " + dependency.Name);
                }
            }
            // Preserve the npm lock itself as the original dependency oracle; candidate replacements
            // are exact archive selections in the isolated materialized tree, not an unlocked restore.
            var manifestPath = Path.Combine(source, "package.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            foreach (var group in new[] { "dependencies", "devDependencies" })
                foreach (var id in candidate.Keys)
                    if (manifest[group]?[id] is not null) manifest[group]![id] = input.Version;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            originalFiles[manifestPath] = Hash(manifestPath);
            async Task<CapturedProcessResult> RunAsync(params string[] arguments)
            {
                var start = new ProcessStartInfo("node") { UseShellExecute = false, WorkingDirectory = source };
                start.Environment.Remove("NODE_PATH"); start.Environment.Remove("NODE_OPTIONS");
                start.Environment["SUNDER_CONTRACT_CACHE"] = Path.Combine(root, "contract-cache");
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                await using var process = CapturedProcess.Start(start, 8 * 1024 * 1024);
                return await process.EnsureSuccessAsync(TimeSpan.FromMinutes(3));
            }
            await RunAsync("-e", "const [major,minor,patch]=process.versions.node.split('.').map(Number);if(major!==24||minor<18||minor===18&&patch<1)throw Error('Node >=24.18.1 <25 required');if(process.platform!==process.argv[1]||process.arch!==process.argv[2])throw Error('Node process RID differs from selected native context')", os, cpu);
            var tool = Path.Combine(source, "node_modules/@sunder/package-tool/dist/cli.js");
            await RunAsync(tool, "generate", "--project", source);
            var tsconfig = ReadJson(Path.Combine(source, "tsconfig.json"));
            Require(tsconfig.GetProperty("compilerOptions").GetProperty("strict").GetBoolean(), "Source compiler must be strict.");
            if (extension == ".js") Require(tsconfig.GetProperty("compilerOptions").GetProperty("checkJs").GetBoolean(), "checkJs must actually be enabled.");
            var checkedSource = await RunAsync(Path.Combine(source, "node_modules/typescript/bin/tsc"), "--project", "tsconfig.json", "--noEmit", "--listFiles");
            Require(checkedSource.StandardOutput.Replace('\\', '/').Contains(Contained(source, module).Replace('\\', '/'), StringComparison.Ordinal), "Typecheck did not include the retained service source.");
            await RunAsync(tool, "build", "--project", source, "--rid", RuntimeInformation.RuntimeIdentifier);
            await RunAsync(tool, "package", "--project", source, "--discovery-root", Path.Combine(source, "dist/targets"));
            Require(File.Exists(Contained(source, Text(input.Layout, "packageOutput"))), "Candidate package tool did not produce the selected output.");
            foreach (var (path, hash) in originalFiles) Require(Hash(path) == hash, "Authored source/lock changed: " + path);
            var afterFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(path => !Path.GetRelativePath(source, path)
                .Split(Path.DirectorySeparatorChar).Any(part => part is "node_modules" or "dist" or ".sunder")).Order().ToArray();
            Require(afterFiles.SequenceEqual(originalFiles.Keys.Order()), "Node build wrote unexpected authored source/lock files.");
            foreach (var (id, selected) in candidate)
                foreach (var path in Directory.GetFiles(selected.Root, "*", SearchOption.AllDirectories))
                    Require(Hash(Path.Combine(source, "node_modules", id, Path.GetRelativePath(selected.Root, path))) == Hash(path), "Selected npm tool/SDK bytes changed.");
            var built = ReadJson(Path.Combine(source, "dist/package/manifest/sunder-package.json"));
            Require(Text(built, "id") == input.PackageId && Text(built, "version") == input.PackageVersion, "Built Node package identity differs.");
            Require(built.GetProperty("targets").EnumerateArray().All(x => Text(x, "sdkVersion") == input.Version), "Built Node target did not use candidate SDK.");
        }
        finally { input.VerifyUnchanged(); }
    }
}
