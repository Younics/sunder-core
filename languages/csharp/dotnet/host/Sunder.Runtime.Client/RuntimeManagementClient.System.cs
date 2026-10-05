using System.Net.Http.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token = default)
        => GetRequiredAsync<SystemStatusResponse>("system", token);

    public Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token = default)
        => PostResetControlAsync<object, RuntimeResetChallengeResponse>("system/reset/prepare", new { }, token);

    public Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token = default)
        => DrainForResetAsync(challenge, requireBoundStateRoot: false, token);

    public Task<RuntimeResetDrainResponse> DrainForResetAsync(
        string challenge,
        bool requireBoundStateRoot,
        CancellationToken token = default)
        => PostResetControlAsync<RuntimeResetConfirmRequest, RuntimeResetDrainResponse>(
            "system/reset/drain",
            new RuntimeResetConfirmRequest(challenge)
            {
                RequireBoundStateRoot = requireBoundStateRoot,
            },
            token);

    public Task<RuntimeV1ResetResult> DrainAndResetLocalStateAsync(
        RuntimeResetChallengeResponse challenge,
        TimeSpan leaseWait,
        CancellationToken token = default)
        => DrainAndResetAsync(challenge, leaseWait, startupCacheOnly: false, token);

    public Task<RuntimeV1ResetResult> DrainAndResetStartupCacheAsync(
        RuntimeResetChallengeResponse challenge,
        TimeSpan leaseWait,
        CancellationToken token = default)
        => DrainAndResetAsync(challenge, leaseWait, startupCacheOnly: true, token);

    private async Task<RuntimeV1ResetResult> DrainAndResetAsync(
        RuntimeResetChallengeResponse challenge,
        TimeSpan leaseWait,
        bool startupCacheOnly,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (!challenge.SupportsBoundStateRoot)
        {
            throw new InvalidOperationException(
                "The local Host does not support binding reset deletion to its Runtime state root.");
        }
        if (!challenge.SupportsHostExecutedReset)
        {
            throw new InvalidOperationException(
                "The local Host does not support reset execution while retaining lifecycle ownership.");
        }
        var connection = _transport.GetConnectionInfo()
            ?? throw new InvalidOperationException("Authenticated Runtime connection information is not available.");
        if (!connection.RuntimeUrl.IsLoopback
            && connection.RuntimeUrl != RuntimeIpcEndpoint.LogicalRuntimeUrl)
        {
            throw new InvalidOperationException("Runtime reset requires an authenticated loopback Host connection.");
        }

        var drained = await PostResetControlAsync<RuntimeResetConfirmRequest, RuntimeResetDrainResponse>(
                "system/reset/drain",
                new RuntimeResetConfirmRequest(challenge.Challenge)
                {
                    RequireBoundStateRoot = true,
                    RequireHostExecutedReset = true,
                    StartupCacheOnly = startupCacheOnly,
                },
                token)
            .ConfigureAwait(false);
        var expectedCategories = RuntimeV1StateDescriptor.ResetCategories
            .Select(static category => category.Id)
            .ToArray();
        var expectedResultCategories = startupCacheOnly
            ? [RuntimeV1StateDescriptor.StartupCacheResetCategory.Id]
            : expectedCategories;
        if (!drained.Categories.SequenceEqual(expectedCategories, StringComparer.Ordinal)
            || !drained.HostExecutedReset
            || drained.CategoryResults is null
            || !drained.CategoryResults.Select(static category => category.Category)
                .SequenceEqual(expectedResultCategories, StringComparer.Ordinal)
            || drained.CategoryResults.Any(static category =>
                category.Status is not ("reset" or "already-empty" or "partial"))
            || string.IsNullOrWhiteSpace(drained.ReceiptRelativePath)
            || string.IsNullOrWhiteSpace(drained.RuntimeStateRootPath)
            || string.IsNullOrWhiteSpace(drained.RuntimeStateRootIdentity)
            || !Path.IsPathFullyQualified(drained.RuntimeStateRootPath))
        {
            throw new InvalidDataException(
                "The local Host returned an invalid bound Runtime reset target.");
        }
        var root = Path.GetFullPath(drained.RuntimeStateRootPath);
        if (!IsContainedRelativePath(root, drained.ReceiptRelativePath)
            || drained.CategoryResults.Any(category =>
                category.RelativePaths?.Any(path => !IsContainedRelativePath(root, path)) == true))
        {
            throw new InvalidDataException(
                "The local Host returned invalid Runtime reset evidence paths.");
        }
        RuntimeOwnedFileSystem.ValidateMutableDirectoryIdentity(
            root,
            drained.RuntimeStateRootIdentity);
        RuntimeOwnedFileSystem.ValidateMutableRegularFile(
            root,
            Path.GetFullPath(Path.Combine(root, drained.ReceiptRelativePath)));

        _ = leaseWait;
        return new RuntimeV1ResetResult(
            drained.CategoryResults.Select(static category => new RuntimeV1ResetCategoryResult(
                category.Category,
                category.Status,
                category.RelativePaths)).ToArray(),
            drained.ReceiptRelativePath);
    }

    private static bool IsContainedRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            return false;
        }

        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.StartsWith(rootPrefix, comparison);
    }

    private async Task<TResponse> PostResetControlAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateUri(path))
        {
            Content = JsonContent.Create(body),
        };
        request.Options.Set(RuntimeAuthenticatedHttpMessageHandler.SkipProtocolNegotiationKey, true);
        using var response = await _transport.SendAsync(request, cancellationToken: token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<TResponse>(response, token).ConfigureAwait(false);
    }
}
