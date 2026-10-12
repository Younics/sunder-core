using Avalonia.Controls;
using Avalonia.Threading;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Services;

internal static class RuntimeManagementDialogs
{
    public static async Task<bool> ShowAsync(Window owner, RuntimeConfirmationViewModel review, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new RuntimeConfirmationWindow(review);
        var result = dialog.ShowDialog<bool>(owner);
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await result.WaitAsync(token);
    }
}
