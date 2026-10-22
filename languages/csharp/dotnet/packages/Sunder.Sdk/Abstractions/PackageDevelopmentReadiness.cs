using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>The observed connection/bootstrap state of the development Host, independent of package readiness.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public enum PackageDevelopmentHostState
{
    /// <summary>No authoritative Host state has been observed.</summary>
    Unknown = 0,
    /// <summary>The development owner and snapshot do not yet identify the same current Host.</summary>
    Reconnecting = 1,
    /// <summary>The current Host is starting.</summary>
    Starting = 2,
    /// <summary>The current Host has completed bootstrap; packages may still be unavailable.</summary>
    Ready = 3,
    /// <summary>The current Host failed bootstrap.</summary>
    Failed = 4,
    /// <summary>The current Host is shutting down.</summary>
    ShuttingDown = 5,
}

/// <summary>The observed package Runtime readiness, not App readiness or evidence that output was applied.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public enum PackageDevelopmentRuntimeReadiness
{
    /// <summary>No recognized authoritative package readiness is available.</summary>
    Unknown = 0,
    /// <summary>A ready Host has not observed the package as loaded.</summary>
    NotLoaded = 1,
    /// <summary>The package Runtime is ready.</summary>
    Ready = 2,
    /// <summary>The package needs configuration.</summary>
    NeedsConfiguration = 3,
    /// <summary>The package is available with degraded readiness.</summary>
    Degraded = 4,
    /// <summary>The package Runtime failed.</summary>
    Failed = 5,
    /// <summary>The package is disabled.</summary>
    Disabled = 6,
}

/// <summary>The observed package App activation state; activation does not assert that every lazy view was opened.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public enum PackageDevelopmentAppReadiness
{
    /// <summary>No recognized authoritative App state is available.</summary>
    Unknown = 0,
    /// <summary>The applicable App activation has not yet been published.</summary>
    Pending = 1,
    /// <summary>The applicable App activation has been published.</summary>
    Activated = 2,
    /// <summary>The package App activation is quarantined.</summary>
    Quarantined = 3,
    /// <summary>The App activation cannot currently be observed.</summary>
    Unavailable = 4,
    /// <summary>The observed package has no applicable App role.</summary>
    NotApplicable = 5,
}
