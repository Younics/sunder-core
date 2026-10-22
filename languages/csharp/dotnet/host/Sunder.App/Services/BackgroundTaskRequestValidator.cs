using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class BackgroundTaskRequestValidator
{
    public static void Validate(BackgroundTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Background task title is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.GroupKey))
        {
            throw new ArgumentException("Background task group key is required.", nameof(request));
        }

        if (request.ExecuteAsync is null)
        {
            throw new ArgumentException("Background task execute delegate is required.", nameof(request));
        }

        if (!Enum.IsDefined(request.Indicator))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Indicator, "Unknown background task indicator.");
        }

        if (!Enum.IsDefined(request.ConcurrencyMode))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.ConcurrencyMode, "Unknown background task concurrency mode.");
        }
    }
}
