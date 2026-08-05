namespace Sunder.App.Tests.TestSupport;

internal static class TestPaths
{
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sunder-app-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string GetSunderAppOutputDirectory()
    {
        var targetPathFile = Path.Combine(AppContext.BaseDirectory, "sunder-app-target-path.txt");
        var targetPath = File.ReadAllText(targetPathFile).Trim();
        return Path.GetDirectoryName(targetPath)
               ?? throw new InvalidDataException("The captured Sunder App target path has no directory.");
    }
}
