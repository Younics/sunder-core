using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryAuthenticatedOperations
{
    private readonly RegistryHttpClient _registryClient;
    private readonly RegistryCredentialStore _credentialStore;
    private readonly RuntimeContentTransferStore? _transferStore;
    private readonly Func<long>? _generation;

    public RegistryAuthenticatedOperations(
        RegistryHttpClient registryClient,
        RegistryCredentialStore credentialStore,
        RuntimeContentTransferStore transferStore,
        PackageSessionLifecycleService packageSessions)
        : this(registryClient, credentialStore)
    {
        _transferStore = transferStore;
        _generation = () => packageSessions.Generation;
    }

    internal RegistryAuthenticatedOperations(IHttpClientFactory httpClientFactory, RegistryCredentialStore credentialStore)
        : this(new RegistryHttpClient(httpClientFactory, new RuntimeTransportPolicyOptions(), TimeProvider.System), credentialStore)
    {
    }

    internal RegistryAuthenticatedOperations(IHttpClientFactory httpClientFactory, RegistryCredentialStore credentialStore,
        RuntimeContentTransferStore transferStore, Func<long> generation) : this(httpClientFactory, credentialStore)
    {
        _transferStore = transferStore;
        _generation = generation;
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
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.PackageStar(request.ResourceId)),
            null,
            response => ReadAsync(response, error => new RegistryPackageStarResponse(false, null, null, [error]), cancellationToken),
            () => new RegistryPackageStarResponse(false, null, null, ["Registry sign-in is required."]) { Forbidden = true },
            error => new RegistryPackageStarResponse(false, null, null, [error]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryStackStarResponse> SetStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            request.Starred ? HttpMethod.Put : HttpMethod.Delete,
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.StackStar(request.ResourceId)),
            null,
            response => ReadAsync(response, error => new RegistryStackStarResponse(false, null, null, [error]), cancellationToken),
            () => new RegistryStackStarResponse(false, null, null, ["Registry sign-in is required."]) { Forbidden = true },
            error => new RegistryStackStarResponse(false, null, null, [error]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryStackManagementOperationResponse> SetStackVisibilityAsync(RuntimeRegistryStackVisibilityRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Put,
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.StackVisibility(request.StackId)),
            null,
            async response =>
            {
                if (!response.IsSuccessStatusCode) return new RegistryStackManagementOperationResponse(false, null, [await _registryClient.ReadErrorAsync(response, cancellationToken)]);
                var policy = await _registryClient.ReadJsonAsync<RegistryStackPolicySnapshot>(response, cancellationToken);
                return new RegistryStackManagementOperationResponse(policy is not null, "Stack visibility updated.", policy is null ? ["Stack policy was missing."] : []);
            },
            () => new RegistryStackManagementOperationResponse(false, null, ["Registry sign-in is required."]) { Forbidden = true },
            error => new RegistryStackManagementOperationResponse(false, null, [error]) { Forbidden = true },
            cancellationToken, stackVisibility: request, alreadyVisible: () => new RegistryStackManagementOperationResponse(true, "Stack already has the requested visibility.", []));

    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Put,
            RegistryApiRoutes.ToHttpClientRelativePath(
                RegistryApiRoutes.PackageVersionYank(request.PackageId, request.Version)),
            JsonContent.Create(new RegistrySetPackageVersionYankRequest(request.IsYanked)),
            response => ReadAsync(response, error => new RegistryPackageManagementOperationResponse(false, null, [error]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            ForbiddenPackageManagement,
            cancellationToken, packagePolicyPackageId: request.PackageId);

    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            HttpMethod.Put,
            RegistryApiRoutes.ToHttpClientRelativePath(
                RegistryApiRoutes.PackageVersionDeprecation(request.PackageId, request.Version)),
            JsonContent.Create(new RegistryDeprecatePackageVersionRequest(request.Message)),
            response => ReadAsync(response, error => new RegistryPackageManagementOperationResponse(false, null, [error]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            ForbiddenPackageManagement,
            cancellationToken, packagePolicyPackageId: request.PackageId);

    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken cancellationToken)
        => SendAsync(
            request.RegistryOrigin,
            request.Version is null ? HttpMethod.Delete : HttpMethod.Put,
            RegistryApiRoutes.ToHttpClientRelativePath(
                RegistryApiRoutes.PackageDistTag(request.PackageId, request.Tag)),
            request.Version is null ? null : JsonContent.Create(new RegistrySetPackageDistTagRequest(request.Version)),
            response => ReadAsync(response, error => new RegistryPackageManagementOperationResponse(false, null, [error]), cancellationToken),
            AuthenticationRequiredPackageManagement,
            ForbiddenPackageManagement,
            cancellationToken, packagePolicyPackageId: request.PackageId);

    public Task<RegistryPublishPackageResponse> PublishPackageAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken)
        => PublishAsync(
            request,
            RuntimeUploadKind.Package,
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.PackagePublish),
            "package",
            (response, token) => ReadAsync(response, error => new RegistryPublishPackageResponse(false, null, null, null, [], [error]), token),
            () => new RegistryPublishPackageResponse(false, null, null, null, [], ["Registry sign-in is required."]) { Forbidden = true },
            error => new RegistryPublishPackageResponse(false, null, null, null, [], [error]) { Forbidden = true },
            cancellationToken);

    public Task<RegistryPublishStackResponse> PublishStackAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken)
        => PublishAsync(
            request,
            RuntimeUploadKind.Stack,
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.StackPublish),
            "stack",
            (response, token) => ReadAsync(response, error => new RegistryPublishStackResponse(false, null, null, [], [error]), token),
            () => new RegistryPublishStackResponse(false, null, null, [], ["Registry sign-in is required."]) { Forbidden = true },
            error => new RegistryPublishStackResponse(false, null, null, [], [error]) { Forbidden = true },
            cancellationToken);

    private async Task<T> PublishAsync<T>(
        RuntimeRegistryPublishRequest request,
        RuntimeUploadKind uploadKind,
        string path,
        string formName,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        Func<T> authenticationRequired,
        Func<string, T> forbidden,
        CancellationToken cancellationToken)
    {
        var transferStore = _transferStore ?? throw new InvalidOperationException("Runtime transfer storage is unavailable.");
        var generation = _generation ?? throw new InvalidOperationException("Runtime package sessions are unavailable.");
        var lease = transferStore.AcquireUpload(request.UploadId, uploadKind, generation(), consume: true);
        if (lease is null)
        {
            throw new InvalidDataException("Runtime upload was not found or expired.");
        }

        try
        {
            await using var stream = new FileStream(lease.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string? stackId = null;
            if (uploadKind == RuntimeUploadKind.Stack)
            {
                var inspection = lease.FilePath + ".inspection-" + Guid.NewGuid().ToString("N");
                try
                {
                    var validation = await SunderStackArchiveInspector.ExtractAndValidateAsync(lease.FilePath, inspection, cancellationToken);
                    if (!validation.Success || string.IsNullOrWhiteSpace(validation.Manifest?.StackId))
                        throw new InvalidDataException("Stack upload validation failed: " + string.Join(" ", validation.Errors));
                    stackId = validation.Manifest.StackId;
                }
                finally { if (Directory.Exists(inspection)) Directory.Delete(inspection, recursive: true); }
            }
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
                cancellationToken, authorityPolicy: uploadKind == RuntimeUploadKind.Stack, stackId: stackId);
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
        Func<string, T> forbidden,
        CancellationToken cancellationToken, string? packagePolicyPackageId = null, bool authorityPolicy = false,
        RuntimeRegistryStackVisibilityRequest? stackVisibility = null, Func<T>? alreadyVisible = null, string? stackId = null)
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
        if (stackId is not null)
        {
            using var stateRequest = new HttpRequestMessage(HttpMethod.Get,
                new Uri(origin, RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.StackPolicy(stackId))));
            stateRequest.Headers.Authorization = request.Headers.Authorization;
            using var stateResponse = await _registryClient.SendAsync(stateRequest, cancellationToken);
            if (stateResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                await _credentialStore.TryDeleteAsync(origin, snapshot, cancellationToken);
                return authenticationRequired();
            }
            if (stateResponse.StatusCode == HttpStatusCode.Forbidden)
                return forbidden("Reading Stack policy: " + await _registryClient.ReadErrorAsync(stateResponse, cancellationToken));
            if (stateResponse.StatusCode == HttpStatusCode.NotFound)
            {
                using var reserveRequest = new HttpRequestMessage(HttpMethod.Put,
                    new Uri(origin, RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.CurrentUserStackReservation(stackId))));
                reserveRequest.Headers.Authorization = request.Headers.Authorization;
                using var reserveResponse = await _registryClient.SendAsync(reserveRequest, cancellationToken);
                if (reserveResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await _credentialStore.TryDeleteAsync(origin, snapshot, cancellationToken);
                    return authenticationRequired();
                }
                if (reserveResponse.StatusCode == HttpStatusCode.Forbidden)
                    return forbidden("Reserving Stack identity: " + await _registryClient.ReadErrorAsync(reserveResponse, cancellationToken));
                if (!reserveResponse.IsSuccessStatusCode) return await read(reserveResponse);
                var reservation = await _registryClient.ReadJsonAsync<RegistryIdentityReservationOperationResponse>(reserveResponse, cancellationToken);
                if (reservation is not { Success: true, Reservation: { State: "Reserved", Kind: "Stack" } lease }
                    || lease.ResourceId != stackId || lease.OwnerPrincipalId != credential.UserId || lease.LeaseId == Guid.Empty
                    || lease.Revision <= 0 || lease.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
                    throw new InvalidDataException("The Registry did not confirm a current exact Stack identity reservation.");
            }
            else if (!stateResponse.IsSuccessStatusCode) return await read(stateResponse);
            else
            {
                var state = await _registryClient.ReadJsonAsync<RegistryStackPolicySnapshot>(stateResponse, cancellationToken);
                if (state?.StackId != stackId) throw new InvalidDataException("The Registry returned an inconsistent Stack policy identity.");
            }
        }
        if (packagePolicyPackageId is not null || authorityPolicy || stackVisibility is not null)
        {
            using var policyRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(origin,
                RegistryApiRoutes.ToHttpClientRelativePath(stackVisibility is not null ? RegistryApiRoutes.StackPolicy(stackVisibility.StackId)
                    : authorityPolicy ? RegistryApiRoutes.PublicationAuthorityPolicy : RegistryApiRoutes.PackageDistTags(packagePolicyPackageId!))));
            policyRequest.Headers.Authorization = request.Headers.Authorization;
            using var policyResponse = await _registryClient.SendAsync(policyRequest, cancellationToken);
            if (policyResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                await _credentialStore.TryDeleteAsync(origin, snapshot, cancellationToken);
                return authenticationRequired();
            }
            if (policyResponse.StatusCode == HttpStatusCode.Forbidden)
                return forbidden("Reading Registry publication policy: " + await _registryClient.ReadErrorAsync(policyResponse, cancellationToken));
            if (!policyResponse.IsSuccessStatusCode) return await read(policyResponse);
            var key = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            RegistryPolicyIdentity? policy;
            if (stackVisibility is not null)
            {
                var state = await _registryClient.ReadJsonAsync<RegistryStackPolicySnapshot>(policyResponse, cancellationToken) ?? throw new InvalidDataException("Verified Stack policy is required.");
                var visibility = stackVisibility.Listed ? "Listed" : "Unlisted";
                if (state.Visibility == visibility) return alreadyVisible!();
                policy = state.PublicationAuthorityPolicy;
                request.Content = JsonContent.Create(new RegistryStackVisibilityRequest(key, policy.Revision, state.Visibility, visibility));
            }
            else policy = authorityPolicy ? await _registryClient.ReadJsonAsync<RegistryPolicyIdentity>(policyResponse, cancellationToken)
                : (await _registryClient.ReadJsonAsync<RegistryPackageDistTagsResponse>(policyResponse, cancellationToken))?.PackagePolicy;
            if (policy is not { Revision: > 0 }) throw new InvalidDataException("Verified policy is required before Registry mutation.");
            request.Headers.Add(RegistryPolicyMutationHeaders.IdempotencyKey, key);
            request.Headers.Add(authorityPolicy || stackVisibility is not null ? RegistryPolicyMutationHeaders.PublicationAuthorityRevision : RegistryPolicyMutationHeaders.PackagePolicyRevision,
                policy.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        using var response = await _registryClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _credentialStore.TryDeleteAsync(origin, snapshot, cancellationToken);
            return authenticationRequired();
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return forbidden("Registry operation: " + await _registryClient.ReadErrorAsync(response, cancellationToken));
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

    private static RegistryPackageManagementOperationResponse ForbiddenPackageManagement(string error)
        => new(false, null, [error]) { Forbidden = true };

}
