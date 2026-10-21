using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class PackageSessionDevPreparationConcurrencyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DevPreparation_RespectsConfiguredConcurrencyAndDrainsBeforeCancellationCleanup(int concurrency)
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-dev-preparation-cancellation");
        var folders = CreatePackages(temporary);
        var resolver = new ControlledPreparationResolver(folders.Length);
        var loader = CreateLoader(temporary, resolver, concurrency);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var load = loader.LoadInstalledWithDevOverlaysAsync([], folders, cancellationToken: cancellation.Token);
        string? sessionFolder = null;
        try
        {
            await WaitForPreparationAsync(Task.WhenAll(Enumerable.Range(0, concurrency).Select(resolver.WaitUntilEnteredAsync)), load);
            Assert.Equal(concurrency, resolver.ActiveCount);
            Assert.Equal(concurrency, resolver.MaximumActiveCount);
            Assert.Equal(concurrency, resolver.CallCount);

            resolver.Complete(0);
            await WaitForPreparationAsync(resolver.WaitUntilEnteredAsync(concurrency), load);
            Assert.False(load.IsCompleted);
            Assert.Equal(1, resolver.CreatedRetentions);
            Assert.Equal(0, resolver.DisposedRetentions);
            Assert.Equal(concurrency, resolver.MaximumActiveCount);
            Assert.Equal(concurrency + 1, resolver.CallCount);
            sessionFolder = FindSessionFolder(PackageId(folders[0]));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

            Assert.Equal(0, resolver.ActiveCount);
            Assert.Equal(1, resolver.DisposedRetentions);
            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            cancellation.Cancel();
            try { await load; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task DevPreparation_ReverseCompletionPreservesIndexesGraphOrderAndProjectionBytes()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-dev-preparation-order");
        var folders = CreatePackages(temporary);
        var resolver = new ControlledPreparationResolver(folders.Length);
        var loader = CreateLoader(temporary, resolver, folders.Length);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var load = loader.LoadInstalledWithDevOverlaysAsync([], folders, cancellationToken: cancellation.Token);
        try
        {
            await WaitForPreparationAsync(Task.WhenAll(Enumerable.Range(0, folders.Length).Select(resolver.WaitUntilEnteredAsync)), load);
            foreach (var index in Enumerable.Range(0, folders.Length).Reverse())
            {
                resolver.Complete(index);
                await resolver.WaitUntilCompletedAsync(index);
            }

            var result = await load;
            Assert.NotNull(result.Session);
            await using var session = result.Session;
            Assert.Equal(Enumerable.Range(0, folders.Length).Reverse(), resolver.CompletedIndexes);
            Assert.Equal(folders.Select(folder =>
                $"Package '{PackageId(folder)}' depends on 'missing.package' >=0.0.0-0, but that package was not supplied."), result.Errors);
            Assert.Empty(session.GetActivePackages());
            for (var index = 0; index < folders.Length; index++)
            {
                var candidateFolder = Path.Combine(session.SessionFolder!, $"{index:D2}-{PackageId(folders[index])}");
                var sourceManifest = Path.Combine(folders[index], "manifest", "sunder-package.json");
                Assert.Equal(File.ReadAllBytes(sourceManifest),
                    File.ReadAllBytes(Path.Combine(candidateFolder, "source", "manifest", "sunder-package.json")));
                Assert.Equal(File.ReadAllBytes(TestWorkerFixture.AssemblyPath),
                    File.ReadAllBytes(Path.Combine(candidateFolder, "runtime", "lib", $"preparation-{index}.dll")));
            }
            Assert.Equal(folders.Length, resolver.CreatedRetentions);
            Assert.Equal(folders.Length, resolver.DisposedRetentions);
        }
        finally
        {
            cancellation.Cancel();
            try { await load; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task DevPreparation_ReverseFailuresReportOnlyOwningErrorsInInputOrder()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-dev-preparation-errors");
        var folders = CreatePackages(temporary);
        var resolver = new ControlledPreparationResolver(folders.Length);
        var loader = CreateLoader(temporary, resolver, folders.Length);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var load = loader.LoadInstalledWithDevOverlaysAsync([], folders, cancellationToken: cancellation.Token);
        try
        {
            await WaitForPreparationAsync(Task.WhenAll(Enumerable.Range(0, folders.Length).Select(resolver.WaitUntilEnteredAsync)), load);
            foreach (var index in Enumerable.Range(0, folders.Length).Reverse())
            {
                resolver.Complete(index, fail: true);
                await resolver.WaitUntilCompletedAsync(index);
            }

            var result = await load;
            Assert.NotNull(result.Session);
            await using var session = result.Session;
            Assert.True(session.IsEmpty);
            Assert.Equal(Enumerable.Range(0, folders.Length).Reverse(), resolver.CompletedIndexes);
            Assert.Equal(folders.Select((folder, index) =>
                $"Failed to materialize dev package '{PackageId(folder)}': preparation-{index}"), result.Errors);
            Assert.Equal(0, resolver.ActiveCount);
            Assert.Equal(0, resolver.CreatedRetentions);
        }
        finally
        {
            cancellation.Cancel();
            try { await load; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task DevPreparation_DuplicateIdentityStillRejectsGraphAndReleasesEveryCandidate()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-dev-preparation-duplicates");
        var folders = CreatePackages(temporary, duplicateIdentity: true);
        var resolver = new ControlledPreparationResolver(folders.Length);
        var loader = CreateLoader(temporary, resolver, folders.Length);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var load = loader.LoadInstalledWithDevOverlaysAsync([], folders, cancellationToken: cancellation.Token);
        try
        {
            await WaitForPreparationAsync(Task.WhenAll(Enumerable.Range(0, folders.Length).Select(resolver.WaitUntilEnteredAsync)), load);
            var sessionFolder = FindSessionFolder(PackageId(folders[0]));
            foreach (var index in Enumerable.Range(0, folders.Length)) resolver.Complete(index);

            await Assert.ThrowsAsync<ArgumentException>(() => load);

            Assert.Equal(folders.Length, resolver.CreatedRetentions);
            Assert.Equal(folders.Length, resolver.DisposedRetentions);
            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            cancellation.Cancel();
            try { await load; }
            catch (Exception exception) when (exception is OperationCanceledException or ArgumentException) { }
        }
    }

    private static async Task WaitForPreparationAsync(Task preparation, Task<PackageSessionLoadResult> load)
    {
        if (await Task.WhenAny(preparation, load) == load)
        {
            var result = await load;
            if (result.Session is not null) await result.Session.DisposeAsync();
            Assert.Fail($"Load completed before the preparation barrier: {string.Join(" | ", result.Errors)}");
        }
        await preparation;
    }

    private static PackageSessionLoadService CreateLoader(
        TemporaryDirectoryScope temporary,
        ControlledPreparationResolver resolver,
        int concurrency)
        => new(NullLogger.Instance,
            new RuntimePackagePaths(temporary.GetPath("state")),
            ephemeralExecutionRuntimeBindingResolver: resolver,
            concurrency: new PackageSessionConcurrencyOptions { MaximumPreparationConcurrency = concurrency });

    private static string[] CreatePackages(TemporaryDirectoryScope temporary, bool duplicateIdentity = false)
    {
        var prefix = $"test.dev.{Guid.NewGuid():N}";
        return Enumerable.Range(0, 4).Select(index =>
        {
            var packageId = $"{prefix}.package{(duplicateIdentity ? 0 : index)}";
            var folder = temporary.GetPath($"dev-{index}", packageId);
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                folder, packageId, "1.0.0", typeof(PackageSessionDevPreparationConcurrencyTests).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                dependencies: [new InstalledPackageDependencyRecord("missing.package", ">=0.0.0-0")],
                runtimeEntryPoint: $"lib/preparation-{index}.dll");
            var library = Path.Combine(folder, "payload", "runtime", PackageTargetSelection.GetCurrentRuntimeIdentifier(), "lib");
            foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                var destination = Path.Combine(library, $"preparation-{index}{extension}");
                File.Move(Path.Combine(library, $"Sunder.Runtime.Host.TestWorker{extension}"), destination);
                if (extension == ".deps.json")
                {
                    File.WriteAllText(destination, File.ReadAllText(destination)
                        .Replace("Sunder.Runtime.Host.TestWorker", $"preparation-{index}", StringComparison.Ordinal));
                }
            }
            CanonicalPackageTestBuilder.WriteContentIndex(folder);
            return folder;
        }).ToArray();
    }

    private static string PackageId(string folder) => Path.GetFileName(folder);

    private static string FindSessionFolder(string firstPackageId)
        => Directory.EnumerateDirectories(Path.Combine(Path.GetTempPath(), "Sunder.Runtime.Host", "V1", "installed-sessions"))
            .Single(folder => Directory.Exists(Path.Combine(folder, $"00-{firstPackageId}")));

    private sealed class ControlledPreparationResolver(int packageCount) : IEphemeralExecutionRuntimeBindingResolver
    {
        private readonly TaskCompletionSource[] _entered = Enumerable.Range(0, packageCount)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource<bool>[] _release = Enumerable.Range(0, packageCount)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource[] _completed = Enumerable.Range(0, packageCount)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly ConcurrentQueue<int> _completedIndexes = new();
        private readonly object _sync = new();
        private int _activeCount;
        private int _maximumActiveCount;
        private int _callCount;
        private int _createdRetentions;
        private int _disposedRetentions;

        public int ActiveCount { get { lock (_sync) return _activeCount; } }
        public int MaximumActiveCount { get { lock (_sync) return _maximumActiveCount; } }
        public int CallCount { get { lock (_sync) return _callCount; } }
        public int CreatedRetentions => Volatile.Read(ref _createdRetentions);
        public int DisposedRetentions => Volatile.Read(ref _disposedRetentions);
        public IReadOnlyList<int> CompletedIndexes => _completedIndexes.ToArray();

        public Task WaitUntilEnteredAsync(int index) => _entered[index].Task.WaitAsync(TimeSpan.FromSeconds(10));
        public Task WaitUntilCompletedAsync(int index) => _completed[index].Task.WaitAsync(TimeSpan.FromSeconds(10));
        public void Complete(int index, bool fail = false) => _release[index].TrySetResult(fail);

        public async Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveEphemeralBindingAsync(
            SunderPackageTargetKey targetKey,
            SunderPackageTargetManifest target,
            CancellationToken cancellationToken)
        {
            var index = int.Parse(Path.GetFileNameWithoutExtension(target.EntryPoint!)["preparation-".Length..]);
            lock (_sync)
            {
                _activeCount++;
                _callCount++;
                _maximumActiveCount = Math.Max(_maximumActiveCount, _activeCount);
            }
            _entered[index].TrySetResult();
            try
            {
                var fail = await _release[index].Task.WaitAsync(cancellationToken);
                if (fail) throw new InvalidOperationException($"preparation-{index}");
                var synthesized = await TestExecutionRuntimeLaunchResolver.Instance.ResolveEphemeralBindingAsync(
                    targetKey, target, cancellationToken);
                await synthesized.DisposeAsync();
                Interlocked.Increment(ref _createdRetentions);
                return new ResolvedEphemeralExecutionRuntimeBinding(synthesized.Binding,
                    new CallbackRetention(() => Interlocked.Increment(ref _disposedRetentions)));
            }
            finally
            {
                lock (_sync) _activeCount--;
                _completedIndexes.Enqueue(index);
                _completed[index].TrySetResult();
            }
        }
    }

    private sealed class CallbackRetention(Action dispose) : IAsyncDisposable
    {
        private Action? _dispose = dispose;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
