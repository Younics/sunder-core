namespace Sunder.Sdk.Compatibility
{
    [AttributeUsage(
        AttributeTargets.Class
        | AttributeTargets.Interface
        | AttributeTargets.Struct
        | AttributeTargets.Enum
        | AttributeTargets.Delegate
        | AttributeTargets.Method
        | AttributeTargets.Property
        | AttributeTargets.Field
        | AttributeTargets.Event,
        AllowMultiple = true,
        Inherited = false)]
    public sealed class SunderSdkCapabilityAttribute(string capability) : Attribute
    {
        public string Capability { get; } = capability;
    }
}

namespace Sunder.Sdk.Abstractions
{
    using Sunder.Sdk.Compatibility;

    [SunderSdkCapability("core.v1")]
    public interface ISunderAppPackageModule;

    [SunderSdkCapability("storage.v1")]
    [SunderSdkCapability("storage.key-migration.v1")]
    public interface IPackageStorageKeyMigrator;

    [SunderSdkCapability("shell-view.v1")]
    [SunderSdkCapability("view-navigation-preparation.v1")]
    public interface IPackageViewNavigationPreparationTarget;

    public interface IPackageSettings;

    public interface IPackageContext
    {
        IPackageSettings Settings { get; }

        object UnresolvedMember { get; }
    }

    public interface UnresolvedSdkContract;
}

namespace Sunder.Sdk.Storage
{
    using Sunder.Sdk.Compatibility;

    [SunderSdkCapability("storage.v1")]
    [SunderSdkCapability("storage.key-migration.v1")]
    public sealed class PackageStorageKeyMigration;
}
