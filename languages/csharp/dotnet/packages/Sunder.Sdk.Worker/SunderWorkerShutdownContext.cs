using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Worker;

/// <summary>Contains the Host-provided reason for stopping a worker activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderWorkerShutdownContext
{
    internal SunderWorkerShutdownContext(string reason) => Reason = reason;

    /// <summary>Gets the bounded Host-provided shutdown reason.</summary>
    public string Reason { get; }
}
