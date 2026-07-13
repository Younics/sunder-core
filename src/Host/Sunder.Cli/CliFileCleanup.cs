namespace Sunder.Cli;

internal static class CliFileCleanup
{
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup cannot replace the primary operation failure.
        }
    }
}
