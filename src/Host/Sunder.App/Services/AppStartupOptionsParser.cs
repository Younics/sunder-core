using Sunder.App.Models;

namespace Sunder.App.Services;

public static class AppStartupOptionsParser
{
    public static AppStartupOptions Parse(IReadOnlyList<string> args)
    {
        var parseErrors = new List<string>();
        var devPackageFolders = new List<string>();
        var watchDevPackages = false;
        var runtimeHostPath = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_HOST_PATH");
        var environmentRuntimeUrl = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_URL");
        var hasExplicitRuntimeUrl = !string.IsNullOrWhiteSpace(environmentRuntimeUrl);
        var runtimeUrl = TryParseRuntimeUrl(environmentRuntimeUrl, parseErrors)
            ?? RuntimeUrlHelper.Normalize(AppStartupOptions.DefaultRuntimeUrl);

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];

            if (TryReadOptionValue(args, ref index, argument, "--dev-package", out var devPackageFolder))
            {
                if (string.IsNullOrWhiteSpace(devPackageFolder))
                {
                    parseErrors.Add("--dev-package requires a folder path.");
                }
                else
                {
                    devPackageFolders.Add(Path.GetFullPath(devPackageFolder));
                }

                continue;
            }

            if (string.Equals(argument, "--watch", StringComparison.OrdinalIgnoreCase))
            {
                watchDevPackages = true;
                continue;
            }

            if (TryReadOptionValue(args, ref index, argument, "--runtime-url", out var runtimeUrlValue))
            {
                hasExplicitRuntimeUrl = true;
                var parsedRuntimeUrl = TryParseRuntimeUrl(runtimeUrlValue, parseErrors);
                if (parsedRuntimeUrl is not null)
                {
                    runtimeUrl = parsedRuntimeUrl;
                }

                continue;
            }

            if (TryReadOptionValue(args, ref index, argument, "--runtime-host-path", out var runtimeHostPathValue))
            {
                if (string.IsNullOrWhiteSpace(runtimeHostPathValue))
                {
                    parseErrors.Add("--runtime-host-path requires a file or directory path.");
                }
                else
                {
                    runtimeHostPath = Path.GetFullPath(runtimeHostPathValue);
                }

                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                parseErrors.Add($"Unrecognized startup argument '{argument}'. Did you mean --dev-package {argument}?");
            }
        }

        var normalizedDevPackageFolders = devPackageFolders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (watchDevPackages && normalizedDevPackageFolders.Length == 0)
        {
            parseErrors.Add("--watch requires at least one --dev-package folder.");
        }

        return new AppStartupOptions
        {
            RuntimeUrl = runtimeUrl,
            HasExplicitRuntimeUrl = hasExplicitRuntimeUrl,
            RuntimeHostPath = string.IsNullOrWhiteSpace(runtimeHostPath) ? null : Path.GetFullPath(runtimeHostPath),
            DevPackageFolders = normalizedDevPackageFolders,
            WatchDevPackages = watchDevPackages,
            ParseErrors = parseErrors,
        };
    }

    private static bool TryReadOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string argument,
        string optionName,
        out string? value)
    {
        if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase))
        {
            value = index + 1 < args.Count ? args[++index] : null;
            return true;
        }

        var prefix = optionName + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = argument[prefix.Length..];
            return true;
        }

        value = null;
        return false;
    }

    private static Uri? TryParseRuntimeUrl(string? value, ICollection<string> parseErrors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (RuntimeUrlHelper.TryParse(value, out var uri) && uri is not null)
        {
            return uri;
        }

        parseErrors.Add($"'{value}' is not a valid HTTP runtime URL.");
        return null;
    }
}
