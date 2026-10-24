using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed partial class AppPackageDevelopmentService
{
    private sealed partial class Session
    {
        private string? _reloadOperationId;
        private string? _reloadContentIdentity;
        private bool _reloadUnresolved;

        public async Task<PackageDevelopmentRevision> ReloadAsync(string operationId, string sourceContentIdentity,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
            if (!IsSourceIdentity(sourceContentIdentity)) throw new ArgumentException("Reload requires the exact built source identity.", nameof(sourceContentIdentity));
            using var operation = BeginOperation(cancellationToken);
            var entered = false;
            try
            {
                await _gate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
                if (_watch) throw new InvalidOperationException("Stop this owner's output watch before requesting an explicit reload.");
                if (_reloadOperationId == operationId && _reloadContentIdentity != sourceContentIdentity)
                    throw new InvalidOperationException("A reload operation id cannot be reused for different output.");
                if (_reloadOperationId != operationId)
                {
                    if (_reloadUnresolved) throw new InvalidOperationException("Resolve the previous exact reload before building or applying another revision.");
                    var preparedId = await service.Backend.PrepareAsync(folder, operation.Token).ConfigureAwait(false);
                    if (preparedId != PackageId) throw new InvalidDataException("The development package identity changed. Unload this owner before loading another identity.");
                    _reloadOperationId = operationId;
                    _reloadContentIdentity = sourceContentIdentity;
                    _reloadUnresolved = true;
                }
                var result = await service.Backend.ReloadOwnerAsync(owner, operationId, PackageId, sourceContentIdentity, operation.Token).ConfigureAwait(false);
                if (result.PackageId != PackageId || result.SourceContentIdentity != sourceContentIdentity
                    || result.Snapshot.RuntimeInstanceId != result.RuntimeInstanceId
                    || result.Snapshot.SessionPackages.SingleOrDefault(item => item.PackageId == PackageId)?.SourceContentIdentity != sourceContentIdentity)
                    throw new InvalidDataException("The reload acknowledgement describes different output.");
                _reloadUnresolved = false;
                operation.Token.ThrowIfCancellationRequested();
                return new(result.RuntimeInstanceId, result.Snapshot.SessionGeneration, sourceContentIdentity);
            }
            catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, cancellationToken);
                throw new ObjectDisposedException(nameof(IPackageDevelopmentSession), "The owning App development session was retired.");
            }
            finally { if (entered) _gate.Release(); }
        }
    }
}
