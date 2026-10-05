using System.Reflection;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeProtocolDescriptor
{
    private static readonly IReadOnlyList<string> Features = Array.AsReadOnly(new[]
    {
        RuntimeProtocolFeatures.VersionedApiV1,
        RuntimeProtocolFeatures.PackageSessionDrainingV1,
        RuntimeProtocolFeatures.AtomicPackageSnapshotV1,
        RuntimeProtocolFeatures.PackageStageStatusV1,
        RuntimeProtocolFeatures.DevPackageOwnerLeasesV1,
        RuntimeProtocolFeatures.DevPackageImportsV1,
        RuntimeProtocolFeatures.TargetAwarePackageSnapshotsV1,
        RuntimeProtocolFeatures.StartupDiagnosticsV1,
        RuntimeProtocolFeatures.SchemaFirstRpcV1,
        RuntimeProtocolFeatures.RpcPermissionsV1,
        RuntimeProtocolFeatures.AppWebRpcV1,
        RuntimeProtocolFeatures.RuntimeResourcePolicyV1,
        RuntimeProtocolFeatures.PackageRuntimeRecoveryV1,
        RuntimeProtocolFeatures.ExecutionRuntimeManagementV1,
    });

    public RuntimeProtocolDescriptor(RuntimeResourcePolicy? resourcePolicy = null)
    {
        resourcePolicy ??= RuntimeResourcePolicies.Standard;
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
                string.IsNullOrWhiteSpace(informationalVersion) ? productVersion : informationalVersion),
            new RuntimeResourceProfileDescriptor(
                RuntimeResourcePolicyContract.GetEffectiveProfileHash(resourcePolicy),
                resourcePolicy.MaximumRpcValueBytes,
                resourcePolicy.MaximumValueDepth,
                resourcePolicy.MaximumValueNodes,
                resourcePolicy.MaximumObjectMembers,
                resourcePolicy.MaximumInlineDecodedBytes,
                resourcePolicy.MaximumContentReferenceBytes));
    }

    public RuntimeHandshakeResponse Handshake { get; }

    public Guid RuntimeInstanceId => Handshake.RuntimeInstanceId;
}
