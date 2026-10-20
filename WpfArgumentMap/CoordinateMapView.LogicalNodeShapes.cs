using System.Windows;
using System.Windows.Media;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    private static readonly Pen RelationNodePen=MakePen("#0891B2",1.8);
    private static readonly Pen PendingRelationNodePen=MakePen("#0E7490",2.2);
    private static readonly Brush RelationNodeFill=MakeBrush("#160891B2");
    private static readonly Pen InstanceNodePen=MakePen("#B45309",1.8);
    private static readonly Brush InstanceNodeFill=MakeBrush("#16D97706");
    private static Brush NodeFill(int size)=>size==10?InstanceNodeFill:size==8?RelationNodeFill:LogicalNodeFill;
    private static Pen NodePen(int size,bool pending=false)=>size==10?InstanceNodePen:size==8?(pending?PendingRelationNodePen:RelationNodePen):(pending?PendingLogicalNodePen:LogicalNodePen);
    internal static StreamGeometry InstanceOutline(Rect b,double radius)
    {
        var r=Math.Min(radius,Math.Min(b.Width,b.Height)/2);var geometry=new StreamGeometry();
        using(var c=geometry.Open())
        {
            c.BeginFigure(new(b.Left+r,b.Top),true,true);
            c.LineTo(new(b.Right-r,b.Top),true,false);c.ArcTo(new(b.Right,b.Top+r),new(r,r),0,false,SweepDirection.Counterclockwise,true,false);
            c.LineTo(new(b.Right,b.Bottom-r),true,false);c.ArcTo(new(b.Right-r,b.Bottom),new(r,r),0,false,SweepDirection.Counterclockwise,true,false);
            c.LineTo(new(b.Left+r,b.Bottom),true,false);c.ArcTo(new(b.Left,b.Bottom-r),new(r,r),0,false,SweepDirection.Counterclockwise,true,false);
            c.LineTo(new(b.Left,b.Top+r),true,false);c.ArcTo(new(b.Left+r,b.Top),new(r,r),0,false,SweepDirection.Counterclockwise,true,false);
        }
        geometry.Freeze();return geometry;
    }
    internal static Point[] ChamferPoints(Rect bounds,double cut)=>
    [new(bounds.Left+cut,bounds.Top),new(bounds.Right-cut,bounds.Top),new(bounds.Right,bounds.Top+cut),new(bounds.Right,bounds.Bottom-cut),
     new(bounds.Right-cut,bounds.Bottom),new(bounds.Left+cut,bounds.Bottom),new(bounds.Left,bounds.Bottom-cut),new(bounds.Left,bounds.Top+cut)];
    private void DrawNodeShape(DrawingContext dc,int size,Brush? fill,Pen pen,Rect bounds,double? corner=null)
    {
        var cut=corner??.5*Scale;
        if(size==10){dc.DrawGeometry(fill,pen,InstanceOutline(bounds,cut));return;}
        if(size!=8){dc.DrawRoundedRectangle(fill,pen,bounds,cut,cut);return;}
        var points=ChamferPoints(bounds,Math.Min(cut,Math.Min(bounds.Width,bounds.Height)/2));var geometry=new StreamGeometry();
        using(var context=geometry.Open())
        {context.BeginFigure(points[0],true,true);context.PolyLineTo(points.Skip(1).ToArray(),true,false);}
        geometry.Freeze();dc.DrawGeometry(fill,pen,geometry);
    }
}
