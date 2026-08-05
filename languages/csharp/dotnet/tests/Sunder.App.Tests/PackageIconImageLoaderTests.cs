using Avalonia;
using Avalonia.Media;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageIconImageLoaderTests
{
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("image/vnd.microsoft.icon")]
    public void ResolveIconFormat_TreatsImageContentTypesAsRaster(string contentType)
    {
        Assert.Equal(
            PackageIconImageLoader.PackageIconImageFormat.Raster,
            PackageIconImageLoader.ResolveIconFormat(contentType));
    }

    [Fact]
    public void ResolveIconFormat_RejectsSvgContentTypeWithoutASanitizer()
    {
        Assert.Equal(
            PackageIconImageLoader.PackageIconImageFormat.Unsupported,
            PackageIconImageLoader.ResolveIconFormat("image/svg+xml"));
    }

    [Fact]
    public void ResolveIconFormatFromPath_RejectsSvgzWithoutASanitizer()
    {
        Assert.Equal(
            PackageIconImageLoader.PackageIconImageFormat.Unsupported,
            PackageIconImageLoader.ResolveIconFormatFromPath("assets/icon.SVGZ"));
    }

    [Theory]
    [InlineData("file:///tmp/icon.png")]
    [InlineData("ftp://cdn.example/icon.png")]
    [InlineData("data:image/png;base64,AA==")]
    [InlineData("https://user:secret@cdn.example/icon.png")]
    public void HttpMediaUriValidator_RejectsNonHttpOrCredentialBearingMedia(string value)
        => Assert.False(HttpMediaUriValidator.IsValid(new Uri(value)));

    [Fact]
    public void HttpMediaUriValidator_RequiresAuthenticatedRuntimeOriginForRuntimeAssets()
    {
        var connection = new RuntimeConnectionInfo(new Uri("https://runtime.example:8443/root/"), "secret");

        Assert.True(HttpMediaUriValidator.IsRuntimeAsset(
            new Uri("https://runtime.example:8443/root/api/v1/packages/agent/assets/icon.png"),
            connection));
        Assert.False(HttpMediaUriValidator.IsRuntimeAsset(
            new Uri("https://cdn.example/icon.png"),
            connection));
        Assert.False(HttpMediaUriValidator.IsRuntimeAsset(
            new Uri("http://runtime.example:8443/root/api/v1/packages/agent/assets/icon.png"),
            connection));
    }

    [Fact]
    public async Task LoadAnonymousMediaAsync_RejectsFileUrls()
    {
        var result = await PackageIconImageLoader.LoadAnonymousMediaAsync(new Uri("file:///tmp/icon.png"));

        Assert.Null(result.Image);
        Assert.Contains("HTTP or HTTPS", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadFileAsync_RejectsSvgzIcon()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "icon.svgz");
        try
        {
            await File.WriteAllTextAsync(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" />");

            var result = await PackageIconImageLoader.LoadFileAsync(path);

            Assert.Null(result.Image);
            Assert.Contains("Unsupported", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    public void ResolveIconFormat_RejectsNonImageContentTypes(string? contentType)
    {
        Assert.Equal(
            PackageIconImageLoader.PackageIconImageFormat.Unsupported,
            PackageIconImageLoader.ResolveIconFormat(contentType));
    }

    [Fact]
    public async Task LoadAsync_WhenCancelled_DoesNotConvertCancellationToFailedResult()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PackageIconImageLoader.LoadAsync(new Uri("https://example.invalid/icon.png"), cancellationTokenSource.Token));
    }

    [Fact]
    public async Task PackageIconCache_CoalescesConcurrentLoadsForOneGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "icon.png"), [1, 2, 3]);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        using var cache = new PackageIconCache(
            _ => root,
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref loadCount);
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
                return PackageIconImageLoadResult.Failed("test fallback");
            });
        var icon = new PackageIconDescriptor(null, "icon.png");
        var runtimeInstanceId = Guid.NewGuid();

        var first = cache.PrewarmAsync(runtimeInstanceId, 4, [("agent", icon)], CancellationToken.None);
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = cache.PrewarmAsync(runtimeInstanceId, 4, [("agent", icon)], CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref loadCount));
        releaseLoad.SetResult();
        await Task.WhenAll(first, second);
        Assert.Null(cache.GetImage("agent", icon));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PackageIconCache_CandidateIsInvisibleUntilPublishAndRetiredImageIsReleased()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "icon.png"), [1, 2, 3]);
        var images = new Queue<TrackedImage>([new TrackedImage(), new TrackedImage()]);
        using var cache = new PackageIconCache(
            _ => root,
            (_, _) => Task.FromResult(PackageIconImageLoadResult.Success(images.Dequeue())));
        var icon = new PackageIconDescriptor(null, "icon.png");
        var runtimeInstanceId = Guid.NewGuid();

        await cache.PrewarmAsync(runtimeInstanceId, 1, [("agent", icon)], CancellationToken.None);
        var currentImage = Assert.IsType<TrackedImage>(cache.GetImage("agent", icon));
        var candidate = await cache.PrepareGenerationAsync(
            runtimeInstanceId,
            2,
            [("agent", icon)],
            _ => root,
            CancellationToken.None);

        Assert.Same(currentImage, cache.GetImage("agent", icon));
        Assert.False(currentImage.IsDisposed);

        var retired = cache.Publish(candidate);
        var replacementImage = Assert.IsType<TrackedImage>(cache.GetImage("agent", icon));
        Assert.NotSame(currentImage, replacementImage);
        retired.Dispose();

        Assert.True(currentImage.IsDisposed);
        Assert.False(replacementImage.IsDisposed);
        Directory.Delete(root, recursive: true);
    }

    private sealed class TrackedImage : IImage, IDisposable
    {
        public Size Size => new(1, 1);

        public bool IsDisposed { get; private set; }

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }

        public void Dispose() => IsDisposed = true;
    }
}
