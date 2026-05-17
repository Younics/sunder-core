using Avalonia.Controls;
using Avalonia.Input;

namespace Sunder.App.Views;

internal sealed class SecondaryWindowLifecycleController
{
    private readonly Window _window;
    private readonly Action _persistWindowState;
    private readonly Action _closed;
    private bool _allowClose;

    public SecondaryWindowLifecycleController(
        Window window,
        Action persistWindowState,
        Action closed)
    {
        _window = window;
        _persistWindowState = persistWindowState;
        _closed = closed;
        _window.KeyDown += OnKeyDown;
        _window.Closing += OnClosing;
        _window.Closed += OnClosed;
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        _window.Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _window.Close();
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            _persistWindowState();
            return;
        }

        e.Cancel = true;
        _persistWindowState();
        _window.Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _persistWindowState();
        _closed();
    }
}
