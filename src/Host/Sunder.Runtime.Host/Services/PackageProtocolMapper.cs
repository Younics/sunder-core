using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Settings;

namespace Sunder.Runtime.Host.Services;

internal static class PackageProtocolMapper
{
    public static ActivePackageDescriptor BuildDescriptor(
        RuntimePackageActivationState activation,
        bool isEnabled,
        PackageReadinessState readiness,
        IReadOnlyList<PackageViewRegistration>? packageViews = null)
    {
        var packageIcon = ToProtocolIcon(activation.Icon, activation.Name);
        var views = new List<PackageViewDescriptor>();

        foreach (var view in packageViews ?? [])
        {
            views.Add(
                new PackageViewDescriptor(
                    view.Id,
                    activation.PackageId,
                    view.Name,
                    ToProtocolIcon(view.IconAssetPath, view.Name) ?? packageIcon,
                    ToProtocolPlacement(view.DefaultPlacement),
                    view.ShowInHotbarByDefault
                )
            );
        }

        return new ActivePackageDescriptor(
            activation.PackageId,
            activation.Name,
            activation.Version,
            activation.HostRoles,
            packageIcon,
            isEnabled,
            readiness,
            views
        );
    }

    public static SessionPackageDescriptor BuildSessionDescriptor(
        RuntimePackageActivationState activation,
        bool isEnabled,
        PackageReadinessState readiness,
        PackageFailureOrigin? failureOrigin = null,
        string? lastError = null,
        int failureCount = 0)
    {
        var descriptor = BuildDescriptor(activation, isEnabled, readiness);
        return new SessionPackageDescriptor(
            descriptor.PackageId,
            descriptor.DisplayName,
            descriptor.Version,
            descriptor.HostRoles,
            descriptor.Icon,
            descriptor.IsEnabled,
            descriptor.Readiness,
            descriptor.Views,
            failureOrigin,
            lastError,
            lastError is null ? null : DateTimeOffset.UtcNow,
            failureCount,
            ToProtocolRpcContractUses(activation.RpcContractUses));
    }

    public static SessionPackageDescriptor BuildSessionDescriptor(
        RuntimePackageActivationState activation,
        bool isEnabled,
        PackageReadinessState readiness,
        IReadOnlyList<PackageViewRegistration> packageViews,
        PackageFailureOrigin? failureOrigin = null,
        string? lastError = null,
        int failureCount = 0)
    {
        var descriptor = BuildDescriptor(activation, isEnabled, readiness, packageViews);
        return new SessionPackageDescriptor(
            descriptor.PackageId,
            descriptor.DisplayName,
            descriptor.Version,
            descriptor.HostRoles,
            descriptor.Icon,
            descriptor.IsEnabled,
            descriptor.Readiness,
            descriptor.Views,
            failureOrigin,
            lastError,
            lastError is null ? null : DateTimeOffset.UtcNow,
            failureCount,
            ToProtocolRpcContractUses(activation.RpcContractUses));
    }

    private static IReadOnlyList<PackageRpcContractUseDescriptor> ToProtocolRpcContractUses(
        IReadOnlyList<Sunder.Package.Format.SunderPackageContractUseManifest?>? uses)
        => (uses ?? [])
            .Where(static use => use is not null)
            .Select(static use => new PackageRpcContractUseDescriptor(
                use!.ContractId!,
                use.VersionRange!,
                use.Required!.Value,
                (use.Actions ?? [])
                    .Where(static action => action is not null)
                    .Select(static action => action!)
                    .ToArray()))
            .ToArray();

    public static PackageSettingsSchemaDescriptor? ToProtocolSettingsSchema(
        string ownerPackageId,
        string ownerPackageDisplayName,
        PackageSettingsSchema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        return new PackageSettingsSchemaDescriptor(
            ownerPackageId,
            ownerPackageDisplayName,
            schema.Summary,
            schema.Sections.Select(section => new PackageSettingsSectionDescriptor(
                section.SectionId,
                section.Title,
                section.Description,
                section.Fields.Select(field => new PackageSettingsFieldDescriptor(
                    field.Key,
                    field.Label,
                    field.Kind switch
                    {
                        Sunder.Sdk.Settings.PackageSettingsFieldKind.Text => Sunder.Runtime.Contracts.PackageSettingsFieldKind.Text,
                        Sunder.Sdk.Settings.PackageSettingsFieldKind.Secret => Sunder.Runtime.Contracts.PackageSettingsFieldKind.Secret,
                        Sunder.Sdk.Settings.PackageSettingsFieldKind.Boolean => Sunder.Runtime.Contracts.PackageSettingsFieldKind.Boolean,
                        Sunder.Sdk.Settings.PackageSettingsFieldKind.Select => Sunder.Runtime.Contracts.PackageSettingsFieldKind.Select,
                        _ => throw new InvalidOperationException($"Unsupported settings field kind '{field.Kind}'."),
                    },
                    field.Description,
                    field.IsRequired,
                    field.Placeholder,
                    field.DefaultValue,
                    field.Options
                        .Select(option => new PackageSettingsOptionDescriptor(option.Value, option.Label))
                        .ToArray()
                )).ToArray()
            )).ToArray()
        );
    }

    public static PackageAuthStatusResponse ToProtocolAuthStatus(PackageAuthStatus status)
    {
        return new PackageAuthStatusResponse(
            status.PackageId,
            status.Status switch
            {
                Sunder.Sdk.Authentication.PackageAuthStatusKind.NotConnected => Sunder.Runtime.Contracts.PackageAuthStatusKind.NotConnected,
                Sunder.Sdk.Authentication.PackageAuthStatusKind.Connected => Sunder.Runtime.Contracts.PackageAuthStatusKind.Connected,
                Sunder.Sdk.Authentication.PackageAuthStatusKind.Failed => Sunder.Runtime.Contracts.PackageAuthStatusKind.Failed,
                _ => Sunder.Runtime.Contracts.PackageAuthStatusKind.Unavailable,
            },
            status.Message,
            status.CanAuthorize,
            status.CanDisconnect
        );
    }

    private static PackageIconDescriptor? ToProtocolIcon(string? assetPath, string fallbackName)
    {
        var normalizedAssetPath = string.IsNullOrWhiteSpace(assetPath) ? null : assetPath;
        var glyph = ResolveGlyphFallback(fallbackName);
        if (normalizedAssetPath is null && glyph is null)
        {
            return null;
        }

        return new PackageIconDescriptor(glyph, normalizedAssetPath);
    }

    private static string? ResolveGlyphFallback(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim()[0].ToString().ToUpperInvariant();

    private static string ToProtocolPlacement(PackageViewPlacement placement)
        => placement switch
        {
            PackageViewPlacement.LeftTop => "leftTop",
            PackageViewPlacement.Middle => "middle",
            PackageViewPlacement.RightTop => "rightTop",
            PackageViewPlacement.LeftBottom => "leftBottom",
            PackageViewPlacement.RightBottom => "rightBottom",
            _ => "middle",
        };
}
