namespace Sunder.Package.Format;

internal static class DotNetWorkerPackagePolicy
{
    private static readonly HashSet<string> BuildTimeSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "analyzers",
        "build",
        "buildMultiTargeting",
        "buildTransitive",
        "obj",
        "packs",
        "ref",
        "refs",
        "sdk",
        "tools",
    };

    private static readonly HashSet<string> BuildTimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".a",
        ".cs",
        ".csproj",
        ".exp",
        ".fs",
        ".fsproj",
        ".ilk",
        ".lib",
        ".props",
        ".rsp",
        ".targets",
        ".tasks",
        ".vb",
        ".vbproj",
    };

    private static readonly HashSet<string> RuntimeStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "apphost",
        "clrgc",
        "clrgcexp",
        "clrjit",
        "coreclr",
        "createdump",
        "dbgshim",
        "dotnet",
        "hostfxr",
        "hostpolicy",
        "mscordaccore",
        "mscordbi",
        "singlefilehost",
        "system.globalization.native",
        "system.io.compression.native",
        "system.native",
        "system.net.security.native",
        "system.private.corelib",
        "system.security.cryptography.native.apple",
        "system.security.cryptography.native.openssl",
    };

    internal static bool IsRuntimePackPackageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var id = DependencyId(value);
        return id.StartsWith("runtimepack.", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("Microsoft.NETCore.App.Host.", StringComparison.OrdinalIgnoreCase)
               || id.Equals("Microsoft.NETCore.App.Ref", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("Microsoft.AspNetCore.App.Runtime.", StringComparison.OrdinalIgnoreCase)
               || id.Equals("Microsoft.AspNetCore.App.Ref", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("Microsoft.WindowsDesktop.App.Runtime.", StringComparison.OrdinalIgnoreCase)
               || id.Equals("Microsoft.WindowsDesktop.App.Ref", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("Microsoft.NETCore.DotNetHost", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("Microsoft.NETCore.DotNetAppHost", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsToolchainPackageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var id = DependencyId(value);
        return id.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("Microsoft.NET.Sdk.", StringComparison.OrdinalIgnoreCase)
               || id.Equals("Microsoft.Build", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("Microsoft.Build.", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSymbolFile(string path)
        => Extension(path) is ".pdb" or ".mdb";

    internal static bool IsBuildTimePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Split('/').Any(BuildTimeSegments.Contains)
               || BuildTimeExtensions.Contains(Extension(normalized));
    }

    internal static bool IsForbiddenRuntimeFile(string path)
    {
        var stem = RuntimeStem(FileName(path));
        return RuntimeStems.Contains(stem)
               || stem.StartsWith("mscordaccore_", StringComparison.OrdinalIgnoreCase)
               || stem.StartsWith("mscordbi_", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAppHostForEntryPoint(string candidatePath, string entryPointPath)
    {
        var normalizedCandidate = candidatePath.Replace('\\', '/');
        var normalizedEntryPoint = entryPointPath.Replace('\\', '/');
        var entryExtension = Extension(normalizedEntryPoint);
        if (!entryExtension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        var entryStemPath = normalizedEntryPoint[..^entryExtension.Length];
        return normalizedCandidate.Equals(entryStemPath, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.Equals(entryStemPath + ".exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPotentialSharedFrameworkAssembly(string path)
    {
        var fileName = FileName(path);
        return fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("WindowsBase.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("Microsoft.CSharp.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("Microsoft.VisualBasic.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("Microsoft.VisualBasic.Core.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("System.dll", StringComparison.OrdinalIgnoreCase)
               || fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
               && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    internal static string FileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static string DependencyId(string value)
    {
        var separator = value.LastIndexOf('/');
        return separator > 0 ? value[..separator] : value;
    }

    private static string Extension(string path)
        => Path.GetExtension(FileName(path)).ToLowerInvariant();

    private static string RuntimeStem(string fileName)
    {
        var stem = fileName.ToLowerInvariant();
        var sharedObject = stem.IndexOf(".so", StringComparison.Ordinal);
        if (sharedObject > 0)
        {
            stem = stem[..sharedObject];
        }
        else
        {
            foreach (var extension in new[] { ".dylib", ".dll", ".exe" })
            {
                if (stem.EndsWith(extension, StringComparison.Ordinal))
                {
                    stem = stem[..^extension.Length];
                    break;
                }
            }
        }
        return stem.StartsWith("lib", StringComparison.Ordinal) ? stem[3..] : stem;
    }
}
