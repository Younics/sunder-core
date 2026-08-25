using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Avalonia;
using Avalonia.Media;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RemoteImageServiceTests
{
    private static readonly byte[] MinimalPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public void ExternalAddressPolicy_RejectsNonPublicAddresses(string value)
        => Assert.False(ExternalImageNetworkGuard.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void ExternalAddressPolicy_AcceptsPublicAddresses(string value)
        => Assert.True(ExternalImageNetworkGuard.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("https://media.example/icon.png")]
    [InlineData("//media.example/icon.png")]
    [InlineData("https://registry.example.evil/icon.png")]
    public void RegistryImageUriResolver_RejectsCrossOriginMedia(string value)
        => Assert.False(RemoteImageService.TryResolveRegistryImageUri(
            new Uri("https://registry.example/"),
            value,
            out _));

    [Fact]
    public async Task ExternalConnector_RejectsRebindingOutcomeBeforeConnecting()
    {
        var answers = new Queue<IPAddress[]>(
        [
            [IPAddress.Parse("8.8.8.8")],
            [IPAddress.Parse("127.0.0.1")],
        ]);
        var connectCount = 0;

        Task<IPAddress[]> Resolve(string _, CancellationToken __) => Task.FromResult(answers.Dequeue());
        ValueTask<Stream> Connect(IPAddress _, int __, CancellationToken ___)
        {
            connectCount++;
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }

        await using var first = await ExternalImageNetworkGuard.ConnectAsync(
            new DnsEndPoint("avatar.example", 443),
            Resolve,
            Connect,
            CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await ExternalImageNetworkGuard.ConnectAsync(
                new DnsEndPoint("avatar.example", 443),
                Resolve,
                Connect,
                CancellationToken.None));
        Assert.Equal(1, connectCount);
    }

    [Fact]
    public async Task LoadAvatarAsync_RejectsPrivateLiteralBeforeSending()
    {
        var sendCount = 0;
        using var service = CreateService(
            _ => CreateImageResponse(MinimalPng),
            _ =>
            {
                sendCount++;
                return CreateImageResponse(MinimalPng);
            });

        var result = await service.LoadAvatarAsync(
            new Uri("https://registry.example/"),
            new Uri("http://127.0.0.1/avatar.png"));

        Assert.Null(result.Image);
        Assert.NotNull(result.Error);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task LoadRegistryIconAsync_RejectsCrossOriginRedirect()
    {
        var sendCount = 0;
        using var service = CreateService(request =>
        {
            sendCount++;
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://media.example/icon.png") },
            };
        });

        var result = await service.LoadRegistryIconAsync(
            new Uri("https://registry.example/"),
            new Uri("https://registry.example/icon.png"));

        Assert.Null(result.Image);
        Assert.NotNull(result.Error);
        Assert.Equal(1, sendCount);
    }

    [Fact]
    public async Task LoadRegistryIconAsync_FollowsBoundedSameOriginRedirect()
    {
        var requests = new List<Uri>();
        using var service = CreateService(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath == "/icon.png"
                ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("/media/icon.png", UriKind.Relative) },
                }
                : CreateImageResponse(MinimalPng);
        });

        var result = await service.LoadRegistryIconAsync(
            new Uri("https://registry.example/"),
            new Uri("https://registry.example/icon.png"));

        using var image = Assert.IsType<TrackedImage>(result.Image);
        Assert.Null(result.Error);
        Assert.Equal(
            [new Uri("https://registry.example/icon.png"), new Uri("https://registry.example/media/icon.png")],
            requests);
    }

    [Fact]
    public async Task LoadRegistryIconAsync_RejectsDeclaredOversizedContent()
    {
        var bytes = new byte[(1024 * 1024) + 1];
        using var service = CreateService(_ => CreateImageResponse(bytes));

        var result = await service.LoadRegistryIconAsync(
            new Uri("https://registry.example/"),
            new Uri("https://registry.example/icon.png"));

        Assert.Null(result.Image);
        Assert.Contains("byte limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadRegistryIconAsync_RejectsStreamingOversizedContentWithoutLength()
    {
        var bytes = new byte[(1024 * 1024) + 1];
        using var service = CreateService(_ => CreateImageResponse(
            new StreamContent(new NonSeekableReadStream(new MemoryStream(bytes)))));

        var result = await service.LoadRegistryIconAsync(
            new Uri("https://registry.example/"),
            new Uri("https://registry.example/icon.png"));

        Assert.Null(result.Image);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task LoadRegistryIconAsync_RejectsDecodedPixelBombBeforeDecode()
    {
        var decodeCalled = false;
        using var service = CreateService(
            _ => CreateImageResponse(CreatePngHeader(10_000, 10_000)),
            decode: (_, _, _) =>
            {
                decodeCalled = true;
                return new TrackedImage();
            });

        var result = await service.LoadRegistryIconAsync(
            new Uri("https://registry.example/"),
            new Uri("https://registry.example/icon.png"));

        Assert.Null(result.Image);
        Assert.False(decodeCalled);
        Assert.Contains("dimensions", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadRegistryIconAsync_PropagatesCallerCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateImageResponse(MinimalPng);
        });
        using var cancellation = new CancellationTokenSource();
        var load = service.LoadRegistryIconAsync(
            new Uri("https://registry.example/"),
            new Uri("https://registry.example/icon.png"),
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    [Fact]
    public async Task RemoteImageService_DoesNotPersistImagesBetweenLoads()
    {
        var sendCount = 0;
        var decodeCount = 0;
        using var service = CreateService(
            _ =>
            {
                sendCount++;
                return CreateImageResponse(MinimalPng);
            },
            decode: (_, metadata, displaySize) =>
            {
                decodeCount++;
                Assert.Equal(1, metadata.Width);
                Assert.Equal(128, displaySize.Width);
                Assert.Equal(128, displaySize.Height);
                return new TrackedImage();
            });
        var origin = new Uri("https://registry.example/");
        var imageUri = new Uri("https://registry.example/icon.png");

        var first = await service.LoadRegistryIconAsync(origin, imageUri);
        var second = await service.LoadRegistryIconAsync(origin, imageUri);

        Assert.NotSame(first.Image, second.Image);
        Assert.Equal(2, sendCount);
        Assert.Equal(2, decodeCount);
        (first.Image as IDisposable)?.Dispose();
        (second.Image as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task MediaItem_DoesNotLoadUntilRequestedAndCoalescesRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;
        using var media = new RegistryPackageMediaItemViewModel(
            CreateMedia(0),
            _ => Task.CompletedTask,
            async cancellationToken =>
            {
                loadCount++;
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return RemoteImageLoadResult.Success(new TrackedImage());
            },
            ImmediateUiDispatcher.Instance);

        Assert.Equal(0, loadCount);

        var first = media.EnsureImageLoadedAsync();
        var second = media.EnsureImageLoadedAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, loadCount);

        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.NotNull(media.Image);
    }

    [Fact]
    public void MarketplaceProfile_CapsAcceptedMediaEntries()
    {
        using var profile = new MarketplacePackageProfileViewModel();
        var origin = new Uri("https://registry.example/");
        var media = Enumerable.Range(0, RemoteImageService.MaxAcceptedMediaEntries + 5)
            .Select(CreateMedia)
            .ToArray();

        profile.Apply(new RegistryPackageProfile(
            "agent",
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            media,
            DateTimeOffset.UtcNow), origin);

        Assert.Equal(RemoteImageService.MaxAcceptedMediaEntries, profile.Media.Count);
    }

    private static RemoteImageService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> registryResponse,
        Func<HttpRequestMessage, HttpResponseMessage>? externalResponse = null,
        Func<byte[], RemoteImageMetadata, RemoteImageDisplaySize, IImage>? decode = null)
        => CreateService(
            (request, _) => Task.FromResult(registryResponse(request)),
            externalResponse is null
                ? null
                : (request, _) => Task.FromResult(externalResponse(request)),
            decode);

    private static RemoteImageService CreateService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> registryResponse,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? externalResponse = null,
        Func<byte[], RemoteImageMetadata, RemoteImageDisplaySize, IImage>? decode = null)
        => new(
            new StubHttpMessageHandler(registryResponse),
            new StubHttpMessageHandler(externalResponse
                ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))),
            decode ?? ((_, _, _) => new TrackedImage()),
            TimeSpan.FromSeconds(10));

    private static HttpResponseMessage CreateImageResponse(byte[] content)
        => CreateImageResponse(new ByteArrayContent(content));

    private static HttpResponseMessage CreateImageResponse(HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        var content = new byte[24];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(content, 0);
        "IHDR"u8.CopyTo(content.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(20, 4), (uint)height);
        return content;
    }

    private static RegistryPackageMedia CreateMedia(int index)
        => new(
            Guid.NewGuid(),
            $"image-{index}.png",
            "image/png",
            MinimalPng.Length,
            $"Image {index}",
            index,
            $"https://registry.example/media/image-{index}.png");

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await sendAsync(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class NonSeekableReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TrackedImage : IImage, IDisposable
    {
        public Size Size => new(1, 1);
        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
        public void Dispose()
        {
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public static ImmediateUiDispatcher Instance { get; } = new();
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
