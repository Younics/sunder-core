using System.Text.Json;

namespace Sunder.App.Services;

public sealed record SunderAppSettings(
    string? RegistryApiUrl,
    string? UpdateGitHubRepositoryUrl,
    bool? IncludePrereleaseUpdates)
{
    public static SunderAppSettings Load()
    {
        var result = new SunderAppSettings(RegistryApiUrl: null, UpdateGitHubRepositoryUrl: null, IncludePrereleaseUpdates: null);
        result = Merge(result, LoadFile("appsettings.json"));

        var environment = ResolveEnvironmentName();
        if (!string.IsNullOrWhiteSpace(environment))
        {
            result = Merge(result, LoadFile($"appsettings.{environment}.json"));
        }

        return result;
    }

    private static SunderAppSettings Merge(SunderAppSettings current, SunderAppSettings next)
        => new(
            next.RegistryApiUrl ?? current.RegistryApiUrl,
            next.UpdateGitHubRepositoryUrl ?? current.UpdateGitHubRepositoryUrl,
            next.IncludePrereleaseUpdates ?? current.IncludePrereleaseUpdates);

    private static SunderAppSettings LoadFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            return new SunderAppSettings(RegistryApiUrl: null, UpdateGitHubRepositoryUrl: null, IncludePrereleaseUpdates: null);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new SunderAppSettings(
            GetString(document.RootElement, "Registry", "ApiUrl"),
            GetString(document.RootElement, "Updates", "GitHubRepositoryUrl"),
            GetBool(document.RootElement, "Updates", "IncludePrerelease"));
    }

    private static string? GetString(JsonElement root, string sectionName, string propertyName)
    {
        if (!root.TryGetProperty(sectionName, out var section)
            || !section.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? GetBool(JsonElement root, string sectionName, string propertyName)
    {
        if (!root.TryGetProperty(sectionName, out var section)
            || !section.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return null;
        }

        return property.GetBoolean();
    }

    private static string? ResolveEnvironmentName()
        => Environment.GetEnvironmentVariable("SUNDER_ENVIRONMENT")
           ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
           ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
#if DEBUG
           ?? "Development"
#endif
        ;
}
