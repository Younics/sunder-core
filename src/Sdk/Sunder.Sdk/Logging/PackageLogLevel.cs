using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Logging;

[SunderSdkCapability(SunderSdkCapabilities.LoggingV1)]
public enum PackageLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}
