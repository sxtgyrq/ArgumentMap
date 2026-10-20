using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class ViewportUnitTests
{
    private static CoordinateViewport View()
    {
        var view = new CoordinateViewport(); view.Resize(new Size(1000, 600)); return view;
    }

    [TestMethod("坐标系默认打开在 (250000,250000)")]
    public void StartsAtWorldCenter() => Assert.AreEqual(new Point(250000, 250000), View().Center);

    [TestMethod]
    [DataRow(0.25), DataRow(1.25), DataRow(4.0)]
    public void Zoom_KeepsWorldPositionUnderCursor(double factor)
    {
        var view = View(); var anchor = new Point(170, 210); var before = view.ScreenToWorld(anchor);
        view.Zoom(factor, anchor); var after = view.ScreenToWorld(anchor);
        Assert.AreEqual(before.X, after.X, 1e-8); Assert.AreEqual(before.Y, after.Y, 1e-8);
    }

    [TestMethod]
    public void Coordinates_RoundTripAndYIncreasesUpwards()
    {
        var view = View(); var world = new Point(250123.5, 249789.5);
        Assert.AreEqual(world, view.ScreenToWorld(view.WorldToScreen(world)));
        Assert.IsTrue(view.WorldToScreen(new Point(250000, 250001)).Y < view.WorldToScreen(view.Center).Y);
    }

    [TestMethod]
    public void Pan_ClampsCenterToWorldBoundsAndResetRestoresCenter()
    {
        var view = View(); view.Pan(new Vector(1000000, 1000000));
        Assert.AreEqual(new Point(0, 500000), view.Center);
        view.Reset(); Assert.AreEqual(new Point(250000, 250000), view.Center); Assert.AreEqual(1.0, view.Scale);
    }

    [TestMethod]
    public void Zoom_EnforcesMinimumAndMaximum()
    {
        var view = View(); view.Zoom(1e9, new Point(500, 300)); Assert.AreEqual(CoordinateViewport.MaximumScale, view.Scale);
        view.Zoom(1e-12, new Point(500, 300)); Assert.AreEqual(view.FitScale, view.Scale);
    }

    [TestMethod]
    [DataRow(0.1, 1000.0), DataRow(1.0, 100.0), DataRow(2.5, 50.0), DataRow(10.0, 10.0), DataRow(32.0, 5.0)]
    public void Rulers_ChangeIntervalsWithZoom(double scale, double expectedStep) =>
        Assert.AreEqual(expectedStep, CoordinateViewport.GetGridStep(scale));
}
