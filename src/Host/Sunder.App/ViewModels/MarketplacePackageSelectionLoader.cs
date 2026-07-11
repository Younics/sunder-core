using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class MarketplacePackageSelectionLoader(PackagesMarketplaceCatalog marketplaceCatalog) : IDisposable
{
    private readonly LatestAsyncRequest _request = new();
    private LatestAsyncRequest.Lease? _selection;

    public int StartSelection()
    {
        _selection = _request.Start();
        return checked((int)_selection.Generation);
    }

    public void Invalidate()
    {
        _request.Invalidate();
        _selection = null;
    }

    public bool IsCurrent(int selectionVersion)
        => _request.IsCurrent(selectionVersion);

    public async Task<PackagesMarketplaceDetailsResult?> LoadDetailsAsync(
        int selectionVersion,
        string packageId,
        Action<RegistryPackageVersionItemViewModel> selectVersion,
        CancellationToken cancellationToken)
    {
        var selection = _selection;
        if (selection is null || selectionVersion != selection.Generation)
        {
            return null;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                selection.Token,
                cancellationToken);
            return await marketplaceCatalog
                .LoadDetailsAsync(packageId, selectVersion, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (selection.Token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_selection, selection))
            {
                _selection = null;
            }

            selection.Dispose();
        }
    }

    public void Dispose()
    {
        _request.Dispose();
        _selection?.Dispose();
        _selection = null;
    }
}
