using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Logging;

/// <summary>Specifies structured event severity and retention priority.</summary>
[SunderSdkCapability(SunderSdkCapabilities.LoggingV1)]
public enum PackageLogLevel
{
    /// <summary>Highly detailed diagnostic activity.</summary>
    Trace = 0,
    /// <summary>Developer-oriented diagnostic state.</summary>
    Debug = 1,
    /// <summary>Normal lifecycle or operation information.</summary>
    Information = 2,
    /// <summary>A recoverable problem requiring attention.</summary>
    Warning = 3,
    /// <summary>An operation failure.</summary>
    Error = 4,
    /// <summary>A failure that prevents safe package operation.</summary>
    Critical = 5,
}
