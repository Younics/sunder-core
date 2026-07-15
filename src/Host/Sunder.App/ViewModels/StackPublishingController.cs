using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class StackPublishingController(
    LocalStackLibraryService library,
    IRuntimeStacksClient runtimeClient)
{
    public async Task<StackPublishingResult> PublishAsync(
        LocalStackLibraryItemViewModel stack,
        Uri registryUrl,
        bool preserveOriginalPublishTime,
        CancellationToken cancellationToken)
    {
        var upload = await runtimeClient.UploadStackAsync(stack.LocalPath, cancellationToken);
        var result = await runtimeClient.PublishRegistryStackAsync(
            new RuntimeRegistryPublishRequest(registryUrl.AbsoluteUri, upload.UploadId),
            cancellationToken);
        if (!result.Success)
        {
            return StackPublishingResult.Failed(result.Errors.FirstOrDefault() ?? "Registry Stack publish failed.");
        }

        var now = DateTimeOffset.UtcNow;
        var publishedStackId = string.IsNullOrWhiteSpace(result.StackId)
            ? stack.PublishedStackId ?? stack.StackId
            : result.StackId!;
        await library.UpdatePublishStateAsync(
            stack.StackId,
            registryUrl.ToString(),
            publishedStackId,
            preserveOriginalPublishTime ? stack.Item.PublishedAtUtc ?? now : now,
            now,
            cancellationToken);
        return StackPublishingResult.Succeeded(result.Message ?? $"Published Stack '{publishedStackId}'.");
    }

    public async Task<StackPublishingResult> UnpublishAsync(
        LocalStackLibraryItemViewModel stack,
        Uri registryUrl,
        CancellationToken cancellationToken)
    {
        var publishedStackId = stack.PublishedStackId;
        if (string.IsNullOrWhiteSpace(publishedStackId))
        {
            return StackPublishingResult.Failed("The selected Stack is not published.");
        }

        var result = await runtimeClient.DeleteRegistryStackAsync(
            new RuntimeRegistryDeleteStackRequest(registryUrl.AbsoluteUri, publishedStackId),
            cancellationToken);
        if (!result.Success)
        {
            return StackPublishingResult.Failed(result.Forbidden
                ? "Registry Stack unpublish requires sign-in as the Stack owner."
                : result.Errors.FirstOrDefault() ?? "Registry Stack unpublish failed.");
        }

        await library.ClearPublishStateAsync(stack.StackId, cancellationToken);
        return StackPublishingResult.Succeeded(result.Message ?? $"Unpublished Stack '{publishedStackId}'.");
    }

    public async Task<StackPublishingResult> ToggleStarAsync(
        Uri registryUrl,
        string stackId,
        bool isCurrentlyStarred,
        CancellationToken cancellationToken)
    {
        var result = await runtimeClient.SetRegistryStackStarAsync(
            new RuntimeRegistryStarRequest(registryUrl.AbsoluteUri, stackId, !isCurrentlyStarred),
            cancellationToken);
        if (!result.Success)
        {
            return StackPublishingResult.Failed(result.Forbidden
                ? "Sign in to the Registry before starring a Stack."
                : result.Errors.FirstOrDefault() ?? "Registry Stack star update failed.");
        }

        return StackPublishingResult.Succeeded(result.Message ?? "Updated Stack star.", result.Stats);
    }
}

internal sealed record StackPublishingResult(bool Success, string Message, RegistryStackStats? Stats = null)
{
    public static StackPublishingResult Succeeded(string message, RegistryStackStats? stats = null)
        => new(true, message, stats);

    public static StackPublishingResult Failed(string message) => new(false, message);
}
