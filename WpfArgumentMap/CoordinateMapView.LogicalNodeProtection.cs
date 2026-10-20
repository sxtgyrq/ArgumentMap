using System.Windows;
using System.Windows.Media;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    public bool ShowLogicalNodeProtection { get; private set; }
    private static readonly Pen LogicalNodeProtectionPen = MakeLogicalNodeProtectionPen();

    public void SetShowLogicalNodeProtection(bool value)
    {
        if (ShowLogicalNodeProtection == value) return;
        ShowLogicalNodeProtection = value;
        Refresh();
    }

    // A screen overlay only: never used by hit testing, selection bounds, routing,
    // validation, fit-to-map bounds, transport contracts or database operations.
    private void DrawLogicalNodeProtectionAreas(DrawingContext dc, Rect plot)
    {
        if (!ShowLogicalNodeProtection) return;
        var drawn = new HashSet<(int Size, Point Center)>();
        void Draw(int sideLength, Point worldCenter)
        {
            if (!drawn.Add((sideLength, worldCenter))) return;
            var center = ToScreen(worldCenter);
            var side = (sideLength * 3 - 1) * Scale;
            var bounds = new Rect(center.X - side / 2, center.Y - side / 2, side, side);
            if (bounds.IntersectsWith(plot)) dc.DrawRectangle(null, LogicalNodeProtectionPen, bounds);
        }
        foreach (var node in logicalNodes.Nodes) Draw(node.SideLength, new((double)node.X, (double)node.Y));

        // Use the same centers as each preview, without creating extra LN objects.
        if ((IsPlacingLogicalNode || IsMovingLogicalNode) && logicalNodes.Mode == LogicalNodeMode.Ready &&
            LogicalNodeEditor.Candidate(PointerPosition) is {} candidate)
            Draw(logicalNodes.Moving?.SideLength ?? LogicalNodePlacementSize, candidate);
        if (IsAreaMoveMode && areaMove.Selection is {} selection)
        {
            var delta = AreaDelta();
            foreach (var node in selection.LogicalNodes) Draw(node.SideLength, new((double)node.X + delta.X, (double)node.Y + delta.Y));
        }
        if (IsFlowDiagramImportActive && flowPreview is {} preview)
            foreach (var node in preview.LogicalNodes) Draw(node.SideLength, new((double)node.X, (double)node.Y));
        if (pastedArea is {} source && PointerPosition is {} pointer && AreaPasteDelta(pointer) is {} shift)
            foreach (var node in source.LogicalNodes) Draw(node.SideLength, new((double)node.X + shift.X, (double)node.Y + shift.Y));
    }

    private static Pen MakeLogicalNodeProtectionPen()
    {
        var pen = new Pen(MakeBrush("#647D9C", .7), 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }
}
