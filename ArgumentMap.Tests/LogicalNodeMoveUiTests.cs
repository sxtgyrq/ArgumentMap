using System.Reflection;
using System.IO;
using System.Net.Http;
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
public sealed class LogicalNodeMoveUiTests
{
    public TestContext TestContext { get; set; } = null!;
    internal sealed class Client(TestMap map) : ILineClient, ILogicalNodeClient, ILogicalNodeMoveClient, ILinePasteClient
    {
        internal int LinePastes;
        internal TaskCompletionSource<ServiceResult<LinePasteResult>>? PasteGate;
        public Task<ServiceResult<LinePasteResult>> PasteLineAsync(LineCopyData source,int x,int y)
        {LinePastes++;return PasteGate?.Task??Task.FromResult(new LinePasteService(map.Repository).Paste(new("owner",source,x,y)));}
        internal int Moves, Creates;
        internal TaskCompletionSource<ServiceResult<LogicalNodeSnapshot>>? Gate;
        public Task<ServiceResult<LineSnapshot>> CreateAsync()=>Task.FromResult(ServiceResult<LineSnapshot>.Ok(map.Create(),""));
        public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string code)=>Task.FromResult(map.Lines.StartDrawing(new(code,"owner")));
        public Task<ServiceResult<LineSnapshot>> SaveAsync(string code,long version,LineNode[] nodes)=>Task.FromResult(map.Lines.CheckShapeIsOKThenSave(new(code,"owner",version,nodes)));
        public Task<ServiceResult<LineSnapshot[]>> GetAllAsync()=>Task.FromResult(map.Lines.GetAll());
        public Task<ServiceResult<bool>> DeleteAsync(string code,long version)=>Task.FromResult(map.Lines.Delete(new(code,"owner",version)));
        public Task<ServiceResult<LogicalNodeSnapshot[]>> GetLogicalNodesAsync()=>Task.FromResult(new LogicalNodeService(map.Repository).GetAll("owner"));
        public Task<ServiceResult<LogicalNodeSnapshot>> CreateLogicalNodeAsync(decimal x,decimal y,int size,LogicalNodeProperties p)
        { Creates++; return Task.FromResult(new LogicalNodeService(map.Repository).Create(new("owner",x,y,size,p))); }
        public Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code,long version,string value,string info)=>Task.FromResult(new LogicalNodeService(map.Repository).Update(new(code,"owner",version,value,info)));
        public Task<ServiceResult<bool>> DeleteLogicalNodeAsync(string code,long version)=>Task.FromResult(new LogicalNodeService(map.Repository).Delete(new(code,"owner",version)));
        public Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeAsync()=>Task.FromResult(new LogicalNodeService(map.Repository).Calculate());
        public Task<ServiceResult<LogicalNodeSnapshot>> MoveLogicalNodeAsync(string code,long version,decimal x,decimal y)
        { Moves++; return Gate?.Task ?? Task.FromResult(new LogicalNodeService(map.Repository).Move(new(code,"owner",version,x,y))); }
    }

    [STATestMethod]
    public void PropertiesMoveButton_ClosesDialogRequestsMapMove_ProtectsUnsavedMetadataAndPermissions()
    {
        var map=new TestMap(); var node=LogicalNodeMoveTests.Create(map); var client=new Client(map);
        var window=new LogicalNodeWindow(client,"owner",new(),node); var moves=0; var closed=false;
        window.MoveRequested+=(_,_)=>moves++; window.Closed+=(_,_)=>closed=true;
        var button=(Button)window.FindName("MoveButton"); Assert.IsTrue(button.IsEnabled);
        Assert.IsFalse(((UIElement)window.FindName("GeometryPanel")).IsEnabled);
        ((TextBox)window.FindName("ValueInput")).Text="未保存值";
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreEqual(0,moves); Assert.IsFalse(closed);
        StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"先保存");
        ((TextBox)window.FindName("ValueInput")).Text=node.Properties.Value;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreEqual(1,moves); Assert.IsTrue(closed); Assert.AreEqual(0,client.Moves);
        window=new LogicalNodeWindow(client,"owner",new(),node with {CanEdit=false});
        button=(Button)window.FindName("MoveButton"); Assert.IsFalse(button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreEqual(0,client.Moves); window.Close();
        window=new LogicalNodeWindow(client,"owner",new()); Assert.AreEqual(Visibility.Collapsed,((Button)window.FindName("MoveButton")).Visibility); window.Close();
    }

    [STATestMethod, DataRow("A"), DataRow("Enter"), DataRow("click")]
    public void MapMoveSnapsToHalfInteger_ValidatesOnConfirm_EndsAfterOneMove_AndEscCancels(string confirm)
    {
        var map=new TestMap();var node=LogicalNodeMoveTests.Create(map,18);var client=new Client(map);
        var window=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");
            view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Assert.IsTrue(view.BeginLogicalNodeMove(node));Assert.IsFalse(view.IsPlacingLogicalNode);
            var before=map.Snapshot(); view.HandleEditorKeyAsync(Key.F6).GetAwaiter().GetResult(); view.RequestLogicalNodeEdit();
            Assert.AreEqual(before,map.Snapshot()); Assert.AreEqual(0,client.Moves); Assert.IsTrue(view.IsMovingLogicalNode);
            Assert.IsFalse(view.MoveLogicalNodeAsync(new(1.1,1.2)).GetAwaiter().GetResult());Assert.AreEqual(before,map.Snapshot());Assert.IsTrue(view.IsMovingLogicalNode);
            Pointer(view,new(100.1,101.9));
            if(confirm=="click")Assert.IsTrue(view.PlaceLogicalNodeAsync(new(100.1,101.9)).GetAwaiter().GetResult());
            else Press(window,confirm=="A"?Key.A:Key.Enter);
            Assert.IsFalse(view.IsMovingLogicalNode);Assert.IsFalse(view.IsPlacingLogicalNode);Assert.AreEqual(2,client.Moves);Assert.AreEqual(0,client.Creates);
            node=new LogicalNodeService(map.Repository).GetAll().Data!.Single();Assert.AreEqual(100.5m,node.X);Assert.AreEqual(101.5m,node.Y);Assert.AreEqual(18,node.SideLength);
            Assert.IsTrue(view.BeginLogicalNodeMove(node));before=map.Snapshot();Press(window,Key.Escape);Assert.IsFalse(view.IsMovingLogicalNode);Assert.AreEqual(before,map.Snapshot());
        }
        finally{window.Close();}
    }

    [TestMethod]
    public async Task PendingMoveBusyFailureRetryAndRefresh_KeepCommittedCacheUntilSuccess()
    {
        var map=new TestMap();var node=LogicalNodeMoveTests.Create(map);var client=new Client(map);var editor=new LogicalNodeEditor();editor.Configure(client);await editor.RefreshAsync();
        Assert.IsFalse(editor.BeginMove(node with {CanEdit=false}));Assert.IsTrue(editor.BeginMove(node));var before=map.Snapshot();
        client.Gate=new();var saving=editor.MoveAsync(100.5m,100.5m);Assert.IsTrue(editor.IsBusy);
        Assert.IsFalse(editor.CancelMove());Assert.IsFalse(await editor.MoveAsync(110.5m,110.5m));Assert.IsFalse(await editor.CreateAsync(110.5m,110.5m,6));Assert.IsFalse(await editor.RefreshAsync());
        editor.Leave();editor.Activate();editor.ToggleSelection();Assert.IsNotNull(editor.Moving);
        client.Gate.SetResult(ServiceResult<LogicalNodeSnapshot>.Fail("DatabaseError","数据库写入失败，已回滚"));Assert.IsFalse(await saving);
        Assert.IsNotNull(editor.Moving);Assert.AreEqual(node.X,editor.Nodes.Single().X);Assert.AreEqual(before,map.Snapshot());Assert.IsFalse(editor.IsBusy);
        client.Gate=null;Assert.IsTrue(await editor.MoveAsync(100.5m,100.5m));Assert.IsNull(editor.Moving);Assert.AreEqual(100.5m,editor.Nodes.Single().X);Assert.AreEqual(0,client.Creates);
        Assert.IsTrue(editor.BeginMove(editor.Nodes.Single()));Assert.IsTrue(await editor.RefreshAsync());Assert.IsNull(editor.Moving);
    }

    private static void Pointer(CoordinateMapView view,Point p)=>typeof(CoordinateMapView).GetProperty(nameof(CoordinateMapView.PointerPosition))!.SetValue(view,p);
    [TestMethod]
    public async Task BeginMoveUsesFreshPropertiesSnapshot_WhenMapCacheStillShowsOldLocation()
    {
        var map=new TestMap();var node=LogicalNodeMoveTests.Create(map);var client=new Client(map);var editor=new LogicalNodeEditor();editor.Configure(client);await editor.RefreshAsync();
        var fresh=LogicalNodeMoveTests.Move(map,node,100.5m,100.5m).Data!;
        Assert.AreEqual(50.5m,editor.Nodes.Single().X);
        Assert.IsTrue(editor.BeginMove(fresh));Assert.AreEqual(100.5m,editor.Nodes.Single().X);Assert.AreEqual(fresh.Version,editor.Moving!.Version);
        Assert.IsTrue(editor.CancelMove());Assert.AreEqual(100.5m,editor.Nodes.Single().X);
    }

    [STATestMethod]
    public void ActualMainWindowLnEditToMoveButtonToEnter_UsesFreshNodeAndHttpMoveRoute()
    {
        using var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var client=new HttpLineClient(http,new("owner","test"));var service=new LogicalNodeService(handler.Repository);
        var node=service.Create(new("owner",250000.5m,250000.5m,6,new())).Data!;
        var window=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();
            // Simulate an external move after this map's last load.
            node=service.Move(new(node.Code,"owner",node.Version,250020.5m,250000.5m)).Data!;
            view.ToggleLogicalNodeSelection();
            var editor=(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
            editor.SelectClosest(new(250000.5,250000.5),0);view.RequestLogicalNodeEdit();
            var dialog=(LogicalNodeWindow?)typeof(MainWindow).GetField("logicalNodeWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
            Assert.IsNotNull(dialog);dialog.WindowStartupLocation=WindowStartupLocation.Manual;dialog.Left=-10000;dialog.Top=-10000;
            Assert.AreEqual("250020.5",((TextBox)dialog.FindName("XInput")).Text);
            ((Button)dialog.FindName("MoveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(view.IsMovingLogicalNode);Assert.IsNull(typeof(MainWindow).GetField("logicalNodeWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window));
            Assert.AreEqual(node.X,editor.Nodes.Single().X);Assert.AreEqual(node.Version,editor.Moving!.Version);
            Pointer(view,new(250030.2,250010.8));Press(window,Key.Enter);
            Assert.IsFalse(view.IsMovingLogicalNode);var stored=service.GetAll().Data!.Single();Assert.AreEqual(250030.5m,stored.X);Assert.AreEqual(250010.5m,stored.Y);Assert.AreEqual(node.Version+1,stored.Version);Assert.AreEqual(0,handler.Creates);
        }
        finally{window.Close();}
    }
    [STATestMethod, TestCategory("Rendering")]
    public void MoveDialogAndMapPreviewRender_WithOriginalSquareAndDestination()
    {
        var map=new TestMap();var client=new Client(map);
        var node=new LogicalNodeService(map.Repository).Create(new("owner",250000.5m,250000.5m,12,new(){Value="逻辑节点",Info="移动预览"})).Data!;
        var window=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        var dialog=new LogicalNodeWindow(client,"owner",new(),node){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            dialog.Show();dialog.UpdateLayout();SaveImage(dialog,"ln-move-dialog.png");
            Assert.IsTrue(((Button)dialog.FindName("MoveButton")).ActualWidth>0);
            dialog.Close();window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();view.ZoomAtCenter(8);
            Assert.IsTrue(view.BeginLogicalNodeMove(node));
            typeof(CoordinateMapView).GetField("lastPointer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,
                (Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[new Point(250030.2,250020.7)])!);
            typeof(CoordinateMapView).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,null);
            SaveImage(window,"ln-move-preview.png");Assert.IsTrue(view.IsMovingLogicalNode);Assert.AreEqual(0,client.Moves);
        }
        finally { dialog.Close();window.Close(); }
    }
    private void SaveImage(Window window,string fileName)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",fileName);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);
    }
    private static void Press(Window window,Key key)
    {
        var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);Assert.IsTrue(e.Handled);
    }
}
