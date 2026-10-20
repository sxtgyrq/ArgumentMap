using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WpfArgumentMap;

public sealed partial class CoordinateMapView
{
    private bool DrawLineSpeed(DrawingContext dc,int speed,Point anchor,Vector normal,Rect plot,Brush brush)
    {
        var text=CreateText(speed.ToString(CultureInfo.InvariantCulture));text.SetForegroundBrush(brush);
        var width=text.Width+8;var height=text.Height+4;
        // Prefer above horizontal/diagonal roads and right of vertical roads, regardless of direction.
        if(normal.Y>0 || (Math.Abs(normal.Y)<1e-9 && normal.X<0))normal=-normal;
        foreach(var side in new[]{normal,-normal})
        {
            var center=anchor+side*(10+Math.Abs(side.X)*width/2+Math.Abs(side.Y)*height/2);
            var bounds=new Rect(center.X-width/2,center.Y-height/2,width,height);
            var separation=bounds;separation.Inflate(8,6);
            if(!plot.Contains(bounds) || speedLabelBounds.Any(b=>b.IntersectsWith(separation)))continue;
            speedLabelBounds.Add(bounds);
            dc.DrawRoundedRectangle(LogicalNodeLabelBackground,null,bounds,3,3);
            dc.DrawText(text,new(bounds.X+4,bounds.Y+2));
            return true;
        }
        return false;
    }
}
