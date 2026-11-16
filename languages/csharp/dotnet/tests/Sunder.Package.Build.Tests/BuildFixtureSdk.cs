namespace Sunder.Package.Build.Tests;

internal static class BuildFixtureSdk
{
    internal static void EnsureRepositorySelection(string workingDirectory, string repositoryRoot)
    {
        // DOTNET_HOST_PATH selects the launcher, not its SDK. Detached fixtures
        // otherwise use the newest installed SDK and mix its MSBuild references
        // with the repository's net10 task/runtime inputs.
        for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
        {
            // Preserve explicit SDK-floor probes and SDK package mappings, too.
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))) return;
        }
        File.Copy(Path.Combine(repositoryRoot, "global.json"), Path.Combine(workingDirectory, "global.json"));
    }
}
