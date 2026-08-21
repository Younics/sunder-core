using Sunder.Sdk.Packaging;

namespace Sunder.Sdk.Worker.Internal;

internal sealed record WorkerEnvironment(
    string Protocol,
    string PackageId,
    string PackageVersion,
    string ActivationId,
    Guid ActivationGuid,
    string SessionId,
    string PackageContentPath,
    string PackageDataPath,
    string PackageWorkspacePath,
    bool RpcExtendedDeadlineV1)
{
    private const string RpcExtendedDeadlineEnvironmentVariable = "SUNDER_RPC_EXTENDED_DEADLINE_V1";

    public static WorkerEnvironment Capture(Func<string, string?> getEnvironmentVariable)
    {
        var protocol = Required(getEnvironmentVariable, "SUNDER_WORKER_PROTOCOL", 64);
        if (!string.Equals(protocol, WorkerProtocol.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SUNDER_WORKER_PROTOCOL does not select {WorkerProtocol.Name}.");
        }

        var packageId = Required(getEnvironmentVariable, "SUNDER_PACKAGE_ID", 256);
        if (!Sunder.Sdk.Packaging.PackageId.TryParse(packageId, out _))
        {
            throw new InvalidOperationException("SUNDER_PACKAGE_ID is not a canonical package identifier.");
        }

        var packageVersion = Required(getEnvironmentVariable, "SUNDER_PACKAGE_VERSION", 128);
        if (!SemanticVersion.TryParse(packageVersion, out _))
        {
            throw new InvalidOperationException("SUNDER_PACKAGE_VERSION is not a strict semantic version.");
        }

        var activationId = Required(getEnvironmentVariable, "SUNDER_ACTIVATION_ID", 64);
        if (!Guid.TryParseExact(activationId, "N", out var activationGuid))
        {
            throw new InvalidOperationException("SUNDER_ACTIVATION_ID is not a canonical activation identifier.");
        }

        var sessionId = Required(getEnvironmentVariable, "SUNDER_SESSION_ID", 256);
        if (!WorkerJson.IsSafeId(sessionId))
        {
            throw new InvalidOperationException("SUNDER_SESSION_ID contains unsupported characters.");
        }

        var contentPath = RequiredPath(getEnvironmentVariable, "SUNDER_PACKAGE_CONTENT_ROOT");
        var temporaryPath = RequiredPath(getEnvironmentVariable, "SUNDER_WORKER_TEMP_ROOT");
        var workspacePath = RequiredPath(getEnvironmentVariable, "SUNDER_PACKAGE_WORKSPACE_ROOT");
        var rpcExtendedDeadlineV1 = OptionalFlag(
            getEnvironmentVariable,
            RpcExtendedDeadlineEnvironmentVariable);
        if (PathsEqual(contentPath, temporaryPath)
            || PathsEqual(contentPath, workspacePath)
            || PathsEqual(temporaryPath, workspacePath))
        {
            throw new InvalidOperationException(
                "Worker V3 content, temporary, and persistent workspace roots must be distinct.");
        }

        return new WorkerEnvironment(
            protocol,
            packageId,
            packageVersion,
            activationId,
            activationGuid,
            sessionId,
            contentPath,
            temporaryPath,
            workspacePath,
            rpcExtendedDeadlineV1);
    }

    private static bool OptionalFlag(
        Func<string, string?> getEnvironmentVariable,
        string name)
    {
        var value = getEnvironmentVariable(name);
        if (value is null) return false;
        if (value == "1") return true;
        throw new InvalidOperationException($"{name} must be absent or exactly '1'.");
    }

    private static string Required(
        Func<string, string?> getEnvironmentVariable,
        string name,
        int maximumLength)
    {
        var value = getEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            throw new InvalidOperationException($"{name} must be a non-empty bounded value.");
        }
        return value;
    }

    private static string? OptionalPath(Func<string, string?> getEnvironmentVariable, string name)
    {
        var value = getEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 4096)
        {
            throw new InvalidOperationException($"{name} exceeds the worker path limit.");
        }
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{name} is not a valid filesystem path.", exception);
        }
    }

    private static string RequiredPath(Func<string, string?> getEnvironmentVariable, string name)
    {
        var path = OptionalPath(getEnvironmentVariable, name)
                   ?? throw new InvalidOperationException($"{name} must be a non-empty absolute path.");
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new InvalidOperationException($"{name} must identify an existing absolute directory.");
        }
        return path;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
