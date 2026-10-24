using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Sunder.App.Views.Controls;

// MainWindowLayoutController owns shell geometry, clamps and persistence. The
// native GridSplitter must not also write definitions behind that model's back.
public sealed class ShellGridSplitter : GridSplitter
{
    protected override Type StyleKeyOverride => typeof(GridSplitter);

    protected override void OnDragStarted(VectorEventArgs e) { }

    protected override void OnDragDelta(VectorEventArgs e) { }

    protected override void OnDragCompleted(VectorEventArgs e) { }

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
