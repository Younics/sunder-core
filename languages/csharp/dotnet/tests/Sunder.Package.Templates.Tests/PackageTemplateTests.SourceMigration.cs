using Xunit;

namespace Sunder.Package.Templates.Tests;

public sealed partial class PackageTemplateTests
{
    private static async Task<Dictionary<string, byte[]>> CaptureTemplateInputsAsync(string root)
    {
        var inputs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
            if (Path.GetExtension(path) is not (".cs" or ".csproj" or ".props" or ".axaml")
                && Path.GetFileName(path) != "sunder-contracts.lock.json") continue;
            inputs.Add(relative, await File.ReadAllBytesAsync(path));
        }
        return inputs;
    }

    private static async Task AssertTemplateInputsAsync(string root, Dictionary<string, byte[]> expected)
    {
        var actual = await CaptureTemplateInputsAsync(root);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, actual[path]);
    }

    private static async Task VerifyTemplateRebuildAsync(string root, string project, string binding, byte[] baseline)
    {
        var generated = Path.GetDirectoryName(Path.GetDirectoryName(binding))!;
        var snapshot = Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path)));
        AssertSuccess(await RunDotnetAsync(root, "build", project, "--no-restore", "--disable-build-servers"));
        foreach (var (path, prior) in snapshot)
        {
            Assert.Equal(prior.Bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(prior.Modified, File.GetLastWriteTimeUtc(path));
        }
        AssertSuccess(await RunDotnetAsync(root, "build", project, "--no-restore", "-t:Rebuild", "--disable-build-servers"));
        Assert.True(File.Exists(binding));
        Assert.Equal(baseline, await File.ReadAllBytesAsync(Path.Combine(root, "sunder-contracts.owned.json")));
        Assert.False(File.Exists(Path.Combine(root, "sunder-package.json")));
    }

    // Explicit acceptance lane retains only this test's valid generated package for
    // the real signed-runtime Host smoke test, outside the disposable NuGet/project tree.
    private static async Task RetainTemplateSmokeArtifactAsync(string source)
    {
        var root = Environment.GetEnvironmentVariable("SUNDER_TEMPLATE_TEST_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var destination = Path.Combine(Path.GetFullPath(root), "csharp-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var output = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, await File.ReadAllBytesAsync(file));
        }
    }
}
