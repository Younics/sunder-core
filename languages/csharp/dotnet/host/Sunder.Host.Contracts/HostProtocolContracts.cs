using System.Collections.ObjectModel;

namespace Sunder.Host.Contracts;

public static class HostProtocol
{
    public const string Identity = "dev.sunder.host";
    public const int CurrentRevision = 2;
    public const int MinimumSupportedRevision = 2;
    public const int MaximumSupportedRevision = 2;
}

public static class HostProtocolFeatures
{
    public const string RuntimeGatewayV1 = "runtime-gateway.v1";
    public const string RuntimeLifecycleV1 = "runtime-lifecycle.v1";
    public const string DurableOperationsV1 = "durable-operations.v1";
}

public static class HostDeploymentIdentity
{
    public const string Sha256Prefix = "host-payload-v1:sha256:";

    public static string FromSha256(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 || sha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Host deployment SHA-256 must contain exactly 64 hexadecimal characters.", nameof(sha256));
        }
        return Sha256Prefix + sha256.ToLowerInvariant();
    }

    public static bool IsValid(string? value)
    {
        if (value is null
            || value.Length != Sha256Prefix.Length + 64
            || !value.StartsWith(Sha256Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var character in value.AsSpan(Sha256Prefix.Length))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }
}

public sealed record HostProductVersionDiagnostics(
    string ProductName,
    string ProductVersion,
    string InformationalVersion);

public sealed record HostHandshakeResponse(
    string ProtocolIdentity,
    int ProtocolRevision,
    int MinimumSupportedRevision,
    int MaximumSupportedRevision,
    Guid HostId,
    Guid SupervisorInstanceId,
    IReadOnlyList<string> SupportedFeatures,
    HostProductVersionDiagnostics Product)
{
    private IReadOnlyList<string> _supportedFeatures = Freeze(SupportedFeatures);

    public IReadOnlyList<string> SupportedFeatures
    {
        get => _supportedFeatures;
        init => _supportedFeatures = Freeze(value);
    }

    public string? DeploymentIdentity { get; init; }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
        => new ReadOnlyCollection<T>(values.ToArray());
}
