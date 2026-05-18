using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Configuration;

namespace Sunder.Runtime.Host.Services;

internal static class PackageProtocolMapper
{
    public static ActivePackageDescriptor BuildDescriptor(
        RuntimePackageManifest manifest,
        bool isEnabled,
        PackageReadinessState readiness,
        IReadOnlyList<PackageViewRegistration>? packageViews = null)
    {
        var packageIcon = ToProtocolIcon(manifest.Icon, manifest.Name!);
        var views = new List<PackageViewDescriptor>();

        foreach (var view in packageViews ?? [])
        {
            views.Add(
                new PackageViewDescriptor(
                    view.Id,
                    manifest.Id!,
                    view.Name,
                    ToProtocolIcon(view.Icon, view.Name) ?? packageIcon,
                    ToProtocolPlacement(view.DefaultPlacement),
                    view.ShowInHotbarByDefault
                )
            );
        }

        return new ActivePackageDescriptor(
            manifest.Id!,
            manifest.Name!,
            manifest.Version!,
            packageIcon,
            isEnabled,
            readiness,
            views
        );
    }

    public static SessionPackageDescriptor BuildSessionDescriptor(
        RuntimePackageManifest manifest,
        bool isEnabled,
        PackageReadinessState readiness,
        PackageFailureOrigin? failureOrigin = null,
        string? lastError = null,
        int failureCount = 0)
    {
        var descriptor = BuildDescriptor(manifest, isEnabled, readiness);
        return new SessionPackageDescriptor(
            descriptor.PackageId,
            descriptor.DisplayName,
            descriptor.Version,
            descriptor.Icon,
            descriptor.IsEnabled,
            descriptor.Readiness,
            descriptor.Views,
            failureOrigin,
            lastError,
            lastError is null ? null : DateTimeOffset.UtcNow,
            failureCount);
    }

    public static SessionPackageDescriptor BuildSessionDescriptor(
        RuntimePackageManifest manifest,
        bool isEnabled,
        PackageReadinessState readiness,
        IReadOnlyList<PackageViewRegistration> packageViews,
        PackageFailureOrigin? failureOrigin = null,
        string? lastError = null,
        int failureCount = 0)
    {
        var descriptor = BuildDescriptor(manifest, isEnabled, readiness, packageViews);
        return new SessionPackageDescriptor(
            descriptor.PackageId,
            descriptor.DisplayName,
            descriptor.Version,
            descriptor.Icon,
            descriptor.IsEnabled,
            descriptor.Readiness,
            descriptor.Views,
            failureOrigin,
            lastError,
            lastError is null ? null : DateTimeOffset.UtcNow,
            failureCount);
    }

    public static PackageConfigurationSchemaDescriptor? ToProtocolConfigurationSchema(PackageConfigurationSchema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        return new PackageConfigurationSchemaDescriptor(
            schema.PackageId,
            schema.PackageDisplayName,
            schema.Summary,
            schema.Sections.Select(section => new PackageConfigurationSectionDescriptor(
                section.SectionId,
                section.Title,
                section.Description,
                section.Fields.Select(field => new PackageConfigurationFieldDescriptor(
                    field.Key,
                    field.Label,
                    field.Kind switch
                    {
                        Sunder.Sdk.Configuration.PackageConfigurationFieldKind.Text => Sunder.Protocol.PackageConfigurationFieldKind.Text,
                        Sunder.Sdk.Configuration.PackageConfigurationFieldKind.Secret => Sunder.Protocol.PackageConfigurationFieldKind.Secret,
                        Sunder.Sdk.Configuration.PackageConfigurationFieldKind.Boolean => Sunder.Protocol.PackageConfigurationFieldKind.Boolean,
                        Sunder.Sdk.Configuration.PackageConfigurationFieldKind.Select => Sunder.Protocol.PackageConfigurationFieldKind.Select,
                        _ => Sunder.Protocol.PackageConfigurationFieldKind.Text,
                    },
                    field.Description,
                    field.IsRequired,
                    field.Placeholder,
                    field.DefaultValue,
                    (field.Options ?? [])
                        .Select(option => new PackageConfigurationOptionDescriptor(option.Value, option.Label))
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
                Sunder.Sdk.Authentication.PackageAuthStatusKind.NotConnected => Sunder.Protocol.PackageAuthStatusKind.NotConnected,
                Sunder.Sdk.Authentication.PackageAuthStatusKind.Connected => Sunder.Protocol.PackageAuthStatusKind.Connected,
                Sunder.Sdk.Authentication.PackageAuthStatusKind.Failed => Sunder.Protocol.PackageAuthStatusKind.Failed,
                _ => Sunder.Protocol.PackageAuthStatusKind.Unavailable,
            },
            status.Message,
            status.CanAuthorize,
            status.CanDisconnect
        );
    }

    public static PackageAuthSessionStartResponse ToProtocolAuthSessionStart(PackageAuthSessionStatus status)
    {
        return new PackageAuthSessionStartResponse(
            status.PackageId,
            status.AuthSessionId,
            Sunder.Protocol.PackageAuthFlowKind.Browser,
            status.LaunchUrl ?? string.Empty,
            status.Message);
    }

    public static PackageAuthSessionStatusResponse ToProtocolAuthSessionStatus(PackageAuthSessionStatus status)
    {
        return new PackageAuthSessionStatusResponse(
            status.PackageId,
            status.AuthSessionId,
            status.State switch
            {
                Sunder.Sdk.Authentication.PackageAuthSessionState.Connected => Sunder.Protocol.PackageAuthSessionState.Connected,
                Sunder.Sdk.Authentication.PackageAuthSessionState.Failed => Sunder.Protocol.PackageAuthSessionState.Failed,
                Sunder.Sdk.Authentication.PackageAuthSessionState.Cancelled => Sunder.Protocol.PackageAuthSessionState.Cancelled,
                _ => Sunder.Protocol.PackageAuthSessionState.Pending,
            },
            status.Message,
            status.LaunchUrl);
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
