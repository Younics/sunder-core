using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Sunder.Runtime.Client;

public sealed record RuntimeV1ResetCategoryResult(string Category, string Status);

public sealed record RuntimeV1ResetResult(IReadOnlyList<RuntimeV1ResetCategoryResult> Categories)
{
    public bool Success => Categories.All(result => result.Status is "reset" or "already-empty");
}

public static class RuntimeV1StateReset
{
    private const string MacOsService = "io.sunder.runtime.v1.package-storage.master-key";
    private const string LinuxAttribute = "sunder-runtime-v1-master-key";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static Task<RuntimeV1ResetResult> ResetAsync(
        TimeSpan leaseWait,
        CancellationToken cancellationToken = default)
        => ResetCoreAsync(leaseWait, cancellationToken, RuntimeLocalState.GetV1RootPath());

    internal static Task<RuntimeV1ResetResult> ResetAsync(
        TimeSpan leaseWait,
        string rootPath,
        CancellationToken cancellationToken = default)
        => ResetCoreAsync(leaseWait, cancellationToken, rootPath);

    private static async Task<RuntimeV1ResetResult> ResetCoreAsync(
        TimeSpan leaseWait,
        CancellationToken cancellationToken,
        string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        RuntimeLocalState.Validate(root);
        if (!Directory.Exists(root))
        {
            return EmptyResult();
        }

        if (IsReparsePoint(root))
        {
            throw new InvalidDataException("The Runtime V1 state root cannot be reset through a symbolic link or reparse point.");
        }

        var leasePath = ContainedPath(root, RuntimeLocalState.LeaseFileName);
        await using var lease = await AcquireLeaseAsync(leasePath, leaseWait, cancellationToken).ConfigureAwait(false);
        RuntimeLocalState.Validate(root);

        var results = new List<RuntimeV1ResetCategoryResult>();
        ResetCategory(results, "package-catalog-and-payloads", root, ["catalog", "installed", "staging", "transactions", "tombstones"]);
        var packageDataCredentialsDeleted = ResetCredentialCategory(results, root);
        if (packageDataCredentialsDeleted)
        {
            ResetCategory(results, "package-state-files-secrets-and-logs", root, ["package-data"]);
        }
        else
        {
            results.Add(new("package-state-files-secrets-and-logs", "partial"));
        }
        ResetCategory(results, "uploads-and-snapshots", root, ["transfers"]);
        ResetCategory(results, "runtime-connection", root, ["connection.json"]);

        await lease.DisposeAsync().ConfigureAwait(false);
        if (results.All(result => result.Status is "reset" or "already-empty"))
        {
            results.Add(new("runtime-v1-root", TryDeleteRoot(root) ? "reset" : "partial"));
        }
        else
        {
            results.Add(new("runtime-v1-root", "partial"));
        }

        return new RuntimeV1ResetResult(results);
    }

    private static RuntimeV1ResetResult EmptyResult() => new([
        new("package-catalog-and-payloads", "already-empty"),
        new("package-state-files-secrets-and-logs", "already-empty"),
        new("uploads-and-snapshots", "already-empty"),
        new("registry-credentials", "already-empty"),
        new("runtime-connection", "already-empty"),
        new("runtime-v1-root", "already-empty"),
    ]);

