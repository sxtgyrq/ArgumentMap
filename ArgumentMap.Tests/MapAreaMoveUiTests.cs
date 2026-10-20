using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class MapAreaMoveUiTests
{
    public TestContext TestContext { get; set; }=null!;
    private static MainWindow Window(HttpLineClient client)=>new(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
    private static void Press(MainWindow window,Key key)
    {var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);Assert.IsTrue(e.Handled);}
    private static void Pointer(CoordinateMapView view,Point p)=>typeof(CoordinateMapView).GetProperty(nameof(CoordinateMapView.PointerPosition))!.SetValue(view,p);
    private static Point Screen(CoordinateMapView view,Point world)=>(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[world])!;
    private static readonly MapAreaBounds Bounds=new(249960,249960,250040,250040);
    private static void Fixture(DirectLogicalNodeHttpStub handler)
    {
        LogicalNodeMySqlTests.Add(handler.Repository,249970,250000,250030,250000);LogicalNodeMySqlTests.Add(handler.Repository,250000,249970,250000,250030);
        var service=new CrossService(handler.Repository);while(service.Calculate().Data!.CanContinue){}
        var c=service.GetAll().Data!.Single();Assert.IsTrue(service.Edit(new(c.Key,"owner",c.Version,CrossState.Disconnected)).Success);
        Assert.IsTrue(new LogicalNodeService(handler.Repository).Create(new("owner",250000.5m,250000.5m,6,new(){Value="整体移动"})).Success);
    }

    [STATestMethod,DataRow("key"),DataRow("button"),DataRow("menu")]
    public void F7ButtonAndMenu_EnterCommitsIntegerTranslation_UpdatesAllCaches_EscAndRestart(string entry)
    {
        using var handler=new DirectLogicalNodeHttpStub();Fixture(handler);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var client=new HttpLineClient(http,new("owner","test"));var window=Window(client);
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");
            if(entry=="key")Press(window,Key.F7);
            else if(entry=="button")((Button)window.FindName("AreaMoveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else ((MenuItem)window.FindName("AreaMoveMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsTrue(view.IsAreaMoveMode);Assert.IsTrue(view.SelectMapAreaAsync(Bounds,new(250040,249960)).GetAwaiter().GetResult());
            Assert.IsFalse(view.NewLineAsync().GetAwaiter().GetResult());view.RequestLogicalNodePlacement();view.ToggleLineSelection();Assert.IsTrue(view.IsAreaMoveMode);Assert.IsFalse(view.IsPlacingLogicalNode);
            Assert.IsFalse(view.CalculateCrossesAsync().GetAwaiter().GetResult());Assert.IsFalse(view.RefreshLogicalNodesAsync().GetAwaiter().GetResult());
            Pointer(view,new(250060.4,249990.3));Press(window,Key.Enter);Assert.IsFalse(view.IsAreaMoveMode);Assert.AreEqual(1,handler.AreaMoves);
            Assert.AreEqual(2,view.LineCount);Assert.AreEqual(1,view.CrossCount);Assert.AreEqual(1,view.LogicalNodeCount);
            var node=new LogicalNodeService(handler.Repository).GetAll().Data!.Single();Assert.AreEqual(250020.5m,node.X);Assert.AreEqual(250030.5m,node.Y);Assert.AreEqual(2,node.Positions.Length);
            var cross=new CrossService(handler.Repository).GetAll().Data!.Single();Assert.AreEqual(new CrossPosition(500040,500060),cross.Key.Position);Assert.AreEqual(CrossState.Disconnected,cross.State);
            Assert.IsTrue(view.BeginAreaMove());Assert.IsTrue(view.SelectMapAreaAsync(new(249980,249980,250080,250080),new()).GetAwaiter().GetResult());
            Press(window,Key.F7);Assert.IsTrue(view.IsAreaMoveMode);Assert.IsFalse(view.CompleteAreaMoveAsync(new(1,1)).GetAwaiter().GetResult());Press(window,Key.Escape);Assert.IsFalse(view.IsAreaMoveMode);Assert.AreEqual(1,handler.AreaMoves);
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void DraggingBothDirectionsSkipsCrossMissingRoads_ThenMouseDropAndRDeleteWork()
    {
        using var handler=new DirectLogicalNodeHttpStub();Fixture(handler);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");
            view.ZoomAtCenter(4);Assert.IsTrue(view.BeginAreaMove());
            var from=Screen(view,new(249960,249995));var to=Screen(view,new(250040,250005));
            Assert.IsTrue(view.BeginAreaSelection(from));view.UpdateAreaSelection(to);
            Assert.IsTrue(view.CompleteAreaSelectionAsync(to).GetAwaiter().GetResult());
            StringAssert.Contains(view.OperationMessage,"已跳过 1 个");
            Assert.AreEqual(1,handler.AreaSelections);Assert.AreEqual(0,handler.AreaMoves);Assert.AreEqual(0,handler.AreaDeletes);
            var forwardBounds=handler.LastAreaBounds!;
            Assert.IsTrue(view.BeginAreaMove());Assert.IsTrue(view.BeginAreaSelection(to));view.UpdateAreaSelection(from);
            Assert.IsTrue(view.CompleteAreaSelectionAsync(from).GetAwaiter().GetResult());
            Assert.AreEqual(forwardBounds,handler.LastAreaBounds,"Both drag directions must produce the same world rectangle.");
            var target=new Point((double)forwardBounds.Left+100,(double)forwardBounds.Bottom+100);
            Assert.IsTrue(view.CompleteAreaMoveAsync(target).GetAwaiter().GetResult());
            Assert.AreEqual(1,handler.AreaMoves);Assert.AreEqual(0,new CrossService(handler.Repository).GetAll().Data!.Length);
            var moved=new LogicalNodeService(handler.Repository).GetAll().Data!.Single();Assert.AreEqual(250100.5m,moved.X);Assert.AreEqual(250100.5m,moved.Y);
            Assert.IsTrue(view.BeginAreaMove());
            view.ZoomAtCenter(0.25);
            from=Screen(view,new(250060,250095));to=Screen(view,new(250140,250105));
            Assert.IsTrue(view.BeginAreaSelection(from));view.UpdateAreaSelection(to);
            Assert.IsTrue(view.CompleteAreaSelectionAsync(to).GetAwaiter().GetResult());Press(window,Key.R);
            Assert.AreEqual(1,handler.AreaDeletes);Assert.AreEqual(1,handler.Repository.Transaction(s=>s.Lines.Count));
            Assert.AreEqual(0,handler.Repository.Transaction(s=>s.LogicalNodes.Count));
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void ClickAndZeroWidthDragExplainGestureWithoutCallingServer_ValidDragCanRetry()
    {
        using var handler=new DirectLogicalNodeHttpStub();Fixture(handler);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");view.ZoomAtCenter(4);
            Assert.IsTrue(view.BeginAreaMove());var from=Screen(view,new(249960,249960));
            Assert.IsTrue(view.BeginAreaSelection(from));Assert.IsFalse(view.CompleteAreaSelectionAsync(from).GetAwaiter().GetResult());
            StringAssert.Contains(view.OperationMessage,"单击不会完成框选");Assert.AreEqual(0,handler.AreaSelections);
            var vertical=Screen(view,new(249960,250040));Assert.IsTrue(view.BeginAreaSelection(from));view.UpdateAreaSelection(vertical);
            Assert.IsFalse(view.CompleteAreaSelectionAsync(vertical).GetAwaiter().GetResult());Assert.AreEqual(0,handler.AreaSelections);
            var to=Screen(view,new(250040,250040));Assert.IsTrue(view.BeginAreaSelection(from));view.UpdateAreaSelection(to);
            Assert.IsTrue(view.CompleteAreaSelectionAsync(to).GetAwaiter().GetResult());Assert.AreEqual(1,handler.AreaSelections);
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void MissingCrossRoadSkipped_RetrySelectionAndInvalidDestinationRetainPreview_ThenCancel()
    {
        using var handler=new DirectLogicalNodeHttpStub();Fixture(handler);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");Assert.IsTrue(view.BeginAreaMove());
            Assert.IsTrue(view.SelectMapAreaAsync(new(249960,249999,250040,250001),new()).GetAwaiter().GetResult());StringAssert.Contains(view.OperationMessage,"已跳过 1 个");
            Assert.IsTrue(view.IsAreaMoveMode);Assert.IsTrue(view.BeginAreaMove());
            Assert.IsTrue(view.SelectMapAreaAsync(Bounds,new(250040,249960)).GetAwaiter().GetResult());
            Assert.IsFalse(view.CompleteAreaMoveAsync(new(500000,500000)).GetAwaiter().GetResult());Assert.IsTrue(view.IsAreaMoveMode);StringAssert.Contains(view.OperationMessage,"边界");
            Press(window,Key.Escape);Assert.IsFalse(view.IsAreaMoveMode);Assert.AreEqual(1,handler.AreaMoves);
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void FullContainmentRuleFlowsThroughWpfSelectionAndDelete()
    {
        using var handler=new DirectLogicalNodeHttpStub();
        var complete=LogicalNodeMySqlTests.Add(handler.Repository,249970,249980,250030,249980);
        var partial=LogicalNodeMySqlTests.Add(handler.Repository,249970,250020,250060,250020);
        var service=new LogicalNodeService(handler.Repository);
        var fullNode=service.Create(new("owner",250000.5m,250000.5m,8,new(){Value="→",LogicLanguage="→"})).Data!;
        var partialNode=service.Create(new("owner",250038.5m,250000.5m,12,new(){Value="部分在框外",LogicLanguage="PARTIAL"})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");
            Press(window,Key.F7);StringAssert.Contains(view.OperationMessage,"部分框入不选");
            Assert.IsTrue(view.SelectMapAreaAsync(Bounds,new()).GetAwaiter().GetResult());
            StringAssert.Contains(view.OperationMessage,"1 条完整线路、1 个完整 LN、0 个 Cross");
            Press(window,Key.R);
            var remaining=handler.Repository.Transaction(s=>(Lines:s.Lines.Keys.ToArray(),Nodes:s.LogicalNodes.Keys.ToArray()));
            Assert.IsFalse(remaining.Lines.Contains(complete.Code));Assert.IsFalse(remaining.Nodes.Contains(fullNode.Code));
            CollectionAssert.AreEqual(new[]{partial.Code},remaining.Lines);
            CollectionAssert.AreEqual(new[]{partialNode.Code},remaining.Nodes);
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void BusyCommitCannotCancelRestartOrDoubleSubmit_FailureAllowsRetry()
    {
        using var handler=new DirectLogicalNodeHttpStub();Fixture(handler);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");view.BeginAreaMove();Assert.IsTrue(view.SelectMapAreaAsync(Bounds,new()).GetAwaiter().GetResult());
            handler.AreaMoveGate=new();var task=view.CompleteAreaMoveAsync(new(20,30));Assert.IsTrue(view.IsSaving);
            Press(window,Key.Escape);Press(window,Key.F7);Assert.IsTrue(view.IsAreaMoveMode);Assert.IsFalse(view.CompleteAreaMoveAsync(new(30,30)).GetAwaiter().GetResult());Assert.AreEqual(1,handler.AreaMoves);
            handler.AreaMoveGate.SetResult(ServiceResult<MapAreaSnapshot>.Fail("DatabaseError","数据库失败，整次回滚"));LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);Assert.IsFalse(task.GetAwaiter().GetResult());Assert.IsFalse(view.IsSaving);Assert.IsTrue(view.IsAreaMoveMode);
            handler.AreaMoveGate=null;Assert.IsTrue(view.CompleteAreaMoveAsync(new(20,30)).GetAwaiter().GetResult());Assert.IsFalse(view.IsAreaMoveMode);Assert.AreEqual(2,handler.AreaMoves);
        }
        finally{window.Close();}
    }

    [STATestMethod,TestCategory("Rendering")]
    public void PreviewDrawsSelectedGeometryWithoutWritingDatabase()
    {
        using var handler=new DirectLogicalNodeHttpStub();Fixture(handler);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();view.RefreshCrossesAsync().GetAwaiter().GetResult();view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();view.ZoomAtCenter(4);
            view.BeginAreaMove();Assert.IsTrue(view.SelectMapAreaAsync(Bounds,new(250040,249960)).GetAwaiter().GetResult());
            typeof(CoordinateMapView).GetField("lastPointer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[new Point(250060,249990)])!);
            typeof(CoordinateMapView).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,null);window.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","map-area-move-preview.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);Assert.AreEqual(0,handler.AreaMoves);
        }
        finally{window.Close();}
    }
}
