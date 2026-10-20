using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class LogicalNodeProtectionTests
{
    public TestContext TestContext { get; set; } = null!;
    private static Point Screen(CoordinateMapView view, Point world) => (Point)typeof(CoordinateMapView)
        .GetMethod("ToScreen", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, [world])!;
    private static LogicalNodeEditor Nodes(CoordinateMapView view) => (LogicalNodeEditor)typeof(CoordinateMapView)
        .GetField("logicalNodes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
    private static GeometryDrawing[] Areas(CoordinateMapView view)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
            typeof(CoordinateMapView).GetMethod("DrawLogicalNodeProtectionAreas", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(view, [dc, new Rect(72, 30, view.ActualWidth - 72, view.ActualHeight - 30)]);
        return visual.Drawing?.Children.OfType<GeometryDrawing>().ToArray() ?? [];
    }
    private static CoordinateMapView View(int size)
    {
        var view = new CoordinateMapView(); view.Measure(new Size(1000, 650)); view.Arrange(new Rect(0, 0, 1000, 650));
        Nodes(view).ReplaceNodes([new("ABCDEFGHIJ", "owner", 0, 250000.5m, 250000.5m, size, new() { Value = "HUMAN", LogicLanguage = "HUMAN" })]);
        view.UpdateLayout(); view.ResetView(); view.ZoomAtCenter(8); view.UpdateLayout(); return view;
    }
    [STATestMethod, DataRow(4, 11), DataRow(6, 17), DataRow(8, 23), DataRow(10, 29), DataRow(12, 35), DataRow(18, 53)]
    public void AllSizesHaveExactConcentricUnfilledSquare_AndZoomScalesOnlyGeometry(int size, int expected)
    {
        var view = View(size); Assert.IsFalse(view.ShowLogicalNodeProtection); Assert.AreEqual(0, Areas(view).Length);
        foreach (var zoom in new[] { 1d, .5d, 4d })
        {
            view.ZoomAtCenter(zoom); view.SetShowLogicalNodeProtection(true); var drawing = Areas(view).Single();
            var bounds = ((RectangleGeometry)drawing.Geometry).Rect; var center = Screen(view, new(250000.5, 250000.5));
            Assert.AreEqual(expected * view.Scale, bounds.Width, 1e-6); Assert.AreEqual(bounds.Width, bounds.Height);
            Assert.AreEqual(center.X, bounds.Left + bounds.Width / 2, 1e-6); Assert.AreEqual(center.Y, bounds.Top + bounds.Height / 2, 1e-6);
            Assert.IsNull(drawing.Brush); Assert.AreEqual(1d, drawing.Pen.Thickness); Assert.IsTrue(drawing.Pen.DashStyle.Dashes.Count > 0);
        }
    }
    [STATestMethod]
    public void ToolbarMenuAndGStaySynchronized_AndTextModifiersAndRepeatAreUnaffected()
    {
        using var handler = new DirectLogicalNodeHttpStub(); MapUndoTests.Node(handler.Repository, 12, "HUMAN", "HUMAN", 250000.5m);
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") }; var window = new MainWindow(new HttpLineClient(http, new("owner", "test")))
        { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); var view = (CoordinateMapView)window.FindName("MapView"); window.InitializeLinesAsync().GetAwaiter().GetResult();
            var toggle = (CheckBox)window.FindName("LogicalNodeProtectionToggle"); var menu = (MenuItem)window.FindName("LogicalNodeProtectionMenu");
            var before = LogicalNodeMySqlTests.Snapshot(handler.Repository); Assert.AreEqual(false, toggle.IsChecked); Assert.IsFalse(menu.IsChecked);
            toggle.IsChecked = true; Assert.IsTrue(view.ShowLogicalNodeProtection); Assert.IsTrue(menu.IsChecked);
            Assert.IsTrue(window.HandleLogicalNodeProtectionShortcut(Key.G, ModifierKeys.None, false, view)); Assert.AreEqual(false, toggle.IsChecked); Assert.IsFalse(menu.IsChecked);
            menu.IsChecked = true; menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.AreEqual(true, toggle.IsChecked);
            Assert.IsTrue(window.HandleLogicalNodeProtectionShortcut(Key.G, ModifierKeys.None, true, view)); Assert.IsTrue(view.ShowLogicalNodeProtection);
            Assert.IsFalse(window.HandleLogicalNodeProtectionShortcut(Key.G, ModifierKeys.None, false, new TextBox()));
            Assert.IsFalse(window.HandleLogicalNodeProtectionShortcut(Key.G, ModifierKeys.None, false, new PasswordBox()));
            foreach (var modifier in new[] { ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Shift })
                Assert.IsFalse(window.HandleLogicalNodeProtectionShortcut(Key.G, modifier, false, view));
            // Exercise the actual window preview-key route as well as the helper.
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.G) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(key); Assert.IsTrue(key.Handled); Assert.IsFalse(view.ShowLogicalNodeProtection);
            view.SetShowLogicalNodeProtection(true); view.ResetView(); view.FitView(); view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();
            Assert.IsTrue(view.ShowLogicalNodeProtection); Assert.AreEqual(true, toggle.IsChecked); Assert.IsTrue(menu.IsChecked);
            Assert.AreEqual(before, LogicalNodeMySqlTests.Snapshot(handler.Repository)); Assert.AreEqual(0, handler.Undos + handler.Creates + handler.AreaMoves + handler.AreaDeletes + handler.FlowImports + handler.LogicalNodeCalculations + handler.CrossCalculations);
        }
        finally { window.Close(); }
    }
    [STATestMethod]
    public void ProtectionCannotBeSelected_AndF7StillUsesOnlyActualLnSquare()
    {
        using var handler = new DirectLogicalNodeHttpStub(); var n = new LogicalNodeService(handler.Repository).Create(new("owner",250000.5m,250000.5m,12,new(){Value="HUMAN"})); Assert.IsTrue(n.Success,n.Message);
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") }; var view = new CoordinateMapView(); view.ConfigureClient(new HttpLineClient(http,new("owner","test")));
        view.Measure(new Size(1000,650)); view.Arrange(new Rect(0,0,1000,650)); view.UpdateLayout(); view.RefreshLogicalNodesAsync().GetAwaiter().GetResult(); view.ResetView(); view.ZoomAtCenter(8); view.SetShowLogicalNodeProtection(true);
        Assert.IsFalse(view.SelectLogicalNodeAt(Screen(view,new(249983,250000.5)))); Assert.IsTrue(view.SelectLogicalNodeAt(Screen(view,new(250000.5,250000.5))));
        Assert.IsTrue(view.BeginAreaMove()); Assert.IsTrue(view.SelectMapAreaAsync(new(249990,249990,250010,250010),new()).GetAwaiter().GetResult());
        Assert.IsTrue(view.TryCopySelection(out var text)); Assert.AreEqual(1,MapAreaClipboard.Decode(text)!.LogicalNodes.Length);
        view.CancelEditing();
        // Both the new road and a second LN enter the visual area, but never touch the actual LN12.
        LogicalNodeMySqlTests.Add(handler.Repository,249970,250012,250030,250012);
        var allowed = new LogicalNodeService(handler.Repository).Create(new("owner",250014.5m,250000.5m,4,new(){Value="False"})); Assert.IsTrue(allowed.Success,allowed.Message);
    }
    [STATestMethod, DataRow("place"), DataRow("move"), DataRow("paste"), DataRow("import")]
    public void UncommittedPreviewsShowProtectionAtTheCandidateCenter_AndGDoesNotCancelThem(string mode)
    {
        var view = View(12); view.SetShowLogicalNodeProtection(true); var existing = Nodes(view).Nodes.Single(); var candidate = new Point(250050.5,250000.5);
        using var handler = new DirectLogicalNodeHttpStub(); using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") }; view.ConfigureClient(new HttpLineClient(http,new("owner","test")));
        if (mode == "place") Assert.IsTrue(view.BeginLogicalNodePlacement(12));
        else if (mode == "move") Assert.IsTrue(view.BeginLogicalNodeMove(existing));
        else if (mode == "import") { Assert.IsTrue(view.BeginFlowDiagramImport()); view.ShowFlowDiagramPreview(new([], [existing with { X = (decimal)candidate.X }], [])); }
        else Assert.IsTrue(view.BeginPasteSelection(LogicalNodeClipboard.Encode(existing.SideLength, existing.Properties)));
        typeof(CoordinateMapView).GetField("lastPointer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,Screen(view,candidate));
        typeof(CoordinateMapView).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,null);
        Assert.IsTrue(Areas(view).Any(a => Math.Abs(((RectangleGeometry)a.Geometry).Rect.Left + ((RectangleGeometry)a.Geometry).Rect.Width/2 - Screen(view,candidate).X)<1e-6));
        Assert.IsTrue(view.HandleEditorKeyAsync(Key.G).GetAwaiter().GetResult()); Assert.AreEqual(0,Areas(view).Length);
        Assert.IsTrue(mode == "import" ? view.IsFlowDiagramImportActive : mode == "move" ? view.IsMovingLogicalNode : view.IsPlacingLogicalNode);
    }
    [STATestMethod, TestCategory("Rendering")]
    public void RenderReferenceSquare_RoadDrawsAboveProtection_AndHideRestoresOriginalPixels()
    {
        var view = View(12); var lines=(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
        lines.ReplaceLines([new("KLMNOPQRST","owner",false,1,[new("KLMNOPQRST",0,249970,250012),new("KLMNOPQRST",1,250030,250012)])]);
        view.ResetView(); view.ZoomAtCenter(8);
        byte[] Render()
        {
            view.UpdateLayout(); var bitmap=new RenderTargetBitmap(1000,650,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var pixels=new byte[1000*650*4];bitmap.CopyPixels(pixels,4000,0);return pixels;
        }
        var hidden=Render(); view.SetShowLogicalNodeProtection(true);var shown=Render(); Assert.IsFalse(hidden.SequenceEqual(shown));
        var crossing=Screen(view,new(249983,250012));var offset=((int)crossing.Y*1000+(int)crossing.X)*4;
        CollectionAssert.AreEqual(hidden.AsSpan(offset,4).ToArray(),shown.AsSpan(offset,4).ToArray(),"Roads remain above the reference square.");
        var bitmap=new RenderTargetBitmap(1000,650,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","ln-protection-reference.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);using(var file=File.Create(path))png.Save(file);TestContext.AddResultFile(path);
        view.SetShowLogicalNodeProtection(false);CollectionAssert.AreEqual(hidden,Render());
    }
}
