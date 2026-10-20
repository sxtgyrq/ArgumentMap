using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class MapAreaDeleteUiTests
{
    private static MainWindow Window(HttpLineClient client)=>new(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
    private static void Press(MainWindow window,Key key)
    {var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);Assert.IsTrue(e.Handled);}
    private static void Fixture(DirectLogicalNodeHttpStub handler)
    {
        LogicalNodeMySqlTests.Add(handler.Repository,20,50,80,50);LogicalNodeMySqlTests.Add(handler.Repository,50,20,50,80);var crosses=new CrossService(handler.Repository);Assert.IsTrue(crosses.Recalculate().Success);
        var c=crosses.GetAll().Data!.Single();Assert.IsTrue(crosses.Edit(new(c.Key,"owner",c.Version,CrossState.Disconnected)).Success);
        Assert.IsTrue(new LogicalNodeService(handler.Repository).Create(new("owner",50.5m,50.5m,6,new(){Value="选中",LogicLanguage="P"})).Success);
        LogicalNodeMySqlTests.Add(handler.Repository,200,250,280,250);Assert.IsTrue(crosses.Recalculate().Success);
        Assert.IsTrue(new LogicalNodeService(handler.Repository).Create(new("owner",250.5m,250.5m,6,new(){Value="保留",LogicLanguage="Q"})).Success);
    }

    [STATestMethod,DataRow(false),DataRow(true)]
    public void RAndMenuDeleteOriginalSelection_NotPreviewDestination_RefreshAllCaches(bool menu)
    {
        using var handler=new DirectLogicalNodeHttpStub();Fixture(handler);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();var view=(CoordinateMapView)window.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();view.RefreshCrossesAsync().GetAwaiter().GetResult();view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();
            Press(window,Key.F7);Assert.IsTrue(view.SelectMapAreaAsync(MapAreaMoveTests.Bounds,new(50,50)).GetAwaiter().GetResult());
            typeof(CoordinateMapView).GetProperty(nameof(CoordinateMapView.PointerPosition))!.SetValue(view,new Point(250,250));
            if(menu){var item=(MenuItem)window.FindName("AreaDeleteMenu");Assert.AreEqual("框选后 R",item.InputGestureText);item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));}else Press(window,Key.R);
            Assert.AreEqual(1,handler.AreaDeletes);Assert.AreEqual(0,handler.AreaMoves);Assert.IsFalse(view.IsAreaMoveMode);Assert.AreEqual(1,view.LineCount);Assert.AreEqual(0,view.CrossCount);Assert.AreEqual(1,view.LogicalNodeCount);
            Assert.AreEqual("保留",new LogicalNodeService(handler.Repository).GetAll().Data!.Single().Properties.Value);StringAssert.Contains(view.OperationMessage,"已删除框选");
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void RBeforeSelectingDoesNotSubmit_BusyDeletionBlocksDuplicateMoveCancel_AndCanRetryFailure()
    {
        using var handler=new DirectLogicalNodeHttpStub();Fixture(handler);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();var view=(CoordinateMapView)window.FindName("MapView");Press(window,Key.F7);Press(window,Key.R);Assert.AreEqual(0,handler.AreaDeletes);StringAssert.Contains(view.OperationMessage,"完成框选");
            Assert.IsTrue(view.SelectMapAreaAsync(MapAreaMoveTests.Bounds,new()).GetAwaiter().GetResult());handler.AreaDeleteGate=new();var deleting=view.DeleteAreaSelectionAsync();Assert.IsTrue(view.IsSaving);
            Press(window,Key.R);Press(window,Key.Enter);Press(window,Key.Escape);Press(window,Key.F7);Assert.IsTrue(view.IsAreaMoveMode);Assert.AreEqual(1,handler.AreaDeletes);Assert.AreEqual(0,handler.AreaMoves);
            handler.AreaDeleteGate.SetResult(ServiceResult<MapAreaSnapshot>.Fail("DatabaseError","删除失败，全部回滚"));LogicalNodeWindowEntryTests.DrainUntil(()=>deleting.IsCompleted);Assert.IsFalse(deleting.GetAwaiter().GetResult());Assert.IsFalse(view.IsSaving);Assert.IsTrue(view.IsAreaMoveMode);
            Assert.AreEqual(3,handler.Repository.Transaction(s=>s.Lines.Count));handler.AreaDeleteGate=null;Press(window,Key.R);Assert.AreEqual(2,handler.AreaDeletes);Assert.IsFalse(view.IsAreaMoveMode);Assert.AreEqual(1,view.LineCount);
        }
        finally{window.Close();}
    }
}
