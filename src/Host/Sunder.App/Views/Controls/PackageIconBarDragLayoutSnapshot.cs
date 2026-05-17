using Avalonia;

namespace Sunder.App.Views.Controls;

internal sealed class PackageIconBarDragLayoutSnapshot(double[] midpoints)
{
    public int GetInsertIndex(Point rootPosition, bool isHorizontal)
    {
        var axis = isHorizontal ? rootPosition.X : rootPosition.Y;
        var index = 0;
        while (index < midpoints.Length && axis >= midpoints[index])
        {
            index++;
        }

        return index;
    }
}
