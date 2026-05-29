using System.Diagnostics;

namespace Sunder.App.Services;

internal static class RuntimeHostStartInfoFactory
{
    public static ProcessStartInfo Create(
        string runtimeHostPath,
        Uri runtimeUrl,
        IReadOnlyList<string>? devPackageFolders = null)
    {
        var runtimeUrlText = runtimeUrl.ToString().TrimEnd('/');
        var isDotnetAssembly = string.Equals(Path.GetExtension(runtimeHostPath), ".dll", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo(isDotnetAssembly ? "dotnet" : runtimeHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtimeHostPath)!,
        };

        if (isDotnetAssembly)
        {
            startInfo.ArgumentList.Add(runtimeHostPath);
        }

        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(runtimeUrlText);
        foreach (var folder in devPackageFolders ?? [])
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            startInfo.ArgumentList.Add("--dev-package");
            startInfo.ArgumentList.Add(folder);
        }

        return startInfo;
    }
}
