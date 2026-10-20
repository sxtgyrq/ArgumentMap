using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class NodeEditUiTests
{
    internal sealed class Client(TestMap map):ILineClient,ILineNodeEditClient
    {
        internal int Finds,Saves,Deletes;internal TaskCompletionSource<ServiceResult<NodePositionProposal>>? Gate;
        public Task<ServiceResult<LineSnapshot>> CreateAsync()=>Task.FromResult(ServiceResult<LineSnapshot>.Ok(map.Create(),""));
        public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string code)=>Task.FromResult(map.Lines.StartDrawing(new(code,"owner")));
        public Task<ServiceResult<LineSnapshot>> SaveAsync(string code,long version,LineNode[] nodes){Saves++;return Task.FromResult(map.Lines.CheckShapeIsOKThenSave(new(code,"owner",version,nodes)));}
        public Task<ServiceResult<LineSnapshot[]>> GetAllAsync()=>Task.FromResult(map.Lines.GetAll());
        public Task<ServiceResult<bool>> DeleteAsync(string code,long version){Deletes++;return Task.FromResult(map.Lines.Delete(new(code,"owner",version)));}
        public Task<ServiceResult<NodePositionProposal>> FindNodePositionAsync(string code,long version,LineNode[] nodes,int index,double x,double y)
        {Finds++;return Gate?.Task??Task.FromResult(new NodeEditService(map.Repository).FindPosition(new(code,"owner",version,nodes,index,x,y)));}
    }
    public TestContext TestContext{get;set;}=null!;
    [TestMethod]
    public async Task ELocksNearestNode_CancelKeepsDraft_ConfirmChangesOnlyThatNodeAndF6Saves()
    {
        var map=new TestMap();map.Add(100,100,110,100,120,100);var client=new Client(map);var editor=new LineEditor(client);await FrontAdditionTests.Edit(editor,new(115,100));
        var before=map.Snapshot();Assert.IsTrue(await editor.MoveNodeAsync(new(110.2,100)));Assert.AreEqual(1,editor.MovingNodeOrder);Assert.AreEqual(0,client.Finds);
        Assert.IsFalse(editor.AddPoint(new(130,100)));Assert.IsFalse(editor.InsertPoint(new(115,100)));Assert.IsFalse(editor.ToggleAdditionEnd());Assert.IsFalse(await editor.RemoveAsync(new(110,100)));Assert.IsFalse(await editor.ReverseDirectionAsync());Assert.IsFalse(await editor.CompleteAsync());Assert.AreEqual(0,client.Saves);
        Assert.IsTrue(editor.CancelNodeMove());Assert.IsNull(editor.MovingNodeOrder);Assert.AreEqual(new GridPoint(110,100),editor.DraftNodes[1]);Assert.AreEqual(before,map.Snapshot());
        await editor.MoveNodeAsync(new(110,100));Assert.IsTrue(await editor.MoveNodeAsync(new(110.3,110.2)));Assert.IsNull(editor.MovingNodeOrder);Assert.AreEqual(new GridPoint(110,110),editor.DraftNodes[1]);
        Assert.AreEqual(new GridPoint(100,100),editor.DraftNodes[0]);Assert.AreEqual(new GridPoint(120,100),editor.DraftNodes[2]);Assert.AreEqual(before,map.Snapshot());
        Assert.IsTrue(await editor.CompleteAsync());Assert.AreEqual(1,client.Saves);Assert.AreEqual(110,map.Lines.GetAll().Data!.Single().Nodes[1].Y);
    }
    [TestMethod]
    public async Task BusyAndFailedSearchKeepSelectedNodeAndDraft_RejectMalformedProposalAndAllowRetry()
    {
        var map=new TestMap();map.Add(100,100,110,100,120,100);var client=new Client(map);var editor=new LineEditor(client);await FrontAdditionTests.Edit(editor,new(115,100));await editor.MoveNodeAsync(new(110,100));
        client.Gate=new();var confirm=editor.MoveNodeAsync(new(110,110));Assert.IsTrue(editor.IsBusy);Assert.IsFalse(editor.CancelNodeMove());editor.Cancel();Assert.AreEqual(1,editor.MovingNodeOrder);
        Assert.IsFalse(await editor.MoveNodeAsync(new(110,120)));Assert.IsFalse(await editor.RemoveAsync(new(110,100)));Assert.IsFalse(await editor.CompleteAsync());
        client.Gate.SetResult(ServiceResult<NodePositionProposal>.Fail("ConnectionFailure","连接失败"));Assert.IsFalse(await confirm);Assert.IsFalse(editor.IsBusy);Assert.AreEqual(1,editor.MovingNodeOrder);
        client.Gate=new();confirm=editor.MoveNodeAsync(new(110,110));client.Gate.SetResult(ServiceResult<NodePositionProposal>.Ok(new(2,110,110),"wrong node"));Assert.IsFalse(await confirm);Assert.AreEqual(new GridPoint(110,100),editor.DraftNodes[1]);Assert.AreEqual(1,editor.MovingNodeOrder);
        client.Gate=null;Assert.IsTrue(await editor.MoveNodeAsync(new(110,110)));Assert.IsNull(editor.MovingNodeOrder);Assert.AreEqual(0,client.Saves);
    }
    [TestMethod]
    public async Task RDeletesNearestDraftNode_ProtectsEightDirectionsAndNodeOnlyButtonNeverDeletesRoad()
    {
        var map=new TestMap();map.Add(100,100,110,100,110,120);var client=new Client(map);var editor=new LineEditor(client);await FrontAdditionTests.Edit(editor,new(105,100));
        Assert.IsFalse(await editor.RemoveAsync(new(110,100)));Assert.AreEqual(3,editor.DraftNodes.Count);
        Assert.IsTrue(await editor.RemoveAsync(new(110,120)));Assert.AreEqual(2,editor.DraftNodes.Count);Assert.IsTrue(await editor.CompleteAsync());
        editor.ToggleSelection();editor.SelectClosest(new(105,100),1);Assert.IsFalse(await editor.RemoveAsync(new(105,100),nodesOnly:true));Assert.AreEqual(0,client.Deletes);Assert.AreEqual(1,map.Lines.GetAll().Data!.Length);
    }
    [STATestMethod,DataRow("keyboard"),DataRow("ime"),DataRow("button"),DataRow("menu"),TestCategory("Rendering")]
    public void ActualWindowE_ChangesMeaningByModeAndConfirmsWithEnter(string entry)
    {
        var map=new TestMap();map.Add(250000,250000,250010,250000,250020,250000);var client=new Client(map);
        var window=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");var editor=Editor(view);FrontAdditionTests.Edit(editor,new(250015,250000)).GetAwaiter().GetResult();view.ZoomAtCenter(8);var properties=0;view.LinePropertiesRequested+=(_,_)=>properties++;
            Pointer(view,new(250010.2,250000.2));
            if(entry=="button")((Button)window.FindName("EditNodeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if(entry=="menu")((MenuItem)window.FindName("EditNodeMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else Press(window,Key.E,entry=="ime");
            Assert.AreEqual(1,editor.MovingNodeOrder);Assert.AreEqual(0,properties);Assert.AreEqual(0,client.Finds);Assert.IsTrue(view.IsEditingLineNode);
            StringAssert.Contains(((Button)window.FindName("EditNodeButton")).Content.ToString()!,"确认");Pointer(view,new(250010.3,250010.2));
            if(entry=="keyboard")SavePreview(window,view);
            Press(window,Key.Enter);Assert.IsNull(editor.MovingNodeOrder);Assert.AreEqual(new GridPoint(250010,250010),editor.DraftNodes[1]);Assert.AreEqual(1,client.Finds);Assert.AreEqual(0,client.Saves);
            Pointer(view,new(250010,250010));Press(window,Key.E);Assert.AreEqual(1,editor.MovingNodeOrder);Press(window,Key.Escape);Assert.IsNull(editor.MovingNodeOrder);Assert.AreEqual(LineEditorMode.Drawing,editor.Mode);
            view.CancelEditing();view.ToggleLineSelection();editor.SelectClosest(new(250015,250000),1);Press(window,Key.E);Assert.AreEqual(1,properties);Assert.IsNull(editor.MovingNodeOrder);
        }
        finally{window.Close();}
    }
    [STATestMethod,DataRow("keyboard"),DataRow("button"),DataRow("menu")]
    public void ActualWindowR_DeletesOnlyNearestDraftNode(string entry)
    {
        var map=new TestMap();map.Add(100,100,110,100,120,100);var client=new Client(map);var window=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");var editor=Editor(view);FrontAdditionTests.Edit(editor,new(115,100)).GetAwaiter().GetResult();Pointer(view,new(110,100));
            if(entry=="button")((Button)window.FindName("RemoveNodeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if(entry=="menu")((MenuItem)window.FindName("RemoveNodeMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));else Press(window,Key.R);
            Assert.AreEqual(2,editor.DraftNodes.Count);CollectionAssert.AreEqual(new[]{100,120},editor.DraftNodes.Select(n=>n.X).ToArray());Assert.AreEqual(3,map.Lines.GetAll().Data!.Single().Nodes.Length);Assert.AreEqual(0,client.Deletes);
        }
        finally{window.Close();}
    }
    private static LineEditor Editor(CoordinateMapView view)=>(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static void Pointer(CoordinateMapView view,Point point)=>typeof(CoordinateMapView).GetProperty(nameof(CoordinateMapView.PointerPosition))!.SetValue(view,point);
    private static void Press(Window window,Key key,bool ime=false)
    {
        var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        if(ime)typeof(KeyEventArgs).GetMethod("MarkImeProcessed",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(e,null);window.RaiseEvent(e);Assert.IsTrue(e.Handled);
    }
    private void SavePreview(Window window,CoordinateMapView view)
    {
        view.InvalidateVisual();window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","node-edit-preview.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);
    }
}
