using System.Text.Json;

namespace Sunder.Cli;

internal sealed class CliAuthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Dictionary<string, CliAuthToken> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CliAuthStore Load()
    {
        var path = GetStorePath();
        if (!File.Exists(path))
        {
            return new CliAuthStore();
        }

        try
        {
            return JsonSerializer.Deserialize<CliAuthStore>(File.ReadAllText(path), JsonOptions) ?? new CliAuthStore();
        }
        catch
        {
            return new CliAuthStore();
        }
    }

    public CliAuthToken? GetToken(Uri registryUrl)
    {
        var key = NormalizeRegistryUrl(registryUrl);
        return Tokens.TryGetValue(key, out var token) ? token : null;
    }

    public void SetToken(Uri registryUrl, string token, string? userId, DateTimeOffset? expiresAtUtc)
    {
        var key = NormalizeRegistryUrl(registryUrl);
        Tokens[key] = new CliAuthToken(key, token, userId, expiresAtUtc);
    }

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
    }

    public static string GetStorePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sunder", "cli-auth.json");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Sunder",
                "cli-auth.json");
        }

        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configRoot, "sunder", "cli-auth.json");
    }

    private static string NormalizeRegistryUrl(Uri registryUrl)
    {
        var builder = new UriBuilder(registryUrl)
        {
            Fragment = string.Empty,
            Query = string.Empty,
        };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri.ToString();
    }
}

internal sealed record CliAuthToken(
    string RegistryUrl,
    string Token,
    string? UserId,
    DateTimeOffset? ExpiresAtUtc);
