using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeStartTests
{
    [TestMethod]
    public void StartIsUniqueAcrossOwners_OrdinaryNodesRemainUnlimited_DeletionReleasesSlot()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);
        var start=service.Create(new("alice",100.5m,100.5m,18,new())).Data!;
        Assert.IsTrue(start.IsStart);
        var before=map.Snapshot();
        Assert.AreEqual("StartLogicalNodeExists",service.Create(new("bob",300.5m,300.5m,18,new())).Code);
        Assert.AreEqual(before,map.Snapshot());
        foreach(var size in new[]{6,12})foreach(var x in new[]{200.5m,300.5m})
            Assert.IsTrue(service.Create(new("bob",x,size==12?250.5m:200.5m,size,new(){Value=$"普通节点{x}"})).Success);
        Assert.AreEqual(1,service.GetAll().Data!.Count(n=>n.IsStart));
        before=map.Snapshot();Assert.AreEqual("NotOwner",service.Delete(new(start.Code,"bob",start.Version)).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.IsTrue(service.Delete(new(start.Code,"alice",start.Version)).Success);
        Assert.IsTrue(service.Create(new("bob",300.5m,300.5m,18,new())).Success);
    }
    [TestMethod]
    public void StartUsesSameGeometryRules_AndPassingRoadPositionsAreAtomic()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);
        Assert.IsFalse(service.Create(new("owner",100m,100.5m,18,new())).Success);
        Assert.IsFalse(service.Create(new("owner",8.5m,100.5m,18,new())).Success);
        var road=map.Add(90,110,130,110);map.Add(110,90,110,130);
        var before=map.Snapshot();Assert.AreEqual("LogicalNodeCrossNotDisconnected",service.Create(new("owner",110.5m,110.5m,18,new())).Code);Assert.AreEqual(before,map.Snapshot());
        map.Calculate();var cross=map.Repository.Transaction(s=>s.Crosses.Values.Single());
        Assert.IsTrue(map.Crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Disconnected)).Success);
        var start=service.Create(new("owner",110.5m,110.5m,18,new())).Data!;Assert.AreEqual(2,start.Positions.Length);
        before=map.Snapshot();Assert.AreEqual("LineNodeInsideLogicalNode",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(road,90,110,110,110,130,110)).Code);Assert.AreEqual(before,map.Snapshot());
        var diagonal=new TestMap();diagonal.Add(90,90,130,130);
        Assert.AreEqual("DiagonalInsideLogicalNode",new LogicalNodeService(diagonal.Repository).Create(new("owner",110.5m,110.5m,18,new())).Code);
    }
    [STATestMethod,DataRow(4),DataRow(6),DataRow(12),DataRow(18)]
    public void PlacementSnapsCenter_ValidatesBoundsAndCanBeCancelled(int size)
    {
        var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var map=new CoordinateMapView();map.ConfigureClient(new HttpLineClient(http,new("owner","test")));
        var windows=0;map.LogicalNodeWindowRequested+=(_,args)=>windows++;
        Assert.IsTrue(map.BeginLogicalNodePlacement(size));Assert.IsTrue(map.IsPlacingLogicalNode);
        Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(0,0)).GetAwaiter().GetResult());Assert.AreEqual(0,handler.Creates);Assert.IsTrue(map.IsPlacingLogicalNode);
        Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(100.9,110.1)).GetAwaiter().GetResult());Assert.AreEqual(size,handler.LastCreate!.SideLength);
        Assert.AreEqual(100.5m,handler.LastCreate.X);Assert.AreEqual(110.5m,handler.LastCreate.Y);Assert.IsTrue(map.IsPlacingLogicalNode);Assert.AreEqual(0,windows);
        Assert.AreEqual(size!=18,map.PlaceLogicalNodeAsync(new Point(200,200)).GetAwaiter().GetResult(),"Continuous placement retains the chosen size; a second start is forbidden.");
        Assert.AreEqual(size,map.LogicalNodePlacementSize);
        map.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsFalse(map.IsPlacingLogicalNode);
        Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(200,200)).GetAwaiter().GetResult());
    }
}

[TestClass,TestCategory("Rendering")]
public sealed class LogicalNodeStartEntryTests
{
    private static T? Field<T>(MainWindow window,string name) where T:class=>(T?)typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
    private static void Drain()
    {var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    [STATestMethod,DataRow(6),DataRow(12),DataRow(18)]
    public void F5ChoosesSizeBeforePlacement_SavesSelectedSizeWithoutOpeningAttributes(int size)
    {
        var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var window=new MainWindow(new HttpLineClient(http,new("owner","test"))){Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();Drain();var map=(CoordinateMapView)window.FindName("MapView");
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.F5){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(key);Drain();
            Assert.IsTrue(key.Handled);Assert.IsNull(Field<LogicalNodeWindow>(window,"logicalNodeWindow"));
            var chooser=Field<LogicalNodeSizeWindow>(window,"logicalNodeSizeWindow");Assert.IsNotNull(chooser);Assert.IsTrue(chooser.IsVisible);Assert.IsFalse(map.IsPlacingLogicalNode);
            ((Button)chooser.FindName($"Size{size}Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Drain();
            Assert.IsNull(Field<LogicalNodeSizeWindow>(window,"logicalNodeSizeWindow"));Assert.IsTrue(map.IsPlacingLogicalNode);
            Assert.AreEqual(0,handler.Creates);Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(100.2,120.7)).GetAwaiter().GetResult());Drain();
            Assert.IsNull(Field<LogicalNodeWindow>(window,"logicalNodeWindow"));Assert.AreEqual(1,map.LogicalNodeCount);
            Assert.AreEqual(size,handler.LastCreate!.SideLength);Assert.AreEqual(100.5m,handler.LastCreate.X);
            if(size==18)Assert.AreEqual("开始",handler.LastCreate.Properties.Value);
        }
        finally{Field<LogicalNodeWindow>(window,"logicalNodeWindow")?.Close();Field<LogicalNodeSizeWindow>(window,"logicalNodeSizeWindow")?.Close();window.Close();Drain();}
    }
    [STATestMethod]
    public void ExistingStartDisablesOnly18_OrdinarySizesRemainAvailable()
    {
        var chooser=new LogicalNodeSizeWindow(true);
        try{Assert.IsFalse(((Button)chooser.FindName("Size18Button")).IsEnabled);Assert.IsTrue(((Button)chooser.FindName("Size6Button")).IsEnabled);Assert.IsTrue(((Button)chooser.FindName("Size12Button")).IsEnabled);}
        finally{chooser.Close();}
    }
}
