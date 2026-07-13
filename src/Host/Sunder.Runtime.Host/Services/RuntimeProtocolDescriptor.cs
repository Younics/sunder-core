using System.Reflection;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeProtocolDescriptor
{
    private static readonly IReadOnlyList<string> Features = Array.AsReadOnly(new[]
    {
        RuntimeProtocolFeatures.VersionedApiV1,
        RuntimeProtocolFeatures.PackageRuntimeOperationsV1,
        RuntimeProtocolFeatures.PackageRuntimeStreamEnvelopesV1,
        RuntimeProtocolFeatures.PackageSessionDrainingV1,
    });

    public RuntimeProtocolDescriptor()
    {
        var assembly = typeof(RuntimeProtocolDescriptor).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var productVersion = RuntimeHostVersion.Current;

        Handshake = new RuntimeHandshakeResponse(
            RuntimeProtocol.Identity,
            RuntimeProtocol.CurrentRevision,
            RuntimeProtocol.MinimumSupportedRevision,
            RuntimeProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            Features,
            new RuntimeProductVersionDiagnostics(
                "Sunder.Runtime.Host",
                productVersion,
                string.IsNullOrWhiteSpace(informationalVersion) ? productVersion : informationalVersion));
    }

    public RuntimeHandshakeResponse Handshake { get; }
}
