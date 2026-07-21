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

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
        => new ReadOnlyCollection<T>(values.ToArray());
}
