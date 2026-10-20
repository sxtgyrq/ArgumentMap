using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Rendering")]
public sealed class ArrowRenderingTests
{
    [STATestMethod]
    [DataRow(1, 0), DataRow(1, 1), DataRow(0, 1), DataRow(-1, 1)]
    [DataRow(-1, 0), DataRow(-1, -1), DataRow(0, -1), DataRow(1, -1)]
    public void SavedDirectionChangesAndReversalRenderCorrectArrowheads(int dx, int dy)
    {
        var store = new TestMap();
        var line = store.Add(250000 - dx * 32, 250000 - dy * 32,
            250000 + dx * 32, 250000 + dy * 32);
        var view = CreateView(line);
        var baseline = Pixels(view);
        var direction = new Vector(dx, -dy); direction.Normalize();
        AssertArrowheads(view, baseline, direction, forward: true, reverse: false);

        var saved = store.Lines.SaveProperties(new(line.Code, line.Owner, line.Version,
            line.Properties with { PassInOpposeDirection = 1 }));
        Assert.IsTrue(saved.Success, saved.Message); line = saved.Data!;
        Apply(view, line);
        CollectionAssert.AreEqual(baseline, Pixels(view), "Hidden arrows must not change the map when the direction property changes.");
        AssertArrowheads(view, baseline, direction, forward: true, reverse: true);

        var reversed = store.Lines.Reverse(new(line.Code, line.Owner, line.Version));
        Assert.IsTrue(reversed.Success, reversed.Message); line = reversed.Data!;
        Apply(view, line);
        AssertArrowheads(view, Pixels(view), direction, forward: true, reverse: true);

        saved = store.Lines.SaveProperties(new(line.Code, line.Owner, line.Version,
            line.Properties with { PassInOpposeDirection = 0 }));
        Assert.IsTrue(saved.Success, saved.Message); line = saved.Data!;
        Apply(view, line);
        AssertArrowheads(view, Pixels(view), direction, forward: false, reverse: true);

        reversed = store.Lines.Reverse(new(line.Code, line.Owner, line.Version));
        Assert.IsTrue(reversed.Success, reversed.Message);
        Apply(view, reversed.Data!);
        AssertArrowheads(view, Pixels(view), direction, forward: true, reverse: false);
    }

    [STATestMethod]
    public void LockedBidirectionalLineKeepsGreenOpposingArrowheads()
    {
        var store = new TestMap(); var line = store.Add(249968, 250000, 250032, 250000);
        var saved = store.Lines.SaveProperties(new(line.Code, line.Owner, line.Version,
            line.Properties with { PassInOpposeDirection = 1 }));
        Assert.IsTrue(saved.Success); line = saved.Data!;
        store.Calculate();
        Assert.IsTrue(new LoginService(store.Repository).CreateUser(new("admin", "admin")).Success);
        var locked = store.Lines.ToggleLock(new(line.Code, "admin", line.Version));
        Assert.IsTrue(locked.Success, locked.Message);
        var view = CreateView(locked.Data!);
        AssertArrowheads(view, Pixels(view), new Vector(1, 0), forward: true, reverse: true, green: true);
    }

    private static CoordinateMapView CreateView(LineSnapshot line)
    {
        var view = new CoordinateMapView();
        view.Measure(new Size(640, 400));
        view.Arrange(new Rect(0, 0, 640, 400));
        Apply(view, line);
        return view;
    }

    private static void Apply(CoordinateMapView view, LineSnapshot line)
    {
        var editor = (LineEditor)typeof(CoordinateMapView).GetField("editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        typeof(LineEditor).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [line]);
        view.SetShowArrows(false);
        view.ResetView();
        view.UpdateLayout();
    }

    // Compare actual rendered pixels with the same map without arrows. Short centered segments
    // have one marker; project its wings onto the original screen direction to distinguish > from <.
    private static void AssertArrowheads(CoordinateMapView view, byte[] baseline, Vector direction,
        bool forward, bool reverse, bool green = false)
    {
        view.SetShowArrows(true); view.UpdateLayout();
        var pixels = Pixels(view);
        var center = (Point)typeof(CoordinateMapView).GetMethod("ToScreen", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [new Point(250000, 250000)])!;
        var perpendicular = new Vector(-direction.Y, direction.X);
        var forwardWings = 0; var reverseWings = 0; var greenWings = 0;
        for (var y = (int)center.Y - 18; y <= center.Y + 18; y++)
        for (var x = (int)center.X - 18; x <= center.X + 18; x++)
        {
            var offset = new Point(x + 0.5, y + 0.5) - center;
            var normal = Math.Abs(Vector.Multiply(offset, perpendicular));
            if (normal < 1.75 || normal > 5.5) continue;
            var index = (y * (int)view.ActualWidth + x) * 4;
            if (pixels.AsSpan(index, 4).SequenceEqual(baseline.AsSpan(index, 4))) continue;
            var tangent = Vector.Multiply(offset, direction);
            // A forward-only chevron has its wings behind the center. Paired chevrons point
            // outward, so the forward wings lie ahead and the reverse wings lie behind.
            if (tangent < -1.5) forwardWings++;
            if (tangent > 1.5) reverseWings++;
            if (pixels[index + 1] > pixels[index] && pixels[index + 1] > pixels[index + 2]) greenWings++;
        }
        Assert.AreEqual(forward, forwardWings > 3, "Expected arrow wings behind the original direction marker.");
        Assert.AreEqual(reverse, reverseWings > 3, "Expected arrow wings ahead of the original direction marker.");
        if (green) Assert.IsTrue(greenWings > 6, "Locked arrows must retain the green line color.");
        view.SetShowArrows(false); view.UpdateLayout();
        CollectionAssert.AreEqual(baseline, Pixels(view), "Hiding arrows restores the original map pixels.");
    }

    private static byte[] Pixels(CoordinateMapView view)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
