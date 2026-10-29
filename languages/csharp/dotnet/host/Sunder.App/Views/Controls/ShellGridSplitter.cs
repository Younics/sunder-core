using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Sunder.App.Views.Controls;

// MainWindowLayoutController owns shell geometry, clamps and persistence. The
// native GridSplitter must not also write definitions behind that model's back.
public sealed class ShellGridSplitter : GridSplitter
{
    private TopLevel? _dragRoot;
    private Point? _lastPointerPosition;
    private Vector? _pointerDelta;

    protected override Type StyleKeyOverride => typeof(GridSplitter);

    protected override void OnDragStarted(VectorEventArgs e)
    {
        _dragRoot = TopLevel.GetTopLevel(this);
        _lastPointerPosition = _dragRoot is null ? null : this.TranslatePoint(new Point(e.Vector.X, e.Vector.Y), _dragRoot);
    }

    protected override void OnDragDelta(VectorEventArgs e) { }

    internal Vector GetWorkspaceDelta(Vector fallback) => _pointerDelta ?? fallback;

    protected override void OnDragCompleted(VectorEventArgs e)
    {
        _dragRoot = null;
        _lastPointerPosition = null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragRoot is { } root && _lastPointerPosition is { } previous)
        {
            var position = e.GetPosition(root);
            _pointerDelta = position - previous;
            _lastPointerPosition = position;
        }
        try { base.OnPointerMoved(e); }
        finally { _pointerDelta = null; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var delta = e.Key switch
        {
            Key.Left when ResizeDirection == GridResizeDirection.Columns => new Avalonia.Vector(-KeyboardIncrement, 0),
            Key.Right when ResizeDirection == GridResizeDirection.Columns => new Avalonia.Vector(KeyboardIncrement, 0),
            Key.Up when ResizeDirection == GridResizeDirection.Rows => new Avalonia.Vector(0, -KeyboardIncrement),
            Key.Down when ResizeDirection == GridResizeDirection.Rows => new Avalonia.Vector(0, KeyboardIncrement),
            _ => default,
        };
        if (delta == default)
        {
            base.OnKeyDown(e);
            return;
        }

        RaiseEvent(new VectorEventArgs { RoutedEvent = DragDeltaEvent, Vector = delta });
        RaiseEvent(new VectorEventArgs { RoutedEvent = DragCompletedEvent, Vector = delta });
        e.Handled = true;
    }
}
