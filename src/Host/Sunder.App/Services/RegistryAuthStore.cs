using System.Text.Json;

namespace Sunder.App.Services;

public sealed class RegistryAuthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Dictionary<string, RegistryAuthToken> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static RegistryAuthStore Load()
    {
        foreach (var path in EnumerateStorePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return JsonSerializer.Deserialize<RegistryAuthStore>(File.ReadAllText(path), JsonOptions) ?? new RegistryAuthStore();
            }
            catch
            {
                // Try the next store path so a corrupt preferred file does not hide a valid legacy CLI token.
            }
        }

        return new RegistryAuthStore();
    }

    public RegistryAuthToken? GetToken(Uri registryUrl)
        => Tokens.TryGetValue(NormalizeRegistryUrl(registryUrl), out var token) ? token : null;

    public void SetToken(Uri registryUrl, string token, string? userId, DateTimeOffset? expiresAtUtc)
    {
        var key = NormalizeRegistryUrl(registryUrl);
        Tokens[key] = new RegistryAuthToken(key, token, userId, expiresAtUtc);
    }

    public bool RemoveToken(Uri registryUrl)
        => Tokens.Remove(NormalizeRegistryUrl(registryUrl));

    public void Save()
    {
        foreach (var path in EnumerateStorePaths())
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
    }

    public static string GetPreferredStorePath()
        => Path.Combine(GetSunderConfigRoot(), "registry-auth.json");

    public static string GetLegacyCliStorePath()
        => Path.Combine(GetSunderConfigRoot(), "cli-auth.json");

    private static IEnumerable<string> EnumerateStorePaths()
    {
        yield return GetPreferredStorePath();
        yield return GetLegacyCliStorePath();
    }

    private static string GetSunderConfigRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sunder");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Sunder");
        }

        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configRoot, "sunder");
    }

    private static string NormalizeRegistryUrl(Uri registryUrl)
        => RegistryUrlHelper.Normalize(registryUrl).ToString();
}

public sealed record RegistryAuthToken(
    string RegistryUrl,
    string Token,
    string? UserId,
    DateTimeOffset? ExpiresAtUtc);
