using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed partial class StacksWindowViewModel
{
    private bool TryResolveRegistryUrlForRegistryAction(out Uri registryUrl)
    {
        if (Registry.TryResolveRegistryUrl(out registryUrl))
        {
            return true;
        }

        StatusText = "Enter a valid HTTP Registry URL before using Registry Stacks.";
        NotifyRegistryStackStateChanged();
        return false;
    }

    private bool TryResolvePublishedRegistryUrl(LocalStackLibraryItemViewModel stack, out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(stack.RegistryUrl, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        return TryResolveRegistryUrlForRegistryAction(out registryUrl);
    }

    private void QueueRegistrySearch(TimeSpan? delay = null)
        => _registrySearchScheduler.Queue(delay);

    private void CancelQueuedRegistrySearch()
        => _registrySearchScheduler.Cancel();
}
