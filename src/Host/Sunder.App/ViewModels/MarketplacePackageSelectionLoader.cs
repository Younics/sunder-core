namespace Sunder.App.ViewModels;

internal sealed class MarketplacePackageSelectionLoader(PackagesMarketplaceCatalog marketplaceCatalog) : IDisposable
{
    private CancellationTokenSource? _selectionCancellation;
    private int _selectionVersion;

    public int StartSelection()
    {
        CancelPendingSelection();
        _selectionCancellation = new CancellationTokenSource();
        return ++_selectionVersion;
    }

    public void Invalidate()
    {
        CancelPendingSelection();
        _selectionVersion++;
    }

    public bool IsCurrent(int selectionVersion)
        => selectionVersion == _selectionVersion;

    public async Task<PackagesMarketplaceDetailsResult?> LoadDetailsAsync(
        int selectionVersion,
        string packageId,
        Action<RegistryPackageVersionItemViewModel> selectVersion,
        CancellationToken cancellationToken)
    {
        var selectionCancellation = _selectionCancellation;
        if (selectionCancellation is null || selectionVersion != _selectionVersion)
        {
            return null;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                selectionCancellation.Token,
                cancellationToken);
            return await marketplaceCatalog
                .LoadDetailsAsync(packageId, selectVersion, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (selectionCancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_selectionCancellation, selectionCancellation))
            {
                _selectionCancellation = null;
            }

            selectionCancellation.Dispose();
        }
    }

    public void Dispose()
    {
        CancelPendingSelection();
    }

    private void CancelPendingSelection()
    {
        var selectionCancellation = _selectionCancellation;
        if (selectionCancellation is null)
        {
            return;
        }

        _selectionCancellation = null;
        selectionCancellation.Cancel();
    }
}
