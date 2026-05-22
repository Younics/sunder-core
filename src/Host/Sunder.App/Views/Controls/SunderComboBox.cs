using Avalonia.Controls;
using Avalonia.Input;

namespace Sunder.App.Views.Controls;

public sealed class SunderComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!IsDropDownOpen)
        {
            return;
        }

        base.OnPointerWheelChanged(e);
    }
}
