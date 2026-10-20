using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class FrontAdditionTests
{
    internal sealed class Client(TestMap map):ILineClient
    {
        internal TaskCompletionSource<ServiceResult<LineSnapshot>>? SaveGate;
        public Task<ServiceResult<LineSnapshot>> CreateAsync()=>Task.FromResult(ServiceResult<LineSnapshot>.Ok(map.Create(),""));
        public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string code)=>Task.FromResult(map.Lines.StartDrawing(new(code,"owner")));
        public Task<ServiceResult<LineSnapshot>> SaveAsync(string code,long version,LineNode[] nodes)=>SaveGate?.Task??Task.FromResult(map.Lines.CheckShapeIsOKThenSave(new(code,"owner",version,nodes)));
        public Task<ServiceResult<LineSnapshot[]>> GetAllAsync()=>Task.FromResult(map.Lines.GetAll());
        public Task<ServiceResult<bool>> DeleteAsync(string code,long version)=>Task.FromResult(map.Lines.Delete(new(code,"owner",version)));
    }
    internal static async Task Edit(LineEditor editor,Point onRoad)
    {
        Assert.IsTrue(await editor.RefreshLinesAsync());Assert.IsTrue(editor.ToggleSelection());editor.SelectClosest(onRoad,1);Assert.IsTrue(await editor.EditSelectedAsync());
    }
    [TestMethod,DataRow(1,0),DataRow(1,1),DataRow(0,1),DataRow(-1,1),DataRow(-1,0),DataRow(-1,-1),DataRow(0,-1),DataRow(1,-1)]
    public async Task FrontCandidateAndContinuousAddition_UseFirstNodeAndEightIntegerDirections(int dx,int dy)
    {
        var map=new TestMap();var road=map.Add(200,200,300,200);var editor=new LineEditor(new Client(map));await Edit(editor,new(250,200));
        var before=map.Snapshot();Assert.IsFalse(editor.AddFromStart);Assert.IsTrue(editor.ToggleAdditionEnd());
        var cursor=new Point(200+10*dx+.2,200+10*dy+.2);Assert.AreEqual(new GridPoint(200+10*dx,200+10*dy),editor.Candidate(cursor));
        Assert.IsTrue(editor.AddPoint(cursor));Assert.AreEqual(editor.DraftNodes[0],editor.AdditionAnchor);
        Assert.IsTrue(editor.AddPoint(new(200+20*dx+.2,200+20*dy+.2)));
        CollectionAssert.AreEqual(new[]{new GridPoint(200+20*dx,200+20*dy),new GridPoint(200+10*dx,200+10*dy),new GridPoint(200,200),new GridPoint(300,200)},editor.DraftNodes.ToArray());
        Assert.AreEqual(road.Code,editor.DraftCode);Assert.AreEqual(before,map.Snapshot());StringAssert.Contains(editor.ModeText,"前端加点");
        var count=editor.DraftNodes.Count;Assert.IsFalse(editor.AddPoint(editor.DraftNodes[0].ToPoint()));Assert.AreEqual(count,editor.DraftNodes.Count);
        Assert.IsNull(editor.Candidate(new(-1,200)));Assert.IsFalse(editor.AddPoint(new(double.NaN,200)));
    }
    [TestMethod]
    public async Task ToggleDoesNotReverseExistingNodes_CancelAndReenterResetToTail()
    {
        var map=new TestMap();var road=map.Add(100,100,110,100);var editor=new LineEditor(new Client(map));Assert.IsFalse(editor.ToggleAdditionEnd());
        await Edit(editor,new(105,100));Assert.IsTrue(editor.ToggleAdditionEnd());Assert.IsTrue(editor.AddPoint(new(90,100)));Assert.IsTrue(editor.ToggleAdditionEnd());
        Assert.IsTrue(editor.AddPoint(new(120,100)));CollectionAssert.AreEqual(new[]{90,100,110,120},editor.DraftNodes.Select(p=>p.X).ToArray());
        Assert.IsTrue(editor.ToggleAdditionEnd());var before=map.Snapshot();editor.Cancel();Assert.IsFalse(editor.AddFromStart);Assert.AreEqual(before,map.Snapshot());
        await Edit(editor,new(105,100));Assert.IsFalse(editor.AddFromStart);CollectionAssert.AreEqual(road.Nodes.Select(n=>new GridPoint(n.X,n.Y)).ToArray(),editor.DraftNodes.ToArray());
        editor.Cancel();Assert.IsTrue(await editor.NewLineAsync());Assert.IsTrue(await editor.BeginDrawingAsync());Assert.IsTrue(editor.ToggleAdditionEnd());
        Assert.IsTrue(editor.AddPoint(new(100.2,200.3)));Assert.IsTrue(editor.AddPoint(new(90.2,200.3)));Assert.AreEqual(new GridPoint(90,200),editor.DraftNodes[0]);
    }
    [TestMethod]
    public async Task SavingBlocksToggleAndFurtherNodes_FailureKeepsFrontDraftAndRetryResetsMode()
    {
        var map=new TestMap();map.Add(100,100,110,100);var client=new Client(map);var editor=new LineEditor(client);await Edit(editor,new(105,100));
        editor.ToggleAdditionEnd();editor.AddPoint(new(90,100));client.SaveGate=new();var saving=editor.CompleteAsync();Assert.IsTrue(editor.IsBusy);
        Assert.IsFalse(editor.ToggleAdditionEnd());Assert.IsTrue(editor.AddFromStart);Assert.IsFalse(editor.AddPoint(new(80,100)));Assert.AreEqual(3,editor.DraftNodes.Count);
        client.SaveGate.SetResult(ServiceResult<LineSnapshot>.Fail("DatabaseError","写入失败"));Assert.IsFalse(await saving);Assert.IsTrue(editor.AddFromStart);Assert.IsFalse(editor.IsBusy);
        client.SaveGate=null;Assert.IsTrue(await editor.CompleteAsync());Assert.IsFalse(editor.AddFromStart);Assert.AreEqual(LineEditorMode.Idle,editor.Mode);
        CollectionAssert.AreEqual(new[]{90,100,110},map.Lines.GetAll().Data!.Single().Nodes.Select(n=>n.X).ToArray());
    }
    [TestMethod,DataRow("ln-node"),DataRow("ln-diagonal"),DataRow("cross"),DataRow("acute"),DataRow("overlap"),DataRow("angle"),DataRow("occupied")]
    public async Task FrontAdditionF6_RejectsInvalidFinalLineWithoutChangingStoredMap(string kind)
    {
        var map=new TestMap();map.Add(100,100,110,100);var point=new Point(90,100);
        if(kind=="ln-node")Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",90.5m,100.5m,6,new())).Success);
        if(kind=="ln-diagonal"){Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",90.5m,90.5m,6,new())).Success);point=new(80,80);}
        if(kind=="cross"){map.Add(85,105,95,95);map.Add(85,95,95,105);map.Calculate();}
        if(kind=="acute")point=new(105,95);
        if(kind=="overlap")map.Add(85,100,95,100);
        if(kind=="angle")map.Add(85,105,95,95);
        if(kind=="occupied")map.Add(90,100,90,110);
        var before=map.Snapshot();var editor=new LineEditor(new Client(map));await Edit(editor,new(105,100));editor.ToggleAdditionEnd();Assert.IsTrue(editor.AddPoint(point));
        Assert.IsFalse(await editor.CompleteAsync());Assert.AreEqual(before,map.Snapshot());Assert.IsTrue(editor.AddFromStart);Assert.AreEqual(3,editor.DraftNodes.Count);
        var hint=kind switch{"ln-node"=>"节点","ln-diagonal"=>"45°","cross"=>"第三","acute"=>"锐角","overlap"=>"重叠","angle"=>"90°",_=>"占用"};StringAssert.Contains(editor.Message,hint);
    }
    [STATestMethod,DataRow("keyboard"),DataRow("numpad"),DataRow("ime"),DataRow("button"),DataRow("menu"),TestCategory("Rendering")]
    public void ActualMainWindowFiveEntry_UpdatesMenuButtonAndAAddsAtFront(string entry)
    {
        var map=new TestMap();map.Add(100,100,110,100);var client=new Client(map);
        var window=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");
            var editor=(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;Edit(editor,new(105,100)).GetAwaiter().GetResult();
            if(entry=="button")((Button)window.FindName("AdditionEndButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if(entry=="menu")((MenuItem)window.FindName("AdditionEndMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else
            {
                var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,entry=="numpad"?Key.NumPad5:Key.D5){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                if(entry=="ime")typeof(KeyEventArgs).GetMethod("MarkImeProcessed",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(key,null);window.RaiseEvent(key);Assert.IsTrue(key.Handled);
            }
            Assert.IsTrue(view.AddFromStart);Assert.IsTrue(((MenuItem)window.FindName("AdditionEndMenu")).IsChecked);StringAssert.Contains(((Button)window.FindName("AdditionEndButton")).Content.ToString()!,"前端");
            typeof(CoordinateMapView).GetProperty(nameof(CoordinateMapView.PointerPosition))!.SetValue(view,new Point(90,100));
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,Key.A){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            Assert.AreEqual(new GridPoint(90,100),editor.DraftNodes[0]);Assert.AreEqual(2,map.Lines.GetAll().Data!.Single().Nodes.Length);
            Assert.IsTrue(CoordinateMapView.IsEditorKey(Key.D5));Assert.IsTrue(CoordinateMapView.IsEditorKey(Key.NumPad5));
        }
        finally{window.Close();}
    }
}
