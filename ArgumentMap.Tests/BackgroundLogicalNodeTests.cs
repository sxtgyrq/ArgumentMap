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
public sealed class BackgroundLogicalNodeTests
{
    [TestMethod]
    public async Task JAllowsForegroundChanges_PreventsDuplicateStart_AndRestartsWithFreshCursorAfterCancellation()
    {
        var map=new TestMap();map.Add(20,50,80,50);
        var gate=new TaskCompletionSource<ServiceResult<LogicalNodeCalculationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cursors=new List<string?>();var client=new BackgroundCrossTests.Client(map){JStep=cursor=>{cursors.Add(cursor);return gate.Task;}};
        var editor=new LogicalNodeEditor();editor.Configure(client);var task=editor.CalculateAsync(()=>{});
        Assert.IsTrue(editor.IsCalculating);Assert.IsFalse(editor.IsBusy);Assert.IsFalse(await editor.CalculateAsync(()=>{}));
        Assert.IsTrue(await editor.CreateAsync(30.5m,50.5m,6));var node=editor.Nodes.Single();Assert.IsTrue(editor.BeginMove(node));Assert.IsTrue(await editor.MoveAsync(35.5m,50.5m));
        var message=editor.Message;editor.CancelCalculation();gate.SetResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(true,3,4,"AAAAAAAAAA"),"一项提交"));
        Assert.IsFalse(await task);Assert.IsFalse(editor.IsCalculating);Assert.AreEqual(message,editor.Message);Assert.AreEqual(35.5m,editor.Nodes.Single().X);
        client.JStep=cursor=>{cursors.Add(cursor);return Task.FromResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,1),"完成"));};
        Assert.IsTrue(await editor.CalculateAsync(()=>{}));Assert.AreEqual(2,cursors.Count);Assert.IsTrue(cursors.All(c=>c is null));
    }

    [TestMethod]
    public async Task JPausesForForegroundAndRejectsInvalidCursorResponseWithoutDiscardingNodes()
    {
        var map=new TestMap();var busy=true;var client=new BackgroundCrossTests.Client(map){JStep=_=>Task.FromResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(true,1,2),"错误响应"))};
        var editor=new LogicalNodeEditor();editor.Configure(client);editor.Notify("未保存编辑");var task=editor.CalculateAsync(()=>{},()=>busy);
        Assert.AreEqual(0,client.JCalculations);Assert.IsTrue(editor.IsCalculating);busy=false;Assert.IsFalse(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1,client.JCalculations);Assert.AreEqual("未保存编辑",editor.Message);StringAssert.Contains(editor.CalculationMessage,"更新 Server");
    }

    private sealed class Handler:HttpMessageHandler
    {
        internal readonly TaskCompletionSource First=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Order=[];private int units;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.AbsolutePath;lock(Order)Order.Add(path);
            if(path=="/CalculateLogicalNode")
            {
                var body=await request.Content!.ReadFromJsonAsync<CalculateLogicalNodeRequest>(cancellationToken:token);Assert.IsTrue(body!.Incremental);
                if(Interlocked.Increment(ref units)==1)await First.Task.WaitAsync(token);
                return new(HttpStatusCode.OK){Content=JsonContent.Create(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,0),"完成"))};
            }
            return new(HttpStatusCode.OK){Content=JsonContent.Create(ServiceResult<LogicalNodeSnapshot[]>.Ok([],"刷新"))};
        }
    }
    [TestMethod]
    public async Task QueuedForegroundRequestRunsBeforeNextJUnit()
    {
        using var handler=new Handler();using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var client=new HttpLineClient(http,new("owner","test"));
        var first=client.CalculateLogicalNodeStepAsync(null);var next=client.CalculateLogicalNodeStepAsync("AAAAAAAAAA");var read=client.GetLogicalNodesAsync();handler.First.SetResult();
        await Task.WhenAll(first,next,read).WaitAsync(TimeSpan.FromSeconds(5));CollectionAssert.AreEqual(new[]{"/CalculateLogicalNode","/GetLogicalNodes","/CalculateLogicalNode"},handler.Order);
    }

    [STATestMethod]
    public void JCanRunBesideK_WhileMovingLnSavingRoadAndKeepingNextUnsavedDraft()
    {
        var map=new TestMap();var road=map.Add(100,100,200,100);var ln=new LogicalNodeService(map.Repository).Create(new("owner",150.5m,100.5m,6,new())).Data!;
        var jGate=new TaskCompletionSource<ServiceResult<LogicalNodeCalculationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var kGate=new TaskCompletionSource<ServiceResult<CrossCalculationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client=new BackgroundCrossTests.Client(map){JStep=_=>jGate.Task,Step=()=>kGate.Task};
        var main=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};Task<bool>? j=null,k=null;
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();
            j=view.CalculateLogicalNodesAsync();k=view.CalculateCrossesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>view.IsCalculatingLogicalNodes&&view.IsCalculatingCrosses);Assert.IsFalse(view.IsSaving);Assert.IsFalse(view.CalculateLogicalNodesAsync().GetAwaiter().GetResult());
            Assert.IsTrue(view.BeginLogicalNodeMove(ln));Assert.IsTrue(view.MoveLogicalNodeAsync(new(160.5,100.5)).GetAwaiter().GetResult());
            var lines=(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
            view.ToggleLineSelection();lines.SelectLine(road.Code);Assert.IsTrue(view.EditSelectedLineAsync().GetAwaiter().GetResult());Assert.IsTrue(lines.AddPoint(new(220,100)));Assert.IsTrue(view.CompleteLineAsync().GetAwaiter().GetResult());
            Assert.IsTrue(view.NewLineAsync().GetAwaiter().GetResult());Assert.IsTrue(view.BeginDrawingAsync().GetAwaiter().GetResult());lines.AddPoint(new(300,300));lines.AddPoint(new(340,300));
            var draft=lines.DraftNodes.ToArray();var message=view.OperationMessage;
            jGate.SetResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,1),"J 完成"));kGate.SetResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,0),"K 完成"));
            LogicalNodeWindowEntryTests.DrainUntil(()=>j.IsCompleted&&k.IsCompleted);Assert.IsTrue(j.GetAwaiter().GetResult());Assert.IsTrue(k.GetAwaiter().GetResult());
            Assert.AreEqual(LineEditorMode.Drawing,lines.Mode);CollectionAssert.AreEqual(draft,lines.DraftNodes.ToArray());Assert.AreEqual(message,view.OperationMessage);
            StringAssert.Contains(((TextBlock)main.FindName("LogicalNodeCalculationStatus")).Text,"J 完成");
        }
        finally{main.Close();jGate.TrySetResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,0),"完成"));kGate.TrySetResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,0),"完成"));if(j is not null&&k is not null)LogicalNodeWindowEntryTests.DrainUntil(()=>j.IsCompleted&&k.IsCompleted);}
    }
}
