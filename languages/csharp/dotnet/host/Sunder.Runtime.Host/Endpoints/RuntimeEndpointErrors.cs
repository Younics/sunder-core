namespace Sunder.Runtime.Host.Endpoints;

internal static class RuntimeEndpointErrors
{
    public static void ThrowFailure(string? message, bool packageValidation = false)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? "The Runtime rejected the operation." : message;
        if (detail.Contains("stale", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeStaleGenerationException(detail);
        }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeNotFoundException(detail);
        }

        if (detail.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeUnavailableException(detail);
        }

        if (packageValidation)
        {
            throw new RuntimePackageValidationException(detail);
        }

        throw new RuntimeValidationException(detail);
    }

    public static T Required<T>(T? value, string resource) where T : class
        => value ?? throw new RuntimeNotFoundException($"{resource} was not found.");

    public static void Require(bool condition, string detail)
    {
        if (!condition)
        {
            throw new RuntimeValidationException(detail);
        }
    }
}
