using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Rendering")]
public sealed class LogicalNodeRenderingTests
{
    [STATestMethod,DataRow(4,16),DataRow(4,32),DataRow(6,16),DataRow(6,32),DataRow(12,16),DataRow(12,32),DataRow(18,16),DataRow(18,32)]
    public void HalfUnitRoundedCorners_ScaleWithMapAndKeepOriginalSquareBounds(int size,int scale)
    {
        var view=new CoordinateMapView();view.Measure(new Size(1000,1000));view.Arrange(new Rect(0,0,1000,1000));view.ZoomAtCenter(scale);view.UpdateLayout();
        var node=new LogicalNodeSnapshot("ABCDEFGHIJ","owner",0,250000.5m,250000.5m,size,new());
        var model=(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        var nodes=(List<LogicalNodeSnapshot>)typeof(LogicalNodeEditor).GetField("nodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(model)!;
        // Finish the same reset and zoom sequence on both sides of the pixel comparison.
        view.ResetView();view.ZoomAtCenter(scale);view.UpdateLayout();var before=Pixels(view);
        nodes.Add(node);view.InvalidateVisual();view.UpdateLayout();var after=Pixels(view);
        Point Screen(decimal x,decimal y)=>(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[new Point((double)x,(double)y)])!;
        var topLeft=Screen(node.Left,node.Top);var bottomRight=Screen(node.Right,node.Bottom);var radius=scale/2;
        bool Changed(int x,int y)=>!before.AsSpan((y*1000+x)*4,4).SequenceEqual(after.AsSpan((y*1000+x)*4,4));
        foreach(var (x,y) in new[]{((int)topLeft.X,(int)topLeft.Y),((int)bottomRight.X-1,(int)topLeft.Y),((int)topLeft.X,(int)bottomRight.Y-1),((int)bottomRight.X-1,(int)bottomRight.Y-1)})
            Assert.IsFalse(Changed(x,y),$"The original sharp vertex ({x},{y}) is visually cut away by the half-unit arc. Scale={view.Scale}, first={string.Join(',',before.AsSpan((y*1000+x)*4,4).ToArray())}, second={string.Join(',',after.AsSpan((y*1000+x)*4,4).ToArray())}");
        Assert.IsTrue(Changed((int)topLeft.X+radius,(int)topLeft.Y+radius),"The interior behind the rounded corner stays filled.");
        Assert.IsTrue(Changed((int)(topLeft.X+bottomRight.X)/2,(int)topLeft.Y),"The original top boundary stays at its half-integer coordinate.");
        Assert.IsFalse(Changed((int)topLeft.X-3,(int)topLeft.Y+radius),"Rounding must not enlarge the logical square.");
    }
    private static byte[] Pixels(FrameworkElement view)
    {
        var bitmap=new RenderTargetBitmap(1000,1000,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var pixels=new byte[1000*1000*4];bitmap.CopyPixels(pixels,4000,0);return pixels;
    }
}
