using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Storage;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageDataEndpoints
{
    public static IEndpointRouteBuilder MapPackageDataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/packages/{packageId}/data");

        group.MapGet("state/{key}", GetStateAsync);
        group.MapGet("state", ListStateAsync);
        group.MapPut("state/{key}", SetStateAsync);
        group.MapDelete("state/{key}", DeleteStateAsync);
        group.MapGet("secrets/{key}", GetSecretAsync);
        group.MapPut("secrets/{key}", SetSecretAsync);
        group.MapDelete("secrets/{key}", DeleteSecretAsync);
        group.MapGet("files/{**relativePath}", ReadFileAsync);
        group.MapPut("files/{**relativePath}", WriteFileAsync);
        group.MapDelete("files/{**relativePath}", DeleteFileAsync);
        return endpoints;
    }

    private static async Task<IResult> GetStateAsync(
        string packageId, string key, RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsKey(key))
        {
            throw new RuntimeValidationException("The package id or state key is invalid.");
        }

        var result = await service.GetStateAsync(packageId, key, cancellationToken);
        return Results.Ok(RuntimeEndpointErrors.Required(result, "Package state value"));
    }

    private static async Task<IResult> ListStateAsync(
        string packageId, string? prefix, RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId)
            || (prefix is not null && !PackageDataInputValidator.IsKeyPrefix(prefix)))
        {
            throw new RuntimeValidationException("The package id or state key prefix is invalid.");
        }

        var keys = await service.ListStateKeysAsync(packageId, prefix, cancellationToken);
        return Results.Ok(new PackageDataKeysResponse(RuntimeEndpointErrors.Required(keys, "Package state")));
    }

    private static async Task<IResult> SetStateAsync(
        string packageId, string key, SetPackageDataValueRequest request,
        RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsKey(key)
            || !PackageStorageValidation.IsValidValue(request.Value))
        {
            throw new RuntimeValidationException("The package id, state key, or value is invalid.");
        }

        if (!await service.SetStateAsync(packageId, key, request.Value, cancellationToken))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteStateAsync(
        string packageId, string key, RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsKey(key))
        {
            throw new RuntimeValidationException("The package id or state key is invalid.");
        }

        if (!await service.DeleteStateAsync(packageId, key, cancellationToken))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }
        return Results.NoContent();
    }

    private static Task<IResult> GetSecretAsync(
        string packageId, string key, RuntimePackageDataService service, CancellationToken cancellationToken)
        => GetSecretCoreAsync(packageId, key, service, cancellationToken);

    private static async Task<IResult> GetSecretCoreAsync(
        string packageId, string key, RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsKey(key))
        {
            throw new RuntimeValidationException("The package id or secret key is invalid.");
        }

        var result = await service.GetSecretAsync(packageId, key, cancellationToken);
        return Results.Ok(RuntimeEndpointErrors.Required(result, "Package secret"));
    }

    private static async Task<IResult> SetSecretAsync(
        string packageId, string key, SetPackageDataValueRequest request,
        RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsKey(key)
            || !PackageStorageValidation.IsValidValue(request.Value))
        {
            throw new RuntimeValidationException("The package id, secret key, or value is invalid.");
        }

        if (!await service.SetSecretAsync(packageId, key, request.Value, cancellationToken))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSecretAsync(
        string packageId, string key, RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsKey(key))
        {
            throw new RuntimeValidationException("The package id or secret key is invalid.");
        }

        if (!await service.DeleteSecretAsync(packageId, key, cancellationToken))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }
        return Results.NoContent();
    }

    private static async Task<IResult> ReadFileAsync(
        string packageId, string relativePath, RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsRelativePath(relativePath))
        {
            throw new RuntimeValidationException("The package id or relative file path is invalid.");
        }

        try
        {
            var contents = await service.ReadFileAsync(
                packageId,
                relativePath,
                PackageStorageValidation.MaximumFileBytes,
                cancellationToken);
            return Results.File(RuntimeEndpointErrors.Required(contents, "Package file"), "application/octet-stream");
        }
        catch (InvalidDataException)
        {
            throw new RuntimeUploadLimitException(
                $"Package file exceeds the {PackageStorageValidation.MaximumFileBytes} byte limit.");
        }
    }

    private static async Task<IResult> WriteFileAsync(
        string packageId, string relativePath, HttpRequest request,
        RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsRelativePath(relativePath)
            || request.ContentLength is long contentLength
               && !PackageStorageValidation.IsValidFileLength(contentLength))
        {
            if (request.ContentLength is long invalidLength
                && !PackageStorageValidation.IsValidFileLength(invalidLength))
            {
                throw new RuntimeUploadLimitException(
                    $"Package file exceeds the {PackageStorageValidation.MaximumFileBytes} byte limit.");
            }
            throw new RuntimeValidationException("The package id or relative file path is invalid.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + bytesRead > PackageStorageValidation.MaximumFileBytes)
            {
                throw new RuntimeUploadLimitException(
                    $"Package file exceeds the {PackageStorageValidation.MaximumFileBytes} byte limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        if (!await service.WriteFileAsync(packageId, relativePath, buffer.ToArray(), cancellationToken))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteFileAsync(
        string packageId, string relativePath, RuntimePackageDataService service, CancellationToken cancellationToken)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId) || !PackageDataInputValidator.IsRelativePath(relativePath))
        {
            throw new RuntimeValidationException("The package id or relative file path is invalid.");
        }

        if (!await service.DeleteFileAsync(packageId, relativePath, cancellationToken))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }
        return Results.NoContent();
    }

}

internal static class PackageDataInputValidator
{
    internal static bool IsPackageId(string value)
        => PackageId.TryParse(value, out _);

    internal static bool IsKey(string value)
        => PackageStorageValidation.IsValidKey(value);

    internal static bool IsKeyPrefix(string value)
        => value.Length == 0 || IsKey(value);

    internal static bool IsRelativePath(string value)
        => PackageStorageValidation.IsValidRelativePath(value);

    internal static bool IsLeaseId(string value)
        => value.Length == 32 && value.All(Uri.IsHexDigit);

}
