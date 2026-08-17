namespace Sunder.App.Services;

internal static class AppPackageResourceDisposer
{
    public static async Task DisposeOwnedInstanceAsync(object ownedInstance)
    {
        if (ownedInstance is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        if (ownedInstance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public static async Task<bool> TryDisposeOwnedInstanceAsync(object ownedInstance, string message)
    {
        try
        {
            await DisposeOwnedInstanceAsync(ownedInstance);
            return true;
        }
        catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
        {
            AppSessionLog.WriteError(message, ex);
            return false;
        }
    }

    public static bool TryUnloadLoadContext(AppPackageLoadContext loadContext, string? packageId)
    {
        try
        {
            loadContext.Unload();
            return true;
        }
        catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
        {
            AppSessionLog.WriteError(packageId is null
                ? "Failed to unload an app-side package load context."
                : $"Failed to unload app-side package load context for '{packageId}'.", ex);
            return false;
        }
    }
}
