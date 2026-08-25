using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class RuntimeEndpointErrors
{
    public static void ThrowFailure(RuntimeStackFailure failure)
    {
        var detail = string.IsNullOrWhiteSpace(failure.Detail)
            ? "The Runtime rejected the operation."
            : failure.Detail;
        throw failure.Category switch
        {
            RuntimeStackFailureCategory.Validation => new RuntimeValidationException(detail),
            RuntimeStackFailureCategory.PackageValidation => new RuntimePackageValidationException(detail),
            RuntimeStackFailureCategory.StaleGeneration => new RuntimeStaleGenerationException(detail),
            RuntimeStackFailureCategory.NotFound => new RuntimeNotFoundException(detail),
            RuntimeStackFailureCategory.Unavailable => new RuntimeUnavailableException(detail),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Category, "Unknown Runtime Stack failure category."),
        };
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
