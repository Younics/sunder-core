using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>The exact development tools selected by the running Sunder release.</summary>
[SunderSdkCapability(SunderSdkCapabilities.DevelopmentToolchainV1)]
public sealed record SunderDevelopmentToolchain(
    string DotnetSdkVersion,
    string NodeVersion,
    string NpmVersion);
