using Avalonia;
using Avalonia.Controls;
using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.Views;

internal sealed class MainWindowStateController : IDisposable
{
    private readonly Window _window;
    private readonly ShellState _state;
    private readonly ShellStateService _service;
    private readonly Action _captureLayout;
    private readonly ShellWindowPlacementTracker _tracker;
    private bool _enabled;
    private bool _closingOrHidden;

    public MainWindowStateController(Window window, ShellState state, ShellStateService service, Action captureLayout)
    {
        _window = window;
        _state = state;
        _service = service;
        _captureLayout = captureLayout;
        // Seed normal bounds before applying a saved maximized/fullscreen mode.
        var initial = state.MainWindowPlacement ?? ShellWindowPlacementService.Capture(window);
        ShellWindowPlacementService.Apply(window, state.MainWindowPlacement);
        _tracker = new ShellWindowPlacementTracker(window, initial);
        window.PositionChanged += OnPositionChanged;
        window.Resized += OnResized;
        window.PropertyChanged += OnPropertyChanged;
    }

    public void EnablePersistence()
    {
        _enabled = true;
        RecordPlacement();
    }

    public void CaptureForClose()
    {
        if (!_enabled || _closingOrHidden) return;
        _state.MainWindowPlacement = _tracker.Capture();
        // macOS may now exit fullscreen to hide the window. That transition is
        // not a user change to the saved reopen mode.
        _closingOrHidden = true;
        _captureLayout();
    }

    public void RestoreForReopen()
    {
        if (!_closingOrHidden || _window.IsVisible) return;
        ShellWindowPlacementService.Apply(_window, _state.MainWindowPlacement);
        _tracker.Reset(_state.MainWindowPlacement);
        _closingOrHidden = false;
    }

    public void CancelClose()
    {
        _closingOrHidden = false;
        RecordPlacement();
    }

    public void SaveImmediately()
    {
        if (!_enabled) return;
        CaptureForClose();
        _captureLayout();
        _service.Flush(_state);
    }

    public Task FlushAsync()
    {
        if (!_enabled) return Task.CompletedTask;
        CaptureForClose();
        _captureLayout();
        return _service.FlushAsync(_state);
    }

    private void RecordPlacement()
    {
        if (!_enabled || _closingOrHidden || !_window.IsVisible) return;
        _state.MainWindowPlacement = _tracker.Capture();
        _captureLayout();
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => RecordPlacement();
    private void OnResized(object? sender, WindowResizedEventArgs e) => RecordPlacement();
    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Window.BoundsProperty) RecordPlacement();
    }

    public void Dispose()
    {
        _window.PositionChanged -= OnPositionChanged;
        _window.Resized -= OnResized;
        _window.PropertyChanged -= OnPropertyChanged;
        _tracker.Dispose();
    }
}
