using Xunit;

namespace Sunder.Core.TestSupport;

internal sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> FileLinks = new(() => CanCreateLink(directory: false));
    private static readonly Lazy<bool> DirectoryLinks = new(() => CanCreateLink(directory: true));

    public SymbolicLinkFactAttribute(bool directory)
    {
        // Required qualification must execute the fixture and fail, never silently
        // accept missing Windows privileges as a successful security check.
        if (Environment.GetEnvironmentVariable("SUNDER_REQUIRE_TEST_PREREQUISITES") == "1") return;
        if (!(directory ? DirectoryLinks : FileLinks).Value)
            Skip = "Windows symbolic-link creation privilege is unavailable.";
    }

    private static bool CanCreateLink(bool directory)
    {
        var root = Directory.CreateTempSubdirectory("sunder-link-probe-").FullName;
        try
        {
            var target = Path.Combine(root, "target");
            var link = Path.Combine(root, "link");
            if (directory)
            {
                Directory.CreateDirectory(target);
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.WriteAllText(target, "probe");
                File.CreateSymbolicLink(link, target);
            }
            return true;
        }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
        {
            return false;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

internal sealed class SymbolicLinkTheoryAttribute : TheoryAttribute
{
    public SymbolicLinkTheoryAttribute(bool directory, bool file = false)
    {
        Skip = new SymbolicLinkFactAttribute(directory).Skip;
        if (Skip is null && file) Skip = new SymbolicLinkFactAttribute(directory: false).Skip;
    }
}
