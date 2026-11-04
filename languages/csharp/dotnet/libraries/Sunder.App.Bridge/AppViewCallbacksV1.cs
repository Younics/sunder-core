using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace Sunder.App.Bridge;

/// <summary>Callbacks implemented against the SDK owned by the view, including its current data context.</summary>
public interface IAppViewCallbacksV1
{
    ValueTask WarmupAsync(CancellationToken cancellationToken);
    ValueTask NavigateAsync(string viewId, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken);
    bool SupportsPreparation { get; }
    ValueTask<bool> PrepareAsync(string viewId, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken);
    ValueTask PresentedAsync(string viewId, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken);
}

/// <summary>Weak association: callbacks cannot keep a retired view or its collectible SDK alive.</summary>
public static class AppViewCallbacksV1
{
    private static readonly ConditionalWeakTable<Control, IAppViewCallbacksV1> Callbacks = new();

    public static void Attach(Control view, IAppViewCallbacksV1 callbacks)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(callbacks);
        Callbacks.Add(view, callbacks);
    }

    public static IAppViewCallbacksV1? Get(Control view) => Callbacks.TryGetValue(view, out var callbacks) ? callbacks : null;
}
