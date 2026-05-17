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

    public static async Task TryDisposeOwnedInstanceAsync(object ownedInstance, string message)
    {
        try
        {
            await DisposeOwnedInstanceAsync(ownedInstance);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError(message, ex);
        }
    }

    public static void TryUnloadLoadContext(AppPackageLoadContext loadContext, string? packageId)
    {
        try
        {
            loadContext.Unload();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError(packageId is null
                ? "Failed to unload an app-side package load context."
                : $"Failed to unload app-side package load context for '{packageId}'.", ex);
        }
    }
}
