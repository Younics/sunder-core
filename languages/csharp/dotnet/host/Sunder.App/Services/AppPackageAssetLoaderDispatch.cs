using Avalonia.Platform;

namespace Sunder.App.Services;

// AssetLoader resolves its service synchronously once per public operation.
// Redirect only that next resolution on this thread, never the global service.
internal sealed class AppPackageAssetLoaderDispatch(IAssetLoader platformLoader)
{
    [ThreadStatic] private static PendingPlatformCall? _pending;

    internal IAssetLoader PlatformLoader { get; } = platformLoader;

    internal IAssetLoader Resolve(IAssetLoader packageLoader)
    {
        if (_pending is { Consumed: false } call && ReferenceEquals(call.Owner, this))
        {
            // Consume before invoking the platform loader so its own reentrant
            // asset requests still go through normal package resolution.
            call.Consumed = true;
            return PlatformLoader;
        }

        return packageLoader;
    }

    internal T Invoke<T>(Func<T> operation)
    {
        var previous = _pending;
        var call = new PendingPlatformCall(this);
        _pending = call;
        try
        {
            var result = operation();
            if (!call.Consumed)
            {
                throw new InvalidOperationException("Package asset-loader forwarding requires its dispatch factory to be installed.");
            }

            return result;
        }
        finally
        {
            _pending = previous;
        }
    }

    internal void Invoke(Action operation)
        => Invoke(() => { operation(); return true; });

    private sealed class PendingPlatformCall(AppPackageAssetLoaderDispatch owner)
    {
        internal AppPackageAssetLoaderDispatch Owner { get; } = owner;
        internal bool Consumed { get; set; }
    }
}
