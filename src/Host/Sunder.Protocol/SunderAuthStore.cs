using System.Text.Json;

namespace Sunder.Protocol;

public sealed class SunderAuthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Dictionary<string, RegistryAuthToken> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SunderAuthStore Load()
    {
        var path = GetStorePath();
        if (File.Exists(path))
        {
            var store = ReadStore(path) ?? new SunderAuthStore();
            DeleteLegacyStores();
            return store;
        }

        var migrated = new SunderAuthStore();
        foreach (var legacyPath in EnumerateLegacyStorePaths())
        {
            var legacy = ReadStore(legacyPath);
            if (legacy is null)
            {
                continue;
            }

            foreach (var (registryUrl, token) in legacy.Tokens)
            {
                migrated.Tokens[registryUrl] = SelectToken(migrated.Tokens.GetValueOrDefault(registryUrl), token);
            }
        }

        if (migrated.Tokens.Count > 0)
        {
            try
            {
                migrated.Save();
            }
            catch
            {
                // Return migrated in-memory tokens even if the one-time write fails.
            }
        }

        return migrated;
    }

    public RegistryAuthToken? GetToken(Uri registryUrl)
        => Tokens.TryGetValue(NormalizeRegistryUrl(registryUrl), out var token) ? token : null;

    public void SetToken(
        Uri registryUrl,
        string token,
        string? userId,
        DateTimeOffset? expiresAtUtc,
        string? username = null,
        string? displayName = null,
        string? email = null,
        string? avatarUrl = null,
        DateTimeOffset? lastSyncedAtUtc = null)
    {
        var key = NormalizeRegistryUrl(registryUrl);
        Tokens[key] = new RegistryAuthToken(
            key,
            token,
            Normalize(userId),
            expiresAtUtc,
            Normalize(username),
            Normalize(displayName),
            Normalize(email),
            Normalize(avatarUrl),
            lastSyncedAtUtc);
    }

    public void SetToken(Uri registryUrl, string token, string? userId, DateTimeOffset? expiresAtUtc)
        => SetToken(registryUrl, token, userId, expiresAtUtc, null, null, null, null, null);

    public bool RemoveToken(Uri registryUrl)
        => Tokens.Remove(NormalizeRegistryUrl(registryUrl));

    public void Save()
    {
        var path = GetStorePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        DeleteLegacyStores();
    }

    public static string GetStorePath()
        => Path.Combine(GetSunderConfigRoot(), "auth.json");

    public static string GetLegacyRegistryStorePath()
        => Path.Combine(GetSunderConfigRoot(), "registry-auth.json");

    public static string GetLegacyCliStorePath()
        => Path.Combine(GetSunderConfigRoot(), "cli-auth.json");

    private static SunderAuthStore? ReadStore(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SunderAuthStore>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateLegacyStorePaths()
    {
        yield return GetLegacyRegistryStorePath();
        yield return GetLegacyCliStorePath();
    }

    private static void DeleteLegacyStores()
    {
        foreach (var legacyPath in EnumerateLegacyStorePaths())
        {
            try
            {
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                }
            }
            catch
            {
                // Migration cleanup should never prevent auth from being saved.
            }
        }
    }

    private static RegistryAuthToken SelectToken(RegistryAuthToken? existing, RegistryAuthToken candidate)
    {
        if (existing is null)
        {
            return candidate;
        }

        if (ProfileScore(candidate) > ProfileScore(existing))
        {
            return candidate;
        }

        if (candidate.ExpiresAtUtc is not null
            && (existing.ExpiresAtUtc is null || candidate.ExpiresAtUtc > existing.ExpiresAtUtc))
        {
            return candidate;
        }

        return existing;
    }

    private static int ProfileScore(RegistryAuthToken token)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(token.Username)) score++;
        if (!string.IsNullOrWhiteSpace(token.DisplayName)) score++;
        if (!string.IsNullOrWhiteSpace(token.Email)) score++;
        if (!string.IsNullOrWhiteSpace(token.AvatarUrl)) score++;
        return score;
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
    {
        var builder = new UriBuilder(registryUrl)
        {
            Fragment = string.Empty,
            Query = string.Empty,
        };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri.ToString();
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record RegistryAuthToken(
    string RegistryUrl,
    string Token,
    string? UserId,
    DateTimeOffset? ExpiresAtUtc,
    string? Username = null,
    string? DisplayName = null,
    string? Email = null,
    string? AvatarUrl = null,
    DateTimeOffset? LastSyncedAtUtc = null);
