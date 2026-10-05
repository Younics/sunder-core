using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Settings;

namespace Sunder.Sdk.Worker;

/// <summary>Supplies the settings schema for generated single-project Worker composition.</summary>
/// <remarks>
/// A package may declare one concrete source implementation. The schema is read
/// during composition, before activation. Its construction must be deterministic
/// and must not perform Host operations or mutate package data.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.SettingsSchemaV1)]
public interface ISunderWorkerSettingsSchemaProvider
{
    /// <summary>Gets the immutable settings declaration advertised by this package.</summary>
    PackageSettingsSchema SettingsSchema { get; }
}
