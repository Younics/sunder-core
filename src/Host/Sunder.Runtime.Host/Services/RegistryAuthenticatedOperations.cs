using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryAuthenticatedOperations
{
    private readonly RegistryHttpClient _registryClient;
    private readonly RegistryCredentialStore _credentialStore;
    private readonly RuntimeContentTransferStore? _transferStore;
    private readonly PackageSessionLifecycleService? _packageSessions;

    public RegistryAuthenticatedOperations(
        RegistryHttpClient registryClient,
        RegistryCredentialStore credentialStore,
        RuntimeContentTransferStore transferStore,
        PackageSessionLifecycleService packageSessions)
        : this(registryClient, credentialStore)
    {
        _transferStore = transferStore;
        _packageSessions = packageSessions;
    }

    internal RegistryAuthenticatedOperations(IHttpClientFactory httpClientFactory, RegistryCredentialStore credentialStore)
        : this(new RegistryHttpClient(httpClientFactory, new RuntimeTransportPolicyOptions(), TimeProvider.System), credentialStore)
    {
    }

    private RegistryAuthenticatedOperations(RegistryHttpClient registryClient, RegistryCredentialStore credentialStore)
    {
        _registryClient = registryClient;
        _credentialStore = credentialStore;
    }

    public Task<RegistryPackageStarResponse> SetPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            request.Starred ? HttpMethod.Put : HttpMethod.Delete,
            $"api/v1/packages/{Uri.EscapeDataString(request.ResourceId)}/star",
            null,
            response => ReadAsync(response, error => new RegistryPackageStarResponse(false, null, null, [error]), cancellationToken),
            () => new RegistryPackageStarResponse(false, null, null, ["Registry sign-in is required."]) { Forbidden = true },
            () => new RegistryPackageStarResponse(false, null, null, ["Registry authorization was denied."]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryStackStarResponse> SetStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            request.Starred ? HttpMethod.Put : HttpMethod.Delete,
            $"api/v1/stacks/{Uri.EscapeDataString(request.ResourceId)}/star",
            null,
            response => ReadAsync(response, error => new RegistryStackStarResponse(false, null, null, [error]), cancellationToken),
            () => new RegistryStackStarResponse(false, null, null, ["Registry sign-in is required."]) { Forbidden = true },
            () => new RegistryStackStarResponse(false, null, null, ["Registry authorization was denied."]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryStackManagementOperationResponse> DeleteStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Delete,
            $"api/v1/stacks/{Uri.EscapeDataString(request.StackId)}",
            null,
            response => ReadAsync(response, error => new RegistryStackManagementOperationResponse(false, null, [error]), cancellationToken),
            () => new RegistryStackManagementOperationResponse(false, null, ["Registry sign-in is required."]) { Forbidden = true },
            () => new RegistryStackManagementOperationResponse(false, null, ["Registry authorization was denied."]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Put,
            $"api/v1/packages/{Uri.EscapeDataString(request.PackageId)}/versions/{Uri.EscapeDataString(request.Version)}/yank",
            JsonContent.Create(new RegistrySetPackageVersionYankRequest(request.IsYanked)),
            response => ReadAsync(response, error => new RegistryPackageManagementOperationResponse(false, null, [error]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            ForbiddenPackageManagement,
            cancellationToken);

    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Put,
            $"api/v1/packages/{Uri.EscapeDataString(request.PackageId)}/versions/{Uri.EscapeDataString(request.Version)}/deprecation",
            JsonContent.Create(new RegistryDeprecatePackageVersionRequest(request.Message)),
            response => ReadAsync(response, error => new RegistryPackageManagementOperationResponse(false, null, [error]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            ForbiddenPackageManagement,
            cancellationToken);

    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            request.Version is null ? HttpMethod.Delete : HttpMethod.Put,
            $"api/v1/packages/{Uri.EscapeDataString(request.PackageId)}/dist-tags/{Uri.EscapeDataString(request.Tag)}",
            request.Version is null ? null : JsonContent.Create(new RegistrySetPackageDistTagRequest(request.Version)),
            response => ReadAsync(response, error => new RegistryPackageManagementOperationResponse(false, null, [error]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            ForbiddenPackageManagement,
            cancellationToken);

    public Task<RegistryPublishPackageResponse> PublishPackageAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken)
        => PublishAsync(
            request,
            RuntimeUploadKind.Package,
            "api/v1/packages/publish",
            "package",
            (response, token) => ReadAsync(response, error => new RegistryPublishPackageResponse(false, null, null, null, [], [error]), token),
            () => new RegistryPublishPackageResponse(false, null, null, null, [], ["Registry sign-in is required."]) { Forbidden = true },
            () => new RegistryPublishPackageResponse(false, null, null, null, [], ["Registry authorization was denied."]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryPublishStackResponse> PublishStackAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken)
        => PublishAsync(
            request,
            RuntimeUploadKind.Stack,
            "api/v1/stacks/publish",
            "stack",
            (response, token) => ReadAsync(response, error => new RegistryPublishStackResponse(false, null, null, [], [error]), token),
            () => new RegistryPublishStackResponse(false, null, null, [], ["Registry sign-in is required."]) { Forbidden = true },
            () => new RegistryPublishStackResponse(false, null, null, [], ["Registry authorization was denied."]) { Forbidden = true },
            cancellationToken);

    private async Task<T> PublishAsync<T>(
        RuntimeRegistryPublishRequest request,
        RuntimeUploadKind uploadKind,
        string path,
        string formName,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        Func<T> authenticationRequired,
        Func<T> forbidden,
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

            return await SendAsync(
                request.RegistryOrigin,
                HttpMethod.Post,
                path,
                form,
                response => read(response, cancellationToken),
                authenticationRequired,
                forbidden,
                cancellationToken);
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
        Func<T> forbidden,
        CancellationToken cancellationToken)
    {
        var origin = RegistryOrigin.Normalize(registryOriginValue);
        var snapshot = await _credentialStore.GetSnapshotAsync(origin, cancellationToken);
        if (snapshot is null || snapshot.Credential.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            if (snapshot is not null)
            {
                await _credentialStore.TryDeleteAsync(origin, snapshot, cancellationToken);
            }
            content?.Dispose();
            return authenticationRequired();
        }

        var credential = snapshot.Credential;
        using var request = new HttpRequestMessage(method, new Uri(origin, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        using var response = await _registryClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _credentialStore.TryDeleteAsync(origin, snapshot, cancellationToken);
            return authenticationRequired();
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return forbidden();
        }

        return await read(response);
    }

    private async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        Func<string, T> fallback,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return fallback(await _registryClient.ReadErrorAsync(response, cancellationToken));
        }

        var value = await _registryClient.ReadJsonAsync<T>(response, cancellationToken);
        if (value is not null) return value;
        return fallback(await _registryClient.ReadErrorAsync(response, cancellationToken));
    }

    private static RegistryPackageManagementOperationResponse AuthenticationRequiredPackageManagement()
        => new(false, null, ["Registry sign-in is required."]) { Forbidden = true };

    private static RegistryPackageManagementOperationResponse ForbiddenPackageManagement()
        => new(false, null, ["Registry authorization was denied."]) { Forbidden = true };

}
