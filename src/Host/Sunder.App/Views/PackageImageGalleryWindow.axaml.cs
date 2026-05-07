using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sunder.App.ViewModels;

namespace Sunder.App.Views;

public partial class PackageImageGalleryWindow : Window
{
    private readonly IReadOnlyList<RegistryPackageMediaItemViewModel> _media;
    private int _selectedIndex;

    public PackageImageGalleryWindow()
    {
        InitializeComponent();
        _media = [];
    }

    public PackageImageGalleryWindow(
        IReadOnlyList<RegistryPackageMediaItemViewModel> media,
        int selectedIndex)
        : this()
    {
        _media = media;
        _selectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, media.Count - 1));
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowState = WindowState.Maximized;
        await ShowSelectedImageAsync();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left:
                await ShowPreviousImageAsync();
                e.Handled = true;
                break;
            case Key.Right:
                await ShowNextImageAsync();
                e.Handled = true;
                break;
        }
    }

    private void Backdrop_OnPointerPressed(object? sender, PointerPressedEventArgs e) => Close();

    private void GalleryContent_OnPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();

    private async void PreviousButton_OnClick(object? sender, RoutedEventArgs e) => await ShowPreviousImageAsync();

    private async void NextButton_OnClick(object? sender, RoutedEventArgs e) => await ShowNextImageAsync();

    private async Task ShowPreviousImageAsync()
    {
        if (_media.Count == 0)
        {
            return;
        }

        _selectedIndex = (_selectedIndex - 1 + _media.Count) % _media.Count;
        await ShowSelectedImageAsync();
    }

    private async Task ShowNextImageAsync()
    {
        if (_media.Count == 0)
        {
            return;
        }

        _selectedIndex = (_selectedIndex + 1) % _media.Count;
        await ShowSelectedImageAsync();
    }

    private async Task ShowSelectedImageAsync()
    {
        if (_media.Count == 0)
        {
            Close();
            return;
        }

        var media = _media[_selectedIndex];
        await media.EnsureImageLoadedAsync();
        GalleryImage.Source = media.Image;
        CaptionText.Text = media.Caption;
        CountText.Text = $"{_selectedIndex + 1} / {_media.Count} · {FormatFileSize(media.Size)}";
        PreviousButton.IsVisible = _media.Count > 1;
        NextButton.IsVisible = _media.Count > 1;
    }

    private static string FormatFileSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
