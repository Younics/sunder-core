using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeEndpointErrorsTests
{
    [Theory]
    [InlineData(nameof(RuntimeStackFailureCategory.Validation), null, typeof(RuntimeValidationException), RuntimeErrorCodes.Validation, 400)]
    [InlineData(nameof(RuntimeStackFailureCategory.PackageValidation), "  ", typeof(RuntimePackageValidationException), RuntimeErrorCodes.PackageValidation, 422)]
    [InlineData(nameof(RuntimeStackFailureCategory.Validation), "stale, not found, and unavailable", typeof(RuntimeValidationException), RuntimeErrorCodes.Validation, 400)]
    [InlineData(nameof(RuntimeStackFailureCategory.PackageValidation), "archive entry was not found", typeof(RuntimePackageValidationException), RuntimeErrorCodes.PackageValidation, 422)]
    [InlineData(nameof(RuntimeStackFailureCategory.StaleGeneration), "generation changed", typeof(RuntimeStaleGenerationException), RuntimeErrorCodes.StaleGeneration, 409)]
    [InlineData(nameof(RuntimeStackFailureCategory.NotFound), "archive entry is absent", typeof(RuntimeNotFoundException), RuntimeErrorCodes.NotFound, 404)]
    [InlineData(nameof(RuntimeStackFailureCategory.Unavailable), "provider is offline", typeof(RuntimeUnavailableException), RuntimeErrorCodes.Unavailable, 503)]
    public void ThrowFailure_MapsTypedCategoryIndependentlyOfDiagnostic(
        string categoryName,
        string? message,
        Type expectedType,
        string expectedCode,
        int expectedStatusCode)
    {
        var category = Enum.Parse<RuntimeStackFailureCategory>(categoryName);
        var exception = Assert.IsAssignableFrom<RuntimeException>(
            Record.Exception(() => RuntimeEndpointErrors.ThrowFailure(new RuntimeStackFailure(category, message))));

        Assert.Equal(expectedType, exception.GetType());
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(expectedStatusCode, exception.StatusCode);
        Assert.Equal(
            string.IsNullOrWhiteSpace(message) ? "The Runtime rejected the operation." : message,
            exception.Message);
    }
}
