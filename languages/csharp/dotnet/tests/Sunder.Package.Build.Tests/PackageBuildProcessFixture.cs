using System.Diagnostics;
using static Sunder.Package.Build.Tests.PackageManifestFixture;

namespace Sunder.Package.Build.Tests;

internal static class PackageBuildProcessFixture
{
    internal static Process StartMsbuild(string projectFile, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectFile);
        startInfo.ArgumentList.Add("-t:Run");
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start concurrent MSBuild process.");
    }

    internal static async Task<ProcessResult> ReadProcessAsync(Process process)
    {
        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(
                process.ExitCode,
                await standardOutput + Environment.NewLine + await standardError);
        }
    }

    internal static Process StartNode(string helper, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(helper);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Node lock helper.");
    }

    internal static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out waiting for '{path}'.");
            await Task.Delay(25);
        }
    }

    internal static string FindTaskDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "languages",
                "csharp",
                "dotnet",
                "packages",
                "Sunder.Package.Build",
                "Tasks");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Sunder.Package.Build tasks.");
    }

    internal sealed record ProcessResult(int ExitCode, string Output);
}
