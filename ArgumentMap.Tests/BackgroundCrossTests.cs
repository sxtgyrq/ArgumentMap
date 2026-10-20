using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class BackgroundCrossTests
{
    internal sealed class Client(TestMap map):ILineClient,ILogicalNodeClient,ILogicalNodeStepClient,ILogicalNodeMoveClient,ICrossClient,IMapAreaMoveClient,IFlowDiagramClient
    {
        private readonly LogicalNodeMoveUiTests.Client foreground=new(map);
        internal Func<Task<ServiceResult<CrossCalculationProgress>>>? Step;
        internal Func<Task<ServiceResult<CrossSnapshot[]>>>? Read;
        internal int Calculations,Reads;
        internal Func<string?,Task<ServiceResult<LogicalNodeCalculationProgress>>>? JStep;
        internal Func<Task<ServiceResult<LogicalNodeSnapshot[]>>>? JRead;
        internal int JCalculations;
        public Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeStepAsync(string? afterCode)
        {JCalculations++;return JStep?.Invoke(afterCode)??Task.FromResult(new LogicalNodeService(map.Repository).CalculateStep(afterCode));}
        public Task<ServiceResult<CrossCalculationProgress>> CalculateCrossAsync(){Calculations++;return Step?.Invoke()??Task.FromResult(map.Crosses.Calculate());}
        public Task<ServiceResult<CrossSnapshot[]>> GetCrossesAsync(){Reads++;return Read?.Invoke()??Task.FromResult(map.Crosses.GetAll());}
        public Task<ServiceResult<CrossSnapshot>> SetCrossStateAsync(CrossKey key,long version,CrossState state)=>Task.FromResult(map.Crosses.Edit(new(key,"owner",version,state)));
        public Task<ServiceResult<LineSnapshot>> CreateAsync()=>foreground.CreateAsync();
        public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string code)=>foreground.StartDrawingAsync(code);
        public Task<ServiceResult<LineSnapshot>> SaveAsync(string code,long version,LineNode[] nodes)=>foreground.SaveAsync(code,version,nodes);
        public Task<ServiceResult<LineSnapshot[]>> GetAllAsync()=>foreground.GetAllAsync();
        public Task<ServiceResult<bool>> DeleteAsync(string code,long version)=>foreground.DeleteAsync(code,version);
        public Task<ServiceResult<LogicalNodeSnapshot[]>> GetLogicalNodesAsync()=>JRead?.Invoke()??foreground.GetLogicalNodesAsync();
        public Task<ServiceResult<LogicalNodeSnapshot>> CreateLogicalNodeAsync(decimal x,decimal y,int size,LogicalNodeProperties p)=>foreground.CreateLogicalNodeAsync(x,y,size,p);
        public Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code,long version,string value,string info)=>foreground.UpdateLogicalNodeAsync(code,version,value,info);
        public Task<ServiceResult<bool>> DeleteLogicalNodeAsync(string code,long version)=>foreground.DeleteLogicalNodeAsync(code,version);
        public Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeAsync()=>foreground.CalculateLogicalNodeAsync();
        public Task<ServiceResult<LogicalNodeSnapshot>> MoveLogicalNodeAsync(string code,long version,decimal x,decimal y)=>foreground.MoveLogicalNodeAsync(code,version,x,y);
        public Task<ServiceResult<MapAreaSelection>> SelectMapAreaAsync(MapAreaBounds bounds)=>Task.FromResult(new MapAreaMoveService(map.Repository).Select(new("owner",bounds)));
        public Task<ServiceResult<MapAreaSnapshot>> MoveMapAreaAsync(MapAreaSelection selection,int x,int y)=>Task.FromResult(new MapAreaMoveService(map.Repository).Move(new("owner",selection,x,y)));
        public Task<ServiceResult<MapAreaSnapshot>> DeleteMapAreaAsync(MapAreaSelection selection)=>Task.FromResult(new MapAreaMoveService(map.Repository).Delete(new("owner",selection)));
        public Task<ServiceResult<FlowDiagramResult>> PreviewFlowDiagramAsync(string text,decimal x,decimal y)=>Task.FromResult(new FlowDiagramService(map.Repository).Preview(new("owner",text,x,y)));
        public Task<ServiceResult<FlowDiagramResult>> ImportFlowDiagramAsync(string text,decimal x,decimal y)=>Task.FromResult(new FlowDiagramService(map.Repository).Import(new("owner",text,x,y)));
    }

    [TestMethod]
    public async Task CalculationIsSeparateFromForegroundBusy_RejectsRepeat_AndCancellationStopsAfterCurrentRequest()
    {
        var map=new TestMap();var gate=new TaskCompletionSource<ServiceResult<CrossCalculationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client=new Client(map){Step=()=>gate.Task};var editor=new CrossEditor();editor.ConfigureClient(client);
        var task=editor.CalculateAsync(()=>{});Assert.IsTrue(editor.IsCalculating);Assert.IsFalse(editor.IsBusy);Assert.AreEqual(1,client.Calculations);
        Assert.IsFalse(await editor.CalculateAsync(()=>{}));Assert.IsTrue(await editor.RefreshAsync());var foregroundMessage=editor.Message;
        editor.CancelCalculation();gate.SetResult(ServiceResult<CrossCalculationProgress>.Ok(new(true,10,0),"已提交一段"));
        Assert.IsFalse(await task);Assert.IsFalse(editor.IsCalculating);Assert.IsFalse(editor.IsBusy);Assert.AreEqual(1,client.Calculations);Assert.AreEqual(1,client.Reads);Assert.AreEqual(foregroundMessage,editor.Message);
        client.Step=()=>Task.FromResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,0),"完成"));Assert.IsTrue(await editor.CalculateAsync(()=>{}));
    }

    [TestMethod]
    public async Task DelayedFinalReadCannotOverwriteCrossEditedInForeground_OrItsSelectionAndMessage()
    {
        var map=new TestMap();map.Add(20,50,80,50);map.Add(50,20,50,80);map.Calculate();var original=map.Crosses.GetAll().Data!;
        var gate=new TaskCompletionSource<ServiceResult<CrossSnapshot[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client=new Client(map){Step=()=>Task.FromResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,1),"完成")),Read=()=>gate.Task};
        var editor=new CrossEditor();editor.ConfigureClient(client);editor.ReplaceCrosses(original);var task=editor.CalculateAsync(()=>{});
        editor.ToggleSelection();editor.SelectClosest(new(50,50),1);Assert.IsTrue(await editor.SetStateAsync(CrossState.Connected,false));var saved=editor.Selected;var message=editor.Message;
        gate.SetResult(ServiceResult<CrossSnapshot[]>.Ok(original,"过期读取"));Assert.IsTrue(await task);
        Assert.AreEqual(saved,editor.Selected);Assert.AreEqual(CrossState.Connected,editor.Crosses.Single().State);Assert.AreEqual(message,editor.Message);
    }

    [TestMethod]
    public async Task ForegroundBusyPausesNextSegment_AndFailureOnlyUpdatesCalculationStatus()
    {
        var map=new TestMap();var busy=true;var client=new Client(map){Step=()=>Task.FromResult(ServiceResult<CrossCalculationProgress>.Fail("InvalidGeometry","计算失败，未提交"))};
        var editor=new CrossEditor();editor.ConfigureClient(client);editor.Notify("正在编辑 LN");var task=editor.CalculateAsync(()=>{},()=>busy);
        Assert.IsTrue(editor.IsCalculating);Assert.AreEqual(0,client.Calculations);busy=false;Assert.IsFalse(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("正在编辑 LN",editor.Message);StringAssert.Contains(editor.CalculationMessage,"计算失败");Assert.IsFalse(editor.IsCalculating);Assert.IsFalse(editor.IsBusy);
    }

    private sealed class PriorityHandler(bool failForeground):HttpMessageHandler
    {
        internal readonly TaskCompletionSource First=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Order=[];
        private int calculations;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.AbsolutePath;lock(Order)Order.Add(path);
            if(path=="/CalculateCross")
            {if(Interlocked.Increment(ref calculations)==1)await First.Task.WaitAsync(token);return new(HttpStatusCode.OK){Content=JsonContent.Create(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,0),"完成"))};}
            if(failForeground)throw new HttpRequestException("Injected foreground network failure");
            return new(HttpStatusCode.OK){Content=JsonContent.Create(ServiceResult<LogicalNodeSnapshot>.Ok(new("AAAAAAAAAA","owner",1,50.5m,50.5m,6,new(){LogicLanguage="P"}),"移动完成"))};
        }
    }

    [TestMethod,DataRow(false),DataRow(true)]
    public async Task HttpForegroundRequestGoesBeforeAlreadyQueuedNextK_EvenAfterConnectionFailure(bool fail)
    {
        using var handler=new PriorityHandler(fail);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var client=new HttpLineClient(http,new("owner","test"));
        var first=client.CalculateCrossAsync();Assert.AreEqual(1,handler.Order.Count);
        var next=client.CalculateCrossAsync();var move=client.MoveLogicalNodeAsync("AAAAAAAAAA",0,50.5m,50.5m);Assert.AreEqual(1,handler.Order.Count);
        handler.First.SetResult();await Task.WhenAll(first,next,move).WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[]{"/CalculateCross","/MoveLogicalNode","/CalculateCross"},handler.Order);Assert.AreEqual(!fail,move.Result.Success);Assert.IsTrue(next.Result.Success);
        Assert.IsTrue((await client.CalculateCrossAsync()).Success,"The request gate must be released after failures.");
    }

    private static LineEditor Lines(CoordinateMapView view)=>(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static MainWindow Window(Client client)=>new(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};

    [STATestMethod]
    public void WhileKIsRunning_UserCanMoveLnAndSaveRoad_AndCompletionPreservesTheNextUnsavedDraft()
    {
        var map=new TestMap();var road=map.Add(100,100,200,100);var ln=new LogicalNodeService(map.Repository).Create(new("owner",150.5m,100.5m,6,new())).Data!;
        var gate=new TaskCompletionSource<ServiceResult<CrossCalculationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);var client=new Client(map);client.Step=()=>client.Calculations==1?gate.Task:Task.FromResult(map.Crosses.Calculate());
        var window=Window(client);Task<bool>? task=null;
        try
        {
            window.Show();var view=(CoordinateMapView)window.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();
            task=view.CalculateCrossesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>view.IsCalculatingCrosses);Assert.IsTrue(view.IsCalculatingCrosses);Assert.IsFalse(view.IsSaving);Assert.IsFalse(view.CalculateCrossesAsync().GetAwaiter().GetResult());Assert.AreEqual(1,client.Calculations);
            Assert.IsTrue(view.BeginLogicalNodeMove(ln));Assert.IsTrue(view.MoveLogicalNodeAsync(new(160.5,100.5)).GetAwaiter().GetResult());Assert.IsTrue(view.IsCalculatingCrosses);
            var editor=Lines(view);view.ToggleLineSelection();editor.SelectLine(road.Code);Assert.IsTrue(view.EditSelectedLineAsync().GetAwaiter().GetResult());Assert.IsTrue(editor.AddPoint(new(220,100)));Assert.IsTrue(view.CompleteLineAsync().GetAwaiter().GetResult());
            Assert.AreEqual(220,map.Lines.GetAll().Data!.Single().Nodes[^1].X);Assert.AreEqual(160.5m,new LogicalNodeService(map.Repository).GetAll().Data!.Single().X);
            Assert.IsTrue(view.NewLineAsync().GetAwaiter().GetResult());Assert.IsTrue(view.BeginDrawingAsync().GetAwaiter().GetResult());editor.AddPoint(new(300,300));editor.AddPoint(new(340,300));
            var code=editor.DraftCode;var draft=editor.DraftNodes.ToArray();var message=view.OperationMessage;Assert.IsFalse(view.CalculateCrossesAsync().GetAwaiter().GetResult());
            gate.SetResult(map.Crosses.Calculate());LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);Assert.IsTrue(task.GetAwaiter().GetResult());
            Assert.IsFalse(view.IsCalculatingCrosses);Assert.AreEqual(code,editor.DraftCode);CollectionAssert.AreEqual(draft,editor.DraftNodes.ToArray());Assert.AreEqual(LineEditorMode.Drawing,editor.Mode);Assert.AreEqual(message,view.OperationMessage);
            StringAssert.Contains(((TextBlock)window.FindName("CrossCalculationStatus")).Text,"完成");Assert.AreEqual(Visibility.Visible,((TextBlock)window.FindName("CrossCalculationStatus")).Visibility);
        }
        finally{window.Close();gate.TrySetResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,0),"完成"));if(task is not null)LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);}
    }

    [STATestMethod]
    public void KCanStartWithUnsavedRoadDraft_AndBackgroundFailureDoesNotInterruptOrOverwriteIt()
    {
        var map=new TestMap();var gate=new TaskCompletionSource<ServiceResult<CrossCalculationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);var client=new Client(map){Step=()=>gate.Task};var window=Window(client);Task<bool>? task=null;
        try
        {
            window.Show();var view=(CoordinateMapView)window.FindName("MapView");view.NewLineAsync().GetAwaiter().GetResult();view.BeginDrawingAsync().GetAwaiter().GetResult();var editor=Lines(view);editor.AddPoint(new(20,20));editor.AddPoint(new(40,20));var draft=editor.DraftNodes.ToArray();var message=view.OperationMessage;
            task=view.CalculateCrossesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>view.IsCalculatingCrosses);Assert.IsTrue(view.IsCalculatingCrosses);Assert.IsFalse(view.IsSaving);CollectionAssert.AreEqual(draft,editor.DraftNodes.ToArray());
            gate.SetResult(ServiceResult<CrossCalculationProgress>.Fail("DatabaseError","本段计算失败，已回滚"));LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);Assert.IsFalse(task.GetAwaiter().GetResult());
            Assert.AreEqual(message,view.OperationMessage);CollectionAssert.AreEqual(draft,editor.DraftNodes.ToArray());StringAssert.Contains(((TextBlock)window.FindName("CrossCalculationStatus")).Text,"已回滚");
        }
        finally{window.Close();gate.TrySetResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,0),"完成"));if(task is not null)LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);}
    }
}
