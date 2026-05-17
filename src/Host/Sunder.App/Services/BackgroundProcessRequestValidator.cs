using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class BackgroundProcessRequestValidator
{
    public static void Validate(BackgroundProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Background process title is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.GroupKey))
        {
            throw new ArgumentException("Background process group key is required.", nameof(request));
        }

        if (request.ExecuteAsync is null)
        {
            throw new ArgumentException("Background process execute delegate is required.", nameof(request));
        }
    }
}
