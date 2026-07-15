using System.Diagnostics;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal static class RuntimeHostStartInfoFactory
{
    public static ProcessStartInfo Create(
        string runtimeHostPath,
        Uri runtimeUrl,
        string connectionInfoPath)
    {
        var runtimeUrlText = runtimeUrl.ToString().TrimEnd('/');
        var isDotnetAssembly = string.Equals(Path.GetExtension(runtimeHostPath), ".dll", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo(isDotnetAssembly ? "dotnet" : runtimeHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtimeHostPath)!,
        };
        startInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"] = connectionInfoPath;

        if (isDotnetAssembly)
        {
            startInfo.ArgumentList.Add(runtimeHostPath);
        }

        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(runtimeUrlText);
        return startInfo;
    }
}
