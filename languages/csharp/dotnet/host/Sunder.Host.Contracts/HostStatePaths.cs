namespace Sunder.Host.Contracts;

public static class HostStatePaths
{
    public static string GetDefaultRootPath()
    {
        var configured = Environment.GetEnvironmentVariable("SUNDER_HOST_STATE_ROOT");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sunder",
                "host",
                "v1")
            : Path.GetFullPath(configured);
    }
}
