using System.Diagnostics;

namespace Sunder.App.Services;

internal static class RuntimeHostStartInfoFactory
{
    public static ProcessStartInfo Create(string runtimeHostPath, Uri runtimeUrl)
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
        return startInfo;
    }
}
