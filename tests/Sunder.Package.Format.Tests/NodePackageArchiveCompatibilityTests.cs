using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class NodePackageArchiveCompatibilityTests
{
    [Fact]
    public async Task NodePackageArchive_WhenConfigured_IsAcceptedByDotNetInspector()
    {
        var archivePath = Environment.GetEnvironmentVariable("SUNDER_NODE_SEA_PACKAGE_PATH");
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        Assert.True(File.Exists(archivePath), $"Node SEA package does not exist: {archivePath}");
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-node-sea-validation",
            Guid.NewGuid().ToString("N"));
        try
        {
            var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, stagingPath);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var targets = result.Manifest!.Targets!
                .Select(Assert.IsType<SunderPackageTargetManifest>)
                .ToArray();
            Assert.NotEmpty(targets);
            Assert.All(targets, target =>
            {
                Assert.Equal(SunderPackageFormat.RuntimeHostRole, target.Role);
                Assert.Equal(SunderPackageFormat.ProcessTargetKind, target.Kind);
            });

            var expectedRids = Environment.GetEnvironmentVariable("SUNDER_NODE_SEA_EXPECTED_RIDS");
            if (!string.IsNullOrWhiteSpace(expectedRids))
            {
                Assert.Equal(
                    expectedRids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Order(StringComparer.Ordinal),
                    targets.Select(static target => target.Rid!).Order(StringComparer.Ordinal));
            }
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }
}
