namespace Sunder.App.Models;

public sealed class AppStartupOptions
{
    public static Uri DefaultRuntimeUrl { get; } = new("http://127.0.0.1:5275/");

    public Uri RuntimeUrl { get; init; } = DefaultRuntimeUrl;

    public bool HasExplicitRuntimeUrl { get; init; }

    public string? RuntimeHostPath { get; init; }

    public IReadOnlyList<string> DevPackageFolders { get; init; } = [];

    public bool WatchDevPackages { get; init; }

    public IReadOnlyList<string> ParseErrors { get; init; } = [];
}
