using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class BuildFixtureSdkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    [InlineData("parent")]
    [InlineData("sdk-map")]
    public async Task DetachedFixture_UsesRepositorySelectionWithoutReplacingAuthoredGlobalJson(string? selection)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-fixture-sdk-selection");
        var repository = scope.CreateDirectory("repository");
        var parent = scope.CreateDirectory("fixture");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        var repositoryBytes = "{\"sdk\":{\"version\":\"10.0.403\",\"rollForward\":\"disable\",\"allowPrerelease\":false}}"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(repository, "global.json"), repositoryBytes);
        var authored = selection == "sdk-map"
            ? "{\"msbuild-sdks\":{\"Sunder.Package.Build\":\"1.1.0-test\"}}"u8.ToArray()
            : "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"disable\"}}"u8.ToArray();
        var selectedPath = Path.Combine(selection == "parent" ? parent : child, "global.json");
        if (selection is not null) await File.WriteAllBytesAsync(selectedPath, authored);

        BuildFixtureSdk.EnsureRepositorySelection(child, repository);
        BuildFixtureSdk.EnsureRepositorySelection(child, repository);

        Assert.Equal(selection is null ? repositoryBytes : authored, await File.ReadAllBytesAsync(selectedPath));
        Assert.Equal(repositoryBytes, await File.ReadAllBytesAsync(Path.Combine(repository, "global.json")));
        if (selection == "parent") Assert.False(File.Exists(Path.Combine(child, "global.json")));
    }
}
