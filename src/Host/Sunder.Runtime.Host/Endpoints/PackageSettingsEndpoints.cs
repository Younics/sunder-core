using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Storage;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageSettingsEndpoints
{
    public static IEndpointRouteBuilder MapPackageSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var packages = endpoints.MapGroup("/packages");
        packages.MapGet(
            "settings/schemas",
            (PackageSettingsAccessService settings) => Results.Ok(settings.GetSchemas()));

        packages.MapGet("{packageId}/settings/values", GetValuesAsync);
        packages.MapPut("{packageId}/settings/values", SaveValuesAsync);
        packages.MapGet("{packageId}/settings/{key}", GetValueAsync);
        packages.MapPut("{packageId}/settings/{key}", SetValueAsync);
        packages.MapDelete("{packageId}/settings/{key}", DeleteValueAsync);
        return endpoints;
    }

    private static async Task<IResult> GetValuesAsync(
        string packageId,
        PackageSettingsAccessService settings,
        CancellationToken cancellationToken)
    {
        ValidatePackageId(packageId);
        var values = await settings.GetValuesAsync(packageId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(RuntimeEndpointErrors.Required(values, $"Package '{packageId}' settings"));
    }

    private static async Task<IResult> SaveValuesAsync(
        string packageId,
        UpdatePackageSettingsRequest request,
        PackageSettingsAccessService settings,
        CancellationToken cancellationToken)
    {
        ValidatePackageId(packageId);
        if (request.Values.Any(static pair => !PackageStorageValidation.IsValidKey(pair.Key)
                                              || pair.Value is not null
                                              && !PackageStorageValidation.IsValidValue(pair.Value)))
        {
            throw new RuntimeValidationException("A package setting key or value is invalid.");
        }

        if (!await settings.SaveValuesAsync(packageId, request, cancellationToken).ConfigureAwait(false))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' settings were not found.");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetValueAsync(
        string packageId,
        string key,
        PackageSettingsAccessService settings,
        CancellationToken cancellationToken)
    {
        Validate(packageId, key);
        var value = await settings.GetValueAsync(packageId, key, cancellationToken).ConfigureAwait(false);
        return Results.Ok(RuntimeEndpointErrors.Required(value, "Package setting"));
    }

    private static async Task<IResult> SetValueAsync(
        string packageId,
        string key,
        SetPackageSettingValueRequest request,
        PackageSettingsAccessService settings,
        CancellationToken cancellationToken)
    {
        Validate(packageId, key);
        if (!PackageStorageValidation.IsValidValue(request.Value))
        {
            throw new RuntimeValidationException("The package setting value is invalid.");
        }

        if (!await settings.SetValueAsync(packageId, key, request.Value, cancellationToken).ConfigureAwait(false))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteValueAsync(
        string packageId,
        string key,
        PackageSettingsAccessService settings,
        CancellationToken cancellationToken)
    {
        Validate(packageId, key);
        if (!await settings.DeleteValueAsync(packageId, key, cancellationToken).ConfigureAwait(false))
        {
            throw new RuntimeNotFoundException($"Package '{packageId}' is not active.");
        }

        return Results.NoContent();
    }

    private static void Validate(string packageId, string key)
    {
        ValidatePackageId(packageId);
        if (!PackageDataInputValidator.IsKey(key))
        {
            throw new RuntimeValidationException("The package setting key is invalid.");
        }
    }

    private static void ValidatePackageId(string packageId)
    {
        if (!PackageDataInputValidator.IsPackageId(packageId))
        {
            throw new RuntimeValidationException("The package id is invalid.");
        }
    }
}
