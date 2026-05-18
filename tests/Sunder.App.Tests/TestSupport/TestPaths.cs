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
}
