using Avalonia;
using Avalonia.Controls;

namespace Sunder.App.Views.Controls;

internal sealed class PackageIconBarDragLayoutSnapshot
{
    public PackageIconBarDragLayoutSnapshot(Border[] orderedHosts, double[] midpoints, Point[] slotCenters)
    {
        OrderedHosts = orderedHosts;
        Midpoints = midpoints;
        SlotCenters = slotCenters;
    }

    public Border[] OrderedHosts { get; }

    public double[] Midpoints { get; }

    public Point[] SlotCenters { get; }

    public int GetInsertIndex(Point rootPosition, bool isHorizontal)
    {
        var axis = isHorizontal ? rootPosition.X : rootPosition.Y;
        var index = 0;
        while (index < Midpoints.Length && axis >= Midpoints[index])
        {
            index++;
        }

        return index;
    }

    public bool TryGetSlotCenter(int insertIndex, out Point center)
    {
        if ((uint)insertIndex < (uint)SlotCenters.Length)
        {
            center = SlotCenters[insertIndex];
            return true;
        }

        center = default;
        return false;
    }

    public bool TryGetPreviewAnchor(int insertIndex, out Border? host, out bool insertAfter)
    {
        if (OrderedHosts.Length == 0)
        {
            host = null;
            insertAfter = false;
            return insertIndex == 0;
        }

        if (insertIndex < 0 || insertIndex > OrderedHosts.Length)
        {
            host = null;
            insertAfter = false;
            return false;
        }

        if (insertIndex < OrderedHosts.Length)
        {
            host = OrderedHosts[insertIndex];
            insertAfter = false;
            return true;
        }

        host = OrderedHosts[^1];
        insertAfter = true;
        return true;
    }
}
