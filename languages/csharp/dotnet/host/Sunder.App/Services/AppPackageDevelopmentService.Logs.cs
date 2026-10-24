using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed partial class AppPackageDevelopmentService
{
    public async Task<PackageDevelopmentLogs> GetLogsAsync(string packageId, PackageDevelopmentLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        using var operation = BeginOperation(cancellationToken);
        try
        {
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                if (!_logPackages.Contains(packageId))
                    throw new InvalidOperationException("This App activation has no development diagnostic scope for that package.");
            }
            finally { _gate.Release(); }
            var result = await Backend.GetLogsAsync(packageId, query, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (result.PackageId != packageId || result.Entries.Count > query.MaximumEntries
                || System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result).Length > query.MaximumBytes)
                throw new InvalidDataException("The development log response exceeds its authorized package scope or response budget.");
            return result;
        }
        catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, cancellationToken);
            throw new ObjectDisposedException(nameof(IPackageDevelopmentService), "The owning App activation was retired.");
        }
    }

    private sealed partial class Session
    {
        public async Task<PackageDevelopmentLogs> GetLogsAsync(PackageDevelopmentLogQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            query.Validate();
            using var operation = BeginOperation(cancellationToken);
            try { return await service.GetLogsAsync(PackageId, query, operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, cancellationToken);
                throw new ObjectDisposedException(nameof(IPackageDevelopmentSession), "The owning App development session was retired.");
            }
        }
        public async Task<PackageDevelopmentLogs> GetLogsAsync(int maximumEntries = 50, CancellationToken cancellationToken = default)
        {
            if (maximumEntries is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            using var operation = BeginOperation(cancellationToken);
            try
            {
                var result = await service.Backend.GetLogsAsync(PackageId, maximumEntries, operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                if (result.PackageId != PackageId || result.Entries.Count > maximumEntries
                    || result.Entries.Sum(entry => (long)entry.Message.Length + entry.Category.Length) > 64 * 1024)
                    throw new InvalidDataException("The development log response exceeds its authorized package scope or output limit.");
                return result;
            }
            catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, cancellationToken);
                throw new ObjectDisposedException(nameof(IPackageDevelopmentSession), "The owning App development session was retired.");
            }
        }
    }
}
