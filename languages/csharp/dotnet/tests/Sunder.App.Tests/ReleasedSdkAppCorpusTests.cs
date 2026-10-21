using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Xunit;
using static Sunder.Core.TestSupport.SdkCorpusInput;

namespace Sunder.App.Tests;

public sealed class ReleasedSdkAppCorpusTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_SDK_CORPUS", "SUNDER_COMPATIBILITY_CONTEXT")]
    [Trait("Category", "ExternalArtifact")]
    public async Task RetainedAppPackageActivatesWithProvidedCandidateAppIdentity()
    {
        var input = Load("old-package-new-host");
        Require(Text(input.Layout, "profile") == "avalonia-app-probe-v1" && input.PackageId == "compatibility.app.probe", "Wrong App artifact profile.");
        using var work = TemporaryDirectoryScope.Create("sunder-app-sdk-corpus");
        try
        {
            var root = WorkPath(work.RootPath);
            var package = input.Artifact(Text(input.Layout, "packageArtifactId"), "package");
            using (var archive = ZipFile.OpenRead(package))
            {
                var entry = archive.GetEntry("manifest/sunder-package.json");
                Require(entry is not null && entry.Length <= 1024 * 1024, "Missing/oversized retained App manifest.");
                using var metadata = JsonDocument.Parse(entry!.Open());
                Assert.Equal(input.PackageId, Text(metadata.RootElement, "id"));
                Assert.Equal(input.PackageVersion, Text(metadata.RootElement, "version"));
            }
            var app = Path.Combine(root, "app");
            Extract(input.AppArtifact(), app);
            foreach (var id in new[] { "Sunder.Sdk", "Sunder.Sdk.Avalonia" })
            {
                var selected = input.Candidates.Single(x => Text(x, "assetName") == $"{id}.{input.Version}.nupkg");
                using var archive = ZipFile.OpenRead(Text(selected, "path"));
                using var stream = archive.GetEntry($"lib/net10.0/{id}.dll")!.Open();
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stream)), Hash(Path.Combine(app, id + ".dll")));
            }
            var hashes = Directory.GetFiles(app, "*", SearchOption.AllDirectories).ToDictionary(x => x, Hash);
            var nonce = Guid.NewGuid().ToString("N");
            var probe = Path.Combine(AppContext.BaseDirectory, "corpus-probe/Sunder.App.Tests.dll");
            Require(File.Exists(probe), "Missing checked-in compiled App owner probe.");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false };
            foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
                || x.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            start.Environment["SUNDER_RUNTIME_STATE_ROOT"] = Path.Combine(root, "state");
            start.Environment["SUNDER_APP_STATE_ROOT"] = Path.Combine(root, "app-state");
            foreach (var arg in new[] { "exec", "--runtimeconfig", Path.Combine(app, "Sunder.App.runtimeconfig.json"), probe,
                app, package, root, nonce }) start.ArgumentList.Add(arg);
            await using var process = CapturedProcess.Start(start, 8 * 1024 * 1024);
            var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(90));
            using var report = JsonDocument.Parse(result.StandardOutput.Trim());
            Assert.Equal(nonce, Text(report.RootElement, "nonce"));
            Assert.Equal(Path.Combine(app, "Sunder.App.dll"), Text(report.RootElement, "app"));
            Assert.Equal(Path.Combine(app, "Sunder.Sdk.dll"), Text(report.RootElement, "sdk"));
            Assert.Equal(Path.Combine(app, "Sunder.Sdk.Avalonia.dll"), Text(report.RootElement, "avaloniaSdk"));
            Assert.Equal(input.PackageId, Text(report.RootElement, "packageId"));
            Assert.Equal(input.PackageVersion, Text(report.RootElement, "packageVersion"));
            Assert.Equal(Text(input.Context, "actualRid"), Text(report.RootElement, "rid"));
            Assert.True(report.RootElement.GetProperty("disposed").GetBoolean());
            Assert.True(report.RootElement.GetProperty("isolatedPackage").GetBoolean());
            Assert.Equal(hashes.Keys.Order(), Directory.GetFiles(app, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, hash) in hashes) Assert.Equal(hash, Hash(path));
        }
        finally { input.VerifyUnchanged(); }
    }
}
