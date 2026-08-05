using Avalonia;
using Avalonia.Controls;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App.Views.Controls;

public partial class PackageDragOverlay : UserControl
{
    private readonly PackageDragGhostController _packageDragGhostController;

    public PackageDragOverlay()
    {
        InitializeComponent();
        _packageDragGhostController = new PackageDragGhostController(PackageDragGhost, PackageDragGhostIcon);
    }

    public void ShowPackageDragGhost(ShellItemViewModel item, bool compact, Point centerPosition)
        => _packageDragGhostController.Show(item, compact, centerPosition);

    public void MovePackageDragGhost(Point centerPosition)
        => _packageDragGhostController.Move(centerPosition);

    public void HidePackageDragGhost()
        => _packageDragGhostController.Hide();
}
