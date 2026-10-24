using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>An App-activation-owned service which starts independently of opening a package view.</summary>
/// <remarks>Start must return promptly. Own and drain asynchronous work through IAsyncDisposable; the supplied token stops admission immediately when this exact App activation is revoked. Constructors and candidate contribution registration must remain effect-free.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.AppActivationServicesV1)]
public interface IPackageAppActivationService
{
    /// <summary>Starts owned work only after this App activation is published.</summary>
    void Start(CancellationToken activationCancellationToken);
}
