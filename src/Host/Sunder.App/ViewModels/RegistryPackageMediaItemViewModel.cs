using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryPackageMediaItemViewModel : ViewModelBase, IDisposable
{
    private const long MaxMediaImageBytes = 8_388_608;
    private static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim ImageLoadSemaphore = new(2, 2);
    private readonly Func<RegistryPackageMediaItemViewModel, Task> _onSelectAsync;
    private Task? _loadTask;

    public RegistryPackageMediaItemViewModel(
        RegistryPackageMedia media,
        Func<RegistryPackageMediaItemViewModel, Task> onSelectAsync)
    {
        Url = media.Url;
        Caption = media.AltText ?? media.FileName;
        FileName = media.FileName;
        Size = media.Size;
        _onSelectAsync = onSelectAsync;
        _ = EnsureImageLoadedAsync();
    }

    public string Url { get; }

    public string Caption { get; }

    public string FileName { get; }

    public long Size { get; }

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isImageLoading;

    [RelayCommand]
    private async Task SelectAsync()
    {
        await EnsureImageLoadedAsync();
        await _onSelectAsync(this);
    }

    public async Task EnsureImageLoadedAsync(CancellationToken cancellationToken = default)
    {
        _loadTask ??= LoadImageAsync(cancellationToken);
        await _loadTask;
    }

    public void Dispose()
    {
        Image?.Dispose();
        Image = null;
    }

    private async Task LoadImageAsync(CancellationToken cancellationToken)
    {
        var semaphoreAcquired = false;
        await SetImageLoadingAsync(true);
        try
        {
            await ImageLoadSemaphore.WaitAsync(cancellationToken);
            semaphoreAcquired = true;
            using var response = await ImageHttpClient.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxMediaImageBytes)
            {
                await UiThread.InvokeAsync(() => Image = null);
                return;
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = await ReadBoundedImageAsync(source, MaxMediaImageBytes, cancellationToken);
            memory.Position = 0;
            var bitmap = new Bitmap(memory);
            await UiThread.InvokeAsync(() => Image = bitmap);
        }
        catch
        {
            await UiThread.InvokeAsync(() => Image = null);
        }
        finally
        {
            if (semaphoreAcquired)
            {
                ImageLoadSemaphore.Release();
            }

            await SetImageLoadingAsync(false);
        }
    }

    private static async Task<MemoryStream> ReadBoundedImageAsync(
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var memory = new MemoryStream();
        var buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return memory;
            }

            totalBytes += bytesRead;
            if (totalBytes > maxBytes)
            {
                memory.Dispose();
                throw new InvalidOperationException($"Image content exceeds the {maxBytes} byte limit.");
            }

            memory.Write(buffer, 0, bytesRead);
        }
    }

    private Task SetImageLoadingAsync(bool value)
        => UiThread.InvokeAsync(() => IsImageLoading = value);
}
