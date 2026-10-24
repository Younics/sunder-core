using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed partial class AppPackageDevelopmentService
{
    private sealed partial class Session
    {
        private readonly AppDevelopmentViewOperations _viewOperations = new();
        public async Task<Sunder.Sdk.Services.SunderContent> CaptureViewAsync(string viewId, PackageDevelopmentViewQuery scope, int maximumEdge = 1280, CancellationToken cancellationToken = default)
        {
            scope.Validate();
            using var operation = BeginOperation(cancellationToken);
            var status = await GetStatusAsync(operation.Token).ConfigureAwait(false);
            if (status.AppliedRevision is not { } revision || status.AppReadiness != PackageDevelopmentAppReadiness.Activated) throw new InvalidOperationException("Wait for the applied App revision before capture.");
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                var binding = await service.Backend.OpenDevelopmentViewAsync(PackageId, viewId, revision, operation.Token).ConfigureAwait(false);
                if (binding.PackageId != PackageId || binding.ViewId != viewId || binding.Revision != revision) throw new InvalidDataException("Different development view returned.");
                return await _viewOperations.CaptureAsync(binding, scope, maximumEdge, operation.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task<IReadOnlyList<PackageDevelopmentView>> GetViewsAsync(CancellationToken cancellationToken = default)
        {
            using var operation = BeginOperation(cancellationToken);
            var status = await GetStatusAsync(operation.Token).ConfigureAwait(false);
            if (status.AppliedRevision is not { } revision) throw new InvalidOperationException("Wait for this package's applied App revision before discovering views.");
            return await service.Backend.GetDevelopmentViewsAsync(PackageId, revision, operation.Token).ConfigureAwait(false);
        }

        public async Task OpenViewAsync(string viewId, CancellationToken cancellationToken = default)
        {
            using var operation = BeginOperation(cancellationToken);
            var status = await GetStatusAsync(operation.Token).ConfigureAwait(false);
            if (status.AppliedRevision is not { } revision) throw new InvalidOperationException("Wait for the applied App revision before opening a view.");
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            try { await service.Backend.ShowDevelopmentViewAsync(PackageId, viewId, revision, operation.Token).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        public async Task<PackageDevelopmentViewPage> QueryViewAsync(string viewId, PackageDevelopmentViewQuery query, CancellationToken cancellationToken = default)
        {
            query.Validate();
            using var operation = BeginOperation(cancellationToken);
            var status = await GetStatusAsync(operation.Token).ConfigureAwait(false);
            if (status.AppReadiness != PackageDevelopmentAppReadiness.Activated || status.AppliedRevision is not { } revision)
                throw new InvalidOperationException("Wait for this package's applied App revision before querying its view.");
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                var binding = await service.Backend.OpenDevelopmentViewAsync(PackageId, viewId, revision, operation.Token).ConfigureAwait(false);
                if (binding.PackageId != PackageId || binding.ViewId != viewId || binding.Revision != revision) throw new InvalidDataException("Different development view returned.");
                return await _viewOperations.QueryAsync(binding, query, operation.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task<PackageDevelopmentViewSnapshot> InspectViewAsync(string viewId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(viewId) || viewId.Length > 256) throw new ArgumentException("A bounded package view identity is required.", nameof(viewId));
            using var operation = BeginOperation(cancellationToken);
            var entered = false;
            try
            {
                var status = await GetStatusAsync(operation.Token).ConfigureAwait(false);
                if (status.AppReadiness != PackageDevelopmentAppReadiness.Activated || status.AppliedRevision is not { } revision)
                    throw new InvalidOperationException("The exact development package must have an applied App revision before view inspection.");
                await _gate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
                var binding = await service.Backend.OpenDevelopmentViewAsync(PackageId, viewId, revision, operation.Token).ConfigureAwait(false);
                if (binding.PackageId != PackageId || binding.ViewId != viewId || binding.Revision != revision)
                    throw new InvalidDataException("The App did not bind the selected development package, view and applied source.");
                return await _viewOperations.InspectAsync(binding, operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (error.CancellationToken == operation.Token && operation.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(error.Message, error, cancellationToken);
                throw new ObjectDisposedException(nameof(IPackageDevelopmentSession), "The owning App development session was retired.");
            }
            finally { if (entered) _gate.Release(); }
        }

        public async Task<PackageDevelopmentViewInputResult> InteractViewAsync(PackageDevelopmentViewInput request, CancellationToken cancellationToken = default)
        {
            using var operation = BeginOperation(cancellationToken);
            var entered = false;
            try
            {
                await _gate.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
                return await _viewOperations.InteractAsync(request, operation.Token).ConfigureAwait(false);
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
