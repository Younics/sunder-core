using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Worker;

/// <summary>Identifies the exact Runtime generation committed to a V1 worker.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RuntimeGenerationsV1)]
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV1)]
public sealed class SunderWorkerGenerationContext
{
    internal SunderWorkerGenerationContext(Guid activationId, long sessionGeneration)
    {
        ActivationId = activationId;
        SessionGeneration = sessionGeneration;
    }

    /// <summary>Gets the Host-assigned identity of the exact worker activation.</summary>
    public Guid ActivationId { get; }

    /// <summary>Gets the committed Runtime session generation.</summary>
    public long SessionGeneration { get; }
}
