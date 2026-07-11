using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryAuthenticatedOperations
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RegistryCredentialStore _credentialStore;
    private readonly RuntimeContentTransferStore? _transferStore;
    private readonly PackageSessionLifecycleService? _packageSessions;

    public RegistryAuthenticatedOperations(
        IHttpClientFactory httpClientFactory,
        RegistryCredentialStore credentialStore,
        RuntimeContentTransferStore transferStore,
        PackageSessionLifecycleService packageSessions)
        : this(httpClientFactory, credentialStore)
    {
        _transferStore = transferStore;
        _packageSessions = packageSessions;
    }

    internal RegistryAuthenticatedOperations(IHttpClientFactory httpClientFactory, RegistryCredentialStore credentialStore)
    {
        _httpClientFactory = httpClientFactory;
        _credentialStore = credentialStore;
    }

    public Task<RegistryPackageStarResponse> SetPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            request.Starred ? HttpMethod.Put : HttpMethod.Delete,
            $"api/v1/packages/{Uri.EscapeDataString(request.ResourceId)}/star",
            null,
            response => ReadAsync(response, new RegistryPackageStarResponse(false, null, null, [Error(response)]), cancellationToken),
            () => new RegistryPackageStarResponse(false, null, null, ["Registry sign-in is required."]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryStackStarResponse> SetStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            request.Starred ? HttpMethod.Put : HttpMethod.Delete,
            $"api/v1/stacks/{Uri.EscapeDataString(request.ResourceId)}/star",
            null,
            response => ReadAsync(response, new RegistryStackStarResponse(false, null, null, [Error(response)]), cancellationToken),
            () => new RegistryStackStarResponse(false, null, null, ["Registry sign-in is required."]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryStackManagementOperationResponse> DeleteStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Delete,
            $"api/v1/stacks/{Uri.EscapeDataString(request.StackId)}",
            null,
            response => ReadAsync(response, new RegistryStackManagementOperationResponse(false, null, [Error(response)]), cancellationToken),
            () => new RegistryStackManagementOperationResponse(false, null, ["Registry sign-in is required."]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Put,
            $"api/v1/packages/{Uri.EscapeDataString(request.PackageId)}/versions/{Uri.EscapeDataString(request.Version)}/yank",
            JsonContent.Create(new RegistrySetPackageVersionYankRequest(request.IsYanked)),
            response => ReadAsync(response, new RegistryPackageManagementOperationResponse(false, null, [Error(response)]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            cancellationToken);

    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Put,
            $"api/v1/packages/{Uri.EscapeDataString(request.PackageId)}/versions/{Uri.EscapeDataString(request.Version)}/deprecation",
            JsonContent.Create(new RegistryDeprecatePackageVersionRequest(request.Message)),
            response => ReadAsync(response, new RegistryPackageManagementOperationResponse(false, null, [Error(response)]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            cancellationToken);

    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            request.Version is null ? HttpMethod.Delete : HttpMethod.Put,
            $"api/v1/packages/{Uri.EscapeDataString(request.PackageId)}/dist-tags/{Uri.EscapeDataString(request.Tag)}",
            request.Version is null ? null : JsonContent.Create(new RegistrySetPackageDistTagRequest(request.Version)),
            response => ReadAsync(response, new RegistryPackageManagementOperationResponse(false, null, [Error(response)]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            cancellationToken);

    public Task<RegistryPublishPackageResponse> PublishPackageAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken)
        => PublishAsync(
            request,
            RuntimeUploadKind.Package,
            "api/v1/packages/publish",
            "package",
            (response, token) => ReadAsync(response, new RegistryPublishPackageResponse(false, null, null, null, [], [Error(response)]), token),
            () => new RegistryPublishPackageResponse(false, null, null, null, [], ["Registry sign-in is required."]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryPublishStackResponse> PublishStackAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken)
        => PublishAsync(
            request,
            RuntimeUploadKind.Stack,
            "api/v1/stacks/publish",
            "stack",
            (response, token) => ReadAsync(response, new RegistryPublishStackResponse(false, null, null, [], [Error(response)]), token),
            () => new RegistryPublishStackResponse(false, null, null, [], ["Registry sign-in is required."]) { Forbidden = true },
            cancellationToken);

    private async Task<T> PublishAsync<T>(
        RuntimeRegistryPublishRequest request,
        RuntimeUploadKind uploadKind,
        string path,
        string formName,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        Func<T> authenticationRequired,
        CancellationToken cancellationToken)
    {
        var transferStore = _transferStore ?? throw new InvalidOperationException("Runtime transfer storage is unavailable.");
        var packageSessions = _packageSessions ?? throw new InvalidOperationException("Runtime package sessions are unavailable.");
        var lease = transferStore.AcquireUpload(request.UploadId, uploadKind, packageSessions.Generation, consume: true);
        if (lease is null)
        {
            throw new InvalidDataException("Runtime upload was not found or expired.");
        }

        try
        {
            await using var stream = new FileStream(lease.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var form = new MultipartFormDataContent();
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = lease.Length;
            form.Add(content, formName, lease.FileName);
            if (uploadKind == RuntimeUploadKind.Package)
            {
                form.Add(new StringContent(request.SetLatest ? "true" : "false"), "setLatest");
            }

            return await SendAsync(request.RegistryOrigin, HttpMethod.Post, path, form, response => read(response, cancellationToken), authenticationRequired, cancellationToken);
        }
        finally
        {
            transferStore.ReleaseUpload(lease);
        }
    }

    private async Task<T> SendAsync<T>(
        string registryOriginValue,
        HttpMethod method,
        string path,
        HttpContent? content,
        Func<HttpResponseMessage, Task<T>> read,
        Func<T> authenticationRequired,
        CancellationToken cancellationToken)
    {
        var origin = RegistryOrigin.Normalize(registryOriginValue);
        var credential = await _credentialStore.GetAsync(origin, cancellationToken);
        if (credential is null || credential.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            if (credential is not null)
            {
                await _credentialStore.DeleteAsync(origin, cancellationToken);
            }
            content?.Dispose();
            return authenticationRequired();
        }

        using var request = new HttpRequestMessage(method, new Uri(origin, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        var client = _httpClientFactory.CreateClient("registry");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await _credentialStore.DeleteAsync(origin, cancellationToken);
            return authenticationRequired();
        }

        return await read(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, T fallback, CancellationToken cancellationToken)
        => await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken) ?? fallback;

    private static RegistryPackageManagementOperationResponse AuthenticationRequiredPackageManagement()
        => new(false, null, ["Registry sign-in is required."]) { Forbidden = true };

    private static string Error(HttpResponseMessage response)
        => response.StatusCode switch
        {
            HttpStatusCode.NotFound => "Registry resource was not found.",
            HttpStatusCode.Conflict => "Registry operation conflicted with existing state.",
            _ => response.ReasonPhrase ?? "Registry operation failed.",
        };
}
