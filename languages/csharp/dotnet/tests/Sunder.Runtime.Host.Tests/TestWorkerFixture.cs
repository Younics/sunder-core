using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Tests;

internal static class TestWorkerFixture
{
    internal const string AssemblyFileName = "Sunder.Runtime.Host.TestWorker.dll";
    private const string DependencyFileName = "Sunder.Runtime.Host.TestWorker.deps.json";
    private const string RuntimeConfigFileName = "Sunder.Runtime.Host.TestWorker.runtimeconfig.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string AssemblyPath => Path.Combine(OutputDirectory, AssemblyFileName);

    internal static string CopyAppHost(string destination)
    {
        const string baseName = "Sunder.Runtime.Host.Tests.TestWorkerAppHost";
        var fileName = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
        var source = Path.Combine(AppContext.BaseDirectory, fileName);
        var target = Path.Combine(destination, fileName);
        File.Copy(source, target, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, File.GetUnixFileMode(source));
        }
        return target;
    }

    internal static string DotNetHostPath
    {
        get
        {
            var runtimeDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(
                RuntimeEnvironment.GetRuntimeDirectory()));
            var dotNetRoot = runtimeDirectory.Parent?.Parent?.Parent
                             ?? throw new DirectoryNotFoundException("The current .NET root is unavailable.");
            var path = Path.Combine(
                dotNetRoot.FullName,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The current .NET host is unavailable.", path);
            }
            return path;
        }
    }

    internal static void CopyPayload(string destination, string? rid = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var fileName in new[] { AssemblyFileName, DependencyFileName, RuntimeConfigFileName })
        {
            var source = Path.Combine(OutputDirectory, fileName);
            var target = Path.Combine(destination, fileName);
            if (fileName == DependencyFileName)
            {
                WriteDependencyManifestForRid(
                    source,
                    target,
                    rid ?? PackageTargetSelection.GetCurrentRuntimeIdentifier());
            }
            else
            {
                File.Copy(source, target, overwrite: true);
            }
        }
    }

    internal static ProcessStartInfo CreateProcessStartInfo(
        string workingDirectory,
        string? assemblyPath = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DotNetHostPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Path.GetFullPath(assemblyPath ?? AssemblyPath));
        return startInfo;
    }

    internal static ProcessStartInfo CreateAppHostProcessStartInfo(
        string appHostPath,
        string workingDirectory)
        => new()
        {
            FileName = Path.GetFullPath(appHostPath),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

    private static string OutputDirectory
        => Path.Combine(
            WorkerReleaseMetrics.FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Runtime.Host.TestWorker",
            "bin",
            ResolveConfiguration(),
            "net10.0",
            PackageTargetSelection.GetCurrentRuntimeIdentifier());

    private static void WriteDependencyManifestForRid(string sourcePath, string destinationPath, string rid)
    {
        var document = JsonNode.Parse(File.ReadAllText(sourcePath))?.AsObject()
                       ?? throw new InvalidDataException("The test Worker dependency manifest is empty.");
        var runtimeTarget = document["runtimeTarget"]?.AsObject()
                            ?? throw new InvalidDataException("The test Worker dependency manifest has no runtime target.");
        var sourceTargetName = runtimeTarget["name"]?.GetValue<string>()
                               ?? throw new InvalidDataException("The test Worker dependency manifest has no runtime target name.");
        var targets = document["targets"]?.AsObject()
                      ?? throw new InvalidDataException("The test Worker dependency manifest has no target graph.");
        var targetGraph = targets[sourceTargetName]
                          ?? throw new InvalidDataException("The test Worker dependency manifest has no selected target graph.");
        var targetName = $".NETCoreApp,Version=v10.0/{rid}";
        if (!string.Equals(sourceTargetName, targetName, StringComparison.Ordinal))
        {
            targets.Remove(sourceTargetName);
            targets[targetName] = targetGraph;
            runtimeTarget["name"] = targetName;
        }
        File.WriteAllText(destinationPath, document.ToJsonString(JsonOptions));
    }

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
            .Parent?.Name
           ?? "Debug";
}