    private static async Task<FileStream> AcquireLeaseAsync(string leasePath, TimeSpan wait, CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < expiresAt)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (DateTimeOffset.UtcNow < expiresAt)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("The Runtime V1 state is locked. Stop the Runtime and retry the reset.", exception);
            }
        }
    }

    private static bool ResetCredentialCategory(List<RuntimeV1ResetCategoryResult> results, string root)
    {
        var credentialPath = ContainedPath(root, "credentials");
        var credentialKeysDeleted = TryDeleteRegistryExternalKey(credentialPath, out var foundCredentials);
        if (credentialKeysDeleted && Directory.Exists(credentialPath))
        {
            foundCredentials = true;
            try
            {
                Directory.Delete(credentialPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                credentialKeysDeleted = false;
            }
        }

        var packageDataKeysDeleted = TryDeletePackageExternalKeys(ContainedPath(root, "package-data"));
        results.Add(new(
            "registry-credentials",
            !credentialKeysDeleted ? "partial" : foundCredentials ? "reset" : "already-empty"));
        return packageDataKeysDeleted;
    }

    private static bool TryDeleteRegistryExternalKey(string root, out bool found)
    {
        found = Directory.Exists(root);
        if (!found)
        {
            return true;
        }
        if (IsReparsePoint(root))
        {
            return false;
        }

        var registryRoot = Path.Combine(root, "registry");
        if (Directory.Exists(registryRoot) && IsReparsePoint(registryRoot))
        {
            return false;
        }

        var keyFile = Path.Combine(registryRoot, "credentials.enc.json.key");
        return !File.Exists(keyFile) || TryDeleteExternalKey(keyFile);
    }

    private static bool TryDeletePackageExternalKeys(string root)
    {
        if (!Directory.Exists(root))
        {
            return true;
        }
        if (IsReparsePoint(root))
        {
            return false;
        }

        try
        {
            var succeeded = true;
            foreach (var packageRoot in Directory.EnumerateDirectories(root))
            {
                if (IsReparsePoint(packageRoot))
                {
                    succeeded = false;
                    continue;
                }

                var dataRoot = Path.Combine(packageRoot, "data");
                if (Directory.Exists(dataRoot) && IsReparsePoint(dataRoot))
                {
                    succeeded = false;
                    continue;
                }

                var keyFile = Path.Combine(dataRoot, "secrets.json.key");
                succeeded &= !File.Exists(keyFile) || TryDeleteExternalKey(keyFile);
            }

            return succeeded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteExternalKey(string keyFile)
    {
        try
        {
            if (IsReparsePoint(keyFile))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(keyFile));
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("format").GetString(), "sunder.package-master-key", StringComparison.Ordinal)
                || root.GetProperty("version").GetInt32() != 1)
            {
                return false;
            }

            var protection = root.GetProperty("protection");
            var scheme = protection.GetProperty("scheme").GetString();
            if (scheme is "restricted-user-file" or "dpapi-current-user")
            {
                return true;
            }

            var identifier = Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("payload").GetString()!));
            if (!Guid.TryParseExact(identifier, "N", out _))
            {
                return false;
            }

            if (scheme == "macos-keychain")
            {
                return !OperatingSystem.IsMacOS()
                    || Run("/usr/bin/security", ["delete-generic-password", "-a", identifier, "-s", MacOsService]);
            }

            if (scheme == "linux-secret-service")
            {
                var secretTool = FindExecutable("secret-tool");
                return !OperatingSystem.IsLinux()
                    || secretTool is not null && Run(secretTool, ["clear", LinuxAttribute, identifier]);
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static void ResetCategory(
        List<RuntimeV1ResetCategoryResult> results,
        string category,
        string root,
        IReadOnlyList<string> relativePaths)
    {
        var found = false;
        var failed = false;
        foreach (var relativePath in relativePaths)
        {
            var path = ContainedPath(root, relativePath);
            if (Directory.Exists(path))
            {
                found = true;
                if (IsReparsePoint(path))
                {
                    failed = true;
                    continue;
                }
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                }
            }
            else if (File.Exists(path))
            {
                found = true;
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                }
            }
        }

        results.Add(new(category, failed ? "partial" : found ? "reset" : "already-empty"));
    }

    private static string ContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Reset paths must be fixed relative V1 paths.");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison))
        {
            throw new InvalidOperationException("A Runtime reset path escaped the V1 root.");
        }

        return path;
    }

    private static bool Run(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            return process is not null && process.WaitForExit(5000) && process.ExitCode is 0 or 44;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static bool TryDeleteRoot(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
